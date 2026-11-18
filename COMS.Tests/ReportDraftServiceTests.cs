using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Draft persistence and ownership guarantees: one row per resident, values
/// survive updates, photos are stored server-side, invalid photos never destroy
/// the rest of the draft, and a user can never touch another user's draft.
/// </summary>
public sealed class ReportDraftServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly string _webRoot;
    private readonly FakeWebHostEnvironment _env;

    public ReportDraftServiceTests()
    {
        _conn.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _webRoot = Path.Combine(Path.GetTempPath(), "coms-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);
        _env = new FakeWebHostEnvironment { WebRootPath = _webRoot, ContentRootPath = _webRoot };
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        try { Directory.Delete(_webRoot, true); } catch { }
    }

    private ReportDraftService Service() =>
        new(_db, _env, NullLogger<ReportDraftService>.Instance);

    private async Task<int> CreateUserIdAsync(string userName)
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.com",
            FullName = userName,
            EmailConfirmed = true
        };
        var result = await mgr.CreateAsync(user, "Secret@123!");
        Assert.True(result.Succeeded);
        return user.Id;
    }

    private static ReportDraftInput Input(string title, string? description = null) => new()
    {
        ReportType = "Garbage / Debris",
        Title = title,
        Description = description,
        Location = "Barangay Centro",
        Latitude = 10.3157,
        Longitude = 123.8854
    };

    private static byte[] PngBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(32, 32, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(new SkiaSharp.SKColor(40, 90, 140));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 80);
        return data.ToArray();
    }

    private static IFormFile FormFile(byte[] bytes, string name, string contentType)
    {
        var headers = new HeaderDictionary { { "Content-Type", contentType } };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Photo", name)
        {
            Headers = headers
        };
    }

    [Fact]
    public async Task Save_RepeatedAutosaves_UpdateSingleDraftRow()
    {
        var userId = await CreateUserIdAsync("drafter1");
        var service = Service();

        var first = await service.SaveAsync(userId, Input("Blocked canal"), null, false);
        Assert.NotNull(first.Draft);
        Assert.Null(first.Warning);

        var second = await service.SaveAsync(userId, Input("Blocked canal", "Water rising"), null, false);
        Assert.NotNull(second.Draft);
        Assert.Equal(first.Draft!.ReportDraftId, second.Draft!.ReportDraftId);

        var rows = await _db.ReportDrafts.Where(d => d.UserId == userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Water rising", rows[0].Description);
        Assert.Equal(10.3157, rows[0].Latitude);
    }

    [Fact]
    public async Task Save_WithValidPhoto_PersistsServerSideAttachment()
    {
        var userId = await CreateUserIdAsync("drafter2");
        var service = Service();

        var result = await service.SaveAsync(
            userId, Input("Canal overflow photo"), FormFile(PngBytes(), "canal.png", "image/png"), false);

        Assert.NotNull(result.Draft);
        Assert.Null(result.Warning);
        var path = result.Draft!.PhotoPath;
        Assert.NotNull(path);
        Assert.StartsWith("draft_", path);

        var diskPath = ReportDraftService.ResolveDraftPhotoDiskPath(_webRoot, path);
        Assert.NotNull(diskPath);
        Assert.True(File.Exists(diskPath));
    }

    [Fact]
    public async Task Save_InvalidPhoto_KeepsTextAndReportsWarning()
    {
        var userId = await CreateUserIdAsync("drafter3");
        var service = Service();

        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0x00 };
        var result = await service.SaveAsync(
            userId, Input("Drainage blocked"), FormFile(gif, "fake.gif", "image/gif"), false);

        Assert.NotNull(result.Draft);
        Assert.NotNull(result.Warning);
        Assert.Contains("JPG", result.Warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Drainage blocked", result.Draft!.Title);
        Assert.Null(result.Draft.PhotoPath);
    }

    [Fact]
    public async Task Delete_IsScopedToOwner()
    {
        var ownerId = await CreateUserIdAsync("drafter4");
        var otherId = await CreateUserIdAsync("drafter5");
        var service = Service();

        var saved = await service.SaveAsync(ownerId, Input("Owner draft"), null, false);
        var draftId = saved.Draft!.ReportDraftId;

        Assert.False(await service.DeleteAsync(draftId, otherId));
        Assert.True(await _db.ReportDrafts.AnyAsync(d => d.ReportDraftId == draftId));

        Assert.True(await service.DeleteAsync(draftId, ownerId));
        Assert.False(await _db.ReportDrafts.AnyAsync(d => d.ReportDraftId == draftId));
    }

    [Fact]
    public async Task EmptyDraft_IsNotPersisted()
    {
        var userId = await CreateUserIdAsync("drafter6");
        var service = Service();

        var result = await service.SaveAsync(userId, new ReportDraftInput(), null, false);

        Assert.Null(result.Draft);
        Assert.False(await _db.ReportDrafts.AnyAsync(d => d.UserId == userId));
    }

    [Fact]
    public async Task GetForUser_ReturnsOnlyThatUsersDraft()
    {
        var a = await CreateUserIdAsync("drafter7");
        var b = await CreateUserIdAsync("drafter8");
        var service = Service();

        await service.SaveAsync(a, Input("A draft"), null, false);
        await service.SaveAsync(b, Input("B draft"), null, false);

        var draftA = await service.GetForUserAsync(a);
        Assert.NotNull(draftA);
        Assert.Equal("A draft", draftA!.Title);
    }
}

