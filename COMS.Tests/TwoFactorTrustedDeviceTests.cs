using COMS_MVC.Data;
using COMS_MVC.Helpers;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

public sealed class TwoFactorTrustedDeviceTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

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

    private static TrustedDeviceService CreateService(
        ApplicationDbContext db, TimeProvider time, int trustedDays = 30)
    {
        return new TrustedDeviceService(
            db,
            NullLogger<TrustedDeviceService>.Instance,
            Options.Create(new TwoFactorOptions { TrustedDeviceDays = trustedDays }),
            time);
    }

    private static async Task<ApplicationUser> AddUserAsync(ApplicationDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = "trusted@example.com",
            NormalizedUserName = "TRUSTED@EXAMPLE.COM",
            Email = "trusted@example.com",
            NormalizedEmail = "TRUSTED@EXAMPLE.COM",
            EmailConfirmed = true,
            FullName = "Trusted User",
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task TrustedDevice_StoresOnlyHash_AndExpiresAfterThirtyDays()
    {
        using var ctx = new Ctx();
        var user = await AddUserAsync(ctx.Db);
        var time = new MutableTimeProvider();
        var service = CreateService(ctx.Db, time);
        var token = TrustedDeviceCookie.NewToken();

        var device = await service.CreateAsync(
            user.Id, "Test browser", "UnitTest/1.0", token);

        Assert.NotEqual(token, device.TokenHash);
        Assert.DoesNotContain(token, device.TokenHash);
        Assert.True(await service.HasValidTrustedDeviceAsync(user.Id, token));
        Assert.False(await service.HasValidTrustedDeviceAsync(user.Id, "not-the-token"));

        time.UtcNow = time.UtcNow.AddDays(31);

        Assert.False(await service.HasValidTrustedDeviceAsync(user.Id, token));
    }

    [Fact]
    public async Task TrustedDevice_CanBeRevokedIndividuallyAndAllAtOnce()
    {
        using var ctx = new Ctx();
        var user = await AddUserAsync(ctx.Db);
        var service = CreateService(ctx.Db, new MutableTimeProvider());

        var firstToken = TrustedDeviceCookie.NewToken();
        var secondToken = TrustedDeviceCookie.NewToken();
        var first = await service.CreateAsync(user.Id, "First", null, firstToken);
        await service.CreateAsync(user.Id, "Second", null, secondToken);

        Assert.Equal(2, (await service.GetDevicesAsync(user.Id)).Count);
        Assert.True(await service.RevokeAsync(user.Id, first.TrustedDeviceId));
        Assert.False(await service.HasValidTrustedDeviceAsync(user.Id, firstToken));
        Assert.True(await service.HasValidTrustedDeviceAsync(user.Id, secondToken));

        Assert.True(await service.RevokeAllAsync(user.Id));
        Assert.Empty(await service.GetDevicesAsync(user.Id));
        Assert.False(await service.HasValidTrustedDeviceAsync(user.Id, secondToken));
    }

    [Fact]
    public void Options_ClampTrustWindowToThirtyDays()
    {
        var options = new TwoFactorOptions
        {
            TrustedDeviceDays = 365,
            CleanupIntervalMinutes = 0
        };

        Assert.Equal(TwoFactorOptions.MaxTrustedDeviceDays, options.TrustedDeviceDays);
        Assert.Equal(TimeSpan.FromDays(30), options.TrustedDeviceLifetime);
        Assert.Equal(1, options.CleanupIntervalMinutes);
    }

    [Fact]
    public async Task Create_TruncatesDeviceFieldsAndUsesServerLifetime()
    {
        using var ctx = new Ctx();
        var user = await AddUserAsync(ctx.Db);
        var time = new MutableTimeProvider();
        var service = CreateService(ctx.Db, time, trustedDays: 365);

        var device = await service.CreateAsync(
            user.Id,
            new string('n', 250),
            new string('u', 600),
            TrustedDeviceCookie.NewToken());

        Assert.Equal(200, device.DeviceName.Length);
        Assert.Equal(500, device.UserAgent!.Length);
        Assert.Equal(time.UtcNow.UtcDateTime.AddDays(30), device.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetActiveDevices_HidesExpiredRows_AndPurgeRemovesThem()
    {
        using var ctx = new Ctx();
        var user = await AddUserAsync(ctx.Db);
        var time = new MutableTimeProvider();
        var service = CreateService(ctx.Db, time);

        await service.CreateAsync(user.Id, "Old browser", null, TrustedDeviceCookie.NewToken());
        time.UtcNow = time.UtcNow.AddDays(31);
        await service.CreateAsync(user.Id, "Current browser", null, TrustedDeviceCookie.NewToken());

        var active = await service.GetActiveDevicesAsync(user.Id);
        Assert.Single(active);
        Assert.Equal("Current browser", active[0].DeviceName);
        Assert.Equal(2, (await service.GetDevicesAsync(user.Id)).Count);

        Assert.Equal(1, await service.PurgeExpiredAsync());
        Assert.Single(await service.GetDevicesAsync(user.Id));
        Assert.Equal(0, await service.PurgeExpiredAsync());
    }

    [Fact]
    public async Task Touch_UpdatesLastSeenOnlyForTheMatchingToken()
    {
        using var ctx = new Ctx();
        var user = await AddUserAsync(ctx.Db);
        var time = new MutableTimeProvider();
        var service = CreateService(ctx.Db, time);
        var token = TrustedDeviceCookie.NewToken();
        var device = await service.CreateAsync(user.Id, "Browser", null, token);

        time.UtcNow = time.UtcNow.AddHours(3);
        await service.TouchAsync(user.Id, token);
        await service.TouchAsync(user.Id, "wrong-token");

        var stored = await ctx.Db.TrustedDevices.SingleAsync(d => d.TrustedDeviceId == device.TrustedDeviceId);
        Assert.Equal(time.UtcNow.UtcDateTime, stored.LastSeenAtUtc);
    }
}
