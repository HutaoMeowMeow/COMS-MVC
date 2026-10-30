using COMS_MVC.Data;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Tests for alert deduplication and concurrency protection (fix #1).
/// Uses an isolated in-memory Sqlite database so no production data is touched.
/// </summary>
public sealed class AlertDeduplicationTests
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

    private sealed class NullClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullClientProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => Proxy;
        public IClientProxy Client(string id) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> ids) => Proxy;
        public IClientProxy Group(string name) => Proxy;
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> excluded) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> names) => Proxy;
        public IClientProxy User(string id) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> ids) => Proxy;
    }

    private sealed class NullHubContext : IHubContext<MonitoringHub>
    {
        public IHubClients Clients { get; } = new NullHubClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeLookups : ILookupService
    {
        public Task<List<string>> GetValuesAsync(string category) => Task.FromResult(new List<string>());
        public Task<List<LookupOption>> GetOptionsAsync(string category) => Task.FromResult(new List<LookupOption>());
        public Task<bool> IsValidAsync(string category, string? value) => Task.FromResult(true);
        public void Invalidate(string? category = null) { }
    }

    private static (Ctx ctx, AlertService alerts) Setup()
    {
        var ctx = new Ctx();
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var hub = new NullHubContext();
        var notifications = new NotificationService(
            ctx.Db, NullLogger<NotificationService>.Instance, hub, mgr);
        var alerts = new AlertService(
            ctx.Db, NullLogger<AlertService>.Instance, hub, notifications, mgr, new FakeLookups());
        return (ctx, alerts);
    }

    private static Canal SeedCanal(ApplicationDbContext db)
    {
        var canal = new Canal
        {
            CanalName = "Dedup Canal",
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
        db.SaveChanges(); // generate CanalId before referencing it from the sensor
        db.Sensors.Add(new Sensor
        {
            SensorCode = "DEDUP-1",
            SensorType = "Water Level",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        });
        db.SaveChanges();
        return canal;
    }

    private static SensorReading CriticalReading(int canalId) => new()
    {
        SensorId = 1,
        CanalId = canalId,
        WaterLevel = 3.5,
        FlowRate = 1,
        DebrisLevel = 0,
        Turbidity = 1,
        Temperature = 28,
        RecordedAt = DateTime.UtcNow,
        IsSimulated = true
    };

    private static async Task<SensorReading> SaveReadingAsync(Ctx ctx, SensorReading reading)
    {
        ctx.Db.SensorReadings.Add(reading);
        await ctx.Db.SaveChangesAsync();
        return reading;
    }

    [Fact]
    public async Task RepeatedUnresolvedCondition_DoesNotCreateDuplicateAlerts()
    {
        var (ctx, alerts) = Setup();
        var canal = SeedCanal(ctx.Db);

        var first = await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));
        var second = await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));

        // Same unresolved condition within 30 minutes: refreshed, not duplicated.
        Assert.Single(first);
        Assert.Single(second);
        var count = await ctx.Db.ObstructionAlerts.CountAsync(a => a.CanalId == canal.CanalId);
        Assert.Equal(1, count);
        var alert = await ctx.Db.ObstructionAlerts.FirstAsync(a => a.CanalId == canal.CanalId);
        Assert.Equal("WaterLevelCritical", alert.AlertType);
        Assert.Equal("Critical", alert.Severity);
        Assert.Equal("Active", alert.Status);
    }

    [Fact]
    public async Task ResolvedConditionThatRecurs_CreatesNewAlert()
    {
        var (ctx, alerts) = Setup();
        var canal = SeedCanal(ctx.Db);

        await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));

        // Resolve the existing incident.
        var resolved = await ctx.Db.ObstructionAlerts.FirstAsync(a => a.CanalId == canal.CanalId);
        resolved.Status = "Resolved";
        resolved.ResolvedAt = DateTime.UtcNow;
        await ctx.Db.SaveChangesAsync();

        // Same condition recurs after resolution: a new alert is created.
        var second = await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));
        Assert.Single(second);
        var count = await ctx.Db.ObstructionAlerts.CountAsync(a => a.CanalId == canal.CanalId);
        Assert.Equal(2, count);
        Assert.All(second, a => Assert.Equal("Active", a.Status));
    }

    [Fact]
    public async Task GenuinelyNewIncident_CreatesSeparateAlert()
    {
        var (ctx, alerts) = Setup();
        var canal = SeedCanal(ctx.Db);

        var first = await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));

        // Different alert type / genuinely new condition on the same canal.
        var debrisReading = new SensorReading
        {
            SensorId = 1,
            CanalId = canal.CanalId,
            WaterLevel = 3.5,
            FlowRate = 1,
            DebrisLevel = 95,
            Turbidity = 1,
            Temperature = 28,
            RecordedAt = DateTime.UtcNow,
            IsSimulated = true
        };
        var second = await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, debrisReading));

        Assert.Single(first);
        Assert.Equal("WaterLevelCritical", first.Single().AlertType);

        var secondTypes = second.Select(a => a.AlertType).ToList();
        // High debris legitimately raises two independent alert types.
        Assert.Contains("ObstructionDetected", secondTypes);
        Assert.Contains("SevereObstruction", secondTypes);

        var totalCount = await ctx.Db.ObstructionAlerts.CountAsync(a => a.CanalId == canal.CanalId);
        Assert.Equal(3, totalCount);
        var allTypes = await ctx.Db.ObstructionAlerts
            .Where(a => a.CanalId == canal.CanalId)
            .Select(a => a.AlertType)
            .ToListAsync();
        Assert.Equal(1, allTypes.Count(t => t == "WaterLevelCritical"));
        Assert.Equal(1, allTypes.Count(t => t == "ObstructionDetected"));
        Assert.Equal(1, allTypes.Count(t => t == "SevereObstruction"));
    }

    [Fact]
    public async Task CriticalAlert_RemainsVisible()
    {
        var (ctx, alerts) = Setup();
        var canal = SeedCanal(ctx.Db);

        await alerts.CheckThresholdsAsync(await SaveReadingAsync(ctx, CriticalReading(canal.CanalId)));

        var active = await alerts.GetActiveAlertsAsync(canal.CanalId);
        Assert.NotEmpty(active);
        Assert.All(active, a => Assert.True(
            a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress"));
        Assert.Contains(active, a => a.Severity == "Critical");
    }
}
