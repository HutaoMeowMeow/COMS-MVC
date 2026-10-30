using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Tests for the 5-hour alert retention cleanup. Uses an isolated in-memory
/// Sqlite database so no production data is ever touched.
/// </summary>
public sealed class AlertRetentionTests
{
    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ApplicationDbContext Db { get; }

        public Ctx()
        {
            Conn.Open();
            Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Conn).Options);
            Db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Db.Dispose();
            Conn.Dispose();
        }
    }

    /// <summary>Minimal scope factory that always hands out the test DbContext.</summary>
    private sealed class TestScopeProvider : IServiceProvider
    {
        private readonly ApplicationDbContext _db;
        public TestScopeProvider(ApplicationDbContext db) => _db = db;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ApplicationDbContext) ? _db : null;
    }

    private sealed class TestScopeFactory : IServiceScopeFactory, IServiceScope
    {
        private readonly ApplicationDbContext _db;
        public TestScopeFactory(ApplicationDbContext db) => _db = db;
        public IServiceProvider ServiceProvider => new TestScopeProvider(_db);
        public IServiceScope CreateScope() => this;
        public void Dispose() { }
    }

    private static Canal SeedCanal(ApplicationDbContext db)
    {
        var canal = new Canal
        {
            CanalName = "Retention Canal",
            Location = "Cebu Test",
            Barangay = "Centro",
            City = "Cebu City",
            Latitude = 10.3,
            Longitude = 123.9,
            NormalWaterLevel = 1.0,
            WarningWaterLevel = 2.0,
            CriticalWaterLevel = 3.0,
            Status = "Normal",
            CreatedAt = DateTime.UtcNow
        };
        db.Canals.Add(canal);
        db.SaveChanges();
        return canal;
    }

    private static ObstructionAlert MakeAlert(int canalId, DateTime detectedAt, string status = "Active") => new()
    {
        CanalId = canalId,
        AlertType = "WaterLevelCritical",
        Severity = "Critical",
        Title = "Test alert",
        Description = "Test alert description",
        ObstructionType = "Possible Blockage",
        WaterLevel = 3.5,
        DetectedAt = detectedAt,
        Status = status
    };

    private static AlertRetentionService BuildService(Ctx ctx, AlertRetentionOptions? options = null)
    {
        return new AlertRetentionService(
            new TestScopeFactory(ctx.Db),
            NullLogger<AlertRetentionService>.Instance,
            Options.Create(options ?? new AlertRetentionOptions()));
    }

    [Fact]
    public void DefaultRetention_IsFiveHours()
    {
        var options = new AlertRetentionOptions();
        Assert.Equal(TimeSpan.FromHours(5), options.Retention);

        var cutoff = options.CutoffUtc;
        // Cutoff must be ~5h in the PAST (the bug this replaces used +5h).
        var expected = DateTime.UtcNow.AddHours(-5);
        Assert.True(cutoff >= expected.AddSeconds(-10) && cutoff <= expected.AddSeconds(10));
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyAlertsOlderThanRetention()
    {
        using var ctx = new Ctx();
        var canal = SeedCanal(ctx.Db);

        var expiredActive = MakeAlert(canal.CanalId, DateTime.UtcNow.AddHours(-6));
        var freshActive = MakeAlert(canal.CanalId, DateTime.UtcNow.AddMinutes(-30));
        var expiredResolved = MakeAlert(canal.CanalId, DateTime.UtcNow.AddHours(-7), status: "Resolved");
        ctx.Db.ObstructionAlerts.AddRange(expiredActive, freshActive, expiredResolved);
        await ctx.Db.SaveChangesAsync();

        await BuildService(ctx).CleanupOldAlertsAsync(CancellationToken.None);

        var remaining = await ctx.Db.ObstructionAlerts.ToListAsync();
        var kept = Assert.Single(remaining);
        Assert.Equal(freshActive.ObstructionAlertId, kept.ObstructionAlertId);
        Assert.Equal("Active", kept.Status);
    }

    [Fact]
    public async Task Cleanup_RemovesLinkedNotificationsButKeepsOthers()
    {
        using var ctx = new Ctx();
        var canal = SeedCanal(ctx.Db);

        // Notifications carry an FK to AspNetUsers — seed a real user.
        var user = new ApplicationUser
        {
            UserName = "retentionuser",
            Email = "retention@example.com",
            FullName = "Retention Tester",
            EmailConfirmed = true
        };
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var created = await mgr.CreateAsync(user, "Secret@123!");
        Assert.True(created.Succeeded);
        var userId = user.Id;

        var expired = MakeAlert(canal.CanalId, DateTime.UtcNow.AddHours(-6));
        var fresh = MakeAlert(canal.CanalId, DateTime.UtcNow.AddMinutes(-10));
        ctx.Db.ObstructionAlerts.AddRange(expired, fresh);
        await ctx.Db.SaveChangesAsync();

        var linkedToExpired = new Notification
        {
            UserId = userId,
            Title = "Old alert",
            Message = "Linked to the expired alert",
            Type = "Alert",
            RelatedAlertId = expired.ObstructionAlertId,
            IsRead = false,
            CreatedAt = DateTime.UtcNow.AddHours(-6)
        };
        var linkedToFresh = new Notification
        {
            UserId = userId,
            Title = "Fresh alert",
            Message = "Linked to the fresh alert",
            Type = "Alert",
            RelatedAlertId = fresh.ObstructionAlertId,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        var unrelated = new Notification
        {
            UserId = userId,
            Title = "Report update",
            Message = "Unrelated report notification",
            Type = "Report",
            IsRead = false,
            CreatedAt = DateTime.UtcNow.AddHours(-6)
        };
        ctx.Db.Notifications.AddRange(linkedToExpired, linkedToFresh, unrelated);
        await ctx.Db.SaveChangesAsync();

        await BuildService(ctx).CleanupOldAlertsAsync(CancellationToken.None);

        var notifications = await ctx.Db.Notifications.ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.DoesNotContain(notifications, n => n.Title == "Old alert");
        Assert.Contains(notifications, n => n.Title == "Fresh alert");
        Assert.Contains(notifications, n => n.Title == "Report update");

        // The surviving alert keeps its notification link intact.
        var freshNotification = notifications.Single(n => n.Title == "Fresh alert");
        Assert.Equal(fresh.ObstructionAlertId, freshNotification.RelatedAlertId);
    }

    [Fact]
    public async Task Cleanup_IsIdempotent_WhenNothingExpired()
    {
        using var ctx = new Ctx();
        var canal = SeedCanal(ctx.Db);
        ctx.Db.ObstructionAlerts.Add(MakeAlert(canal.CanalId, DateTime.UtcNow.AddMinutes(-5)));
        await ctx.Db.SaveChangesAsync();

        var service = BuildService(ctx);
        await service.CleanupOldAlertsAsync(CancellationToken.None);
        await service.CleanupOldAlertsAsync(CancellationToken.None);

        Assert.Equal(1, await ctx.Db.ObstructionAlerts.CountAsync());
    }

    [Fact]
    public async Task Cleanup_RespectsConfiguredRetentionWindow()
    {
        using var ctx = new Ctx();
        var canal = SeedCanal(ctx.Db);

        var twoHoursOld = MakeAlert(canal.CanalId, DateTime.UtcNow.AddHours(-2));
        var fiveHoursOld = MakeAlert(canal.CanalId, DateTime.UtcNow.AddHours(-5).AddMinutes(-1));
        ctx.Db.ObstructionAlerts.AddRange(twoHoursOld, fiveHoursOld);
        await ctx.Db.SaveChangesAsync();

        // 1-hour window: both alerts are expired.
        await BuildService(ctx, new AlertRetentionOptions { RetentionHours = 1 })
            .CleanupOldAlertsAsync(CancellationToken.None);

        Assert.Equal(0, await ctx.Db.ObstructionAlerts.CountAsync());
    }
}
