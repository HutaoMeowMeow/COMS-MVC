using System.Data.Common;
using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace COMS.Tests;

/// <summary>
/// Before/after measurement for the N+1 optimization (code issue #2):
/// counts actual database round-trips for the old per-canal loop pattern
/// versus the new single-query batched pattern, over 10 canals.
/// </summary>
public sealed class QueryCountTests
{
    private readonly ITestOutputHelper _out;

    public QueryCountTests(ITestOutputHelper output) => _out = output;

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        public int Commands;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken ct = default)
        {
            Commands++;
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    private static async Task<ApplicationDbContext> SeedAsync(
        SqliteConnection conn, CountingInterceptor counter, int canals = 10)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(conn)
            .AddInterceptors(counter)
            .Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        for (var i = 0; i < canals; i++)
        {
            var canal = new Canal
            {
                CanalName = $"Canal {i}",
                Location = "Cebu",
                Barangay = "Centro",
                City = "Cebu City",
                Latitude = 10.3,
                Longitude = 123.9,
                NormalWaterLevel = 1,
                WarningWaterLevel = 2,
                CriticalWaterLevel = 3,
                Status = "Normal",
                CreatedAt = DateTime.UtcNow
            };
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            var sensor = new Sensor
            {
                SensorCode = $"QC-{i}",
                SensorType = "WaterLevel",
                CanalId = canal.CanalId,
                Latitude = 10.3,
                Longitude = 123.9,
                Status = "Online"
            };
            db.Sensors.Add(sensor);
            await db.SaveChangesAsync();
            db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensor.SensorId,
                CanalId = canal.CanalId,
                WaterLevel = 1.0,
                FlowRate = 3,
                DebrisLevel = 5,
                Turbidity = 2,
                Temperature = 28,
                RecordedAt = DateTime.UtcNow,
                IsSimulated = true
            });
            db.FloodRiskAssessments.Add(new FloodRiskAssessment
            {
                CanalId = canal.CanalId,
                RiskScore = 5,
                RiskLevel = "Low",
                AssessmentDetails = "seed",
                ModelVersion = "Rule-Based v1.0",
                AssessmentDate = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddHours(6)
            });
            await db.SaveChangesAsync();
        }
        counter.Commands = 0; // Only measure the read patterns below.
        return db;
    }

    [Fact]
    public async Task BatchedLatestLookups_UseConstantRoundTrips()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var counter = new CountingInterceptor();
        using var db = await SeedAsync(conn, counter);
        var ids = await db.Canals.Select(c => c.CanalId).ToListAsync();
        counter.Commands = 0;

        // OLD pattern: one query per canal per lookup (2N round-trips).
        var oldCount = 0;
        foreach (var id in ids)
        {
            await db.FloodRiskAssessments.Where(a => a.CanalId == id)
                .OrderByDescending(a => a.AssessmentDate).FirstOrDefaultAsync();
            oldCount++;
            await db.SensorReadings.Where(sr => sr.CanalId == id)
                .OrderByDescending(sr => sr.RecordedAt).FirstOrDefaultAsync();
            oldCount++;
        }
        var oldCommands = counter.Commands;
        _out.WriteLine($"OLD pattern: {oldCommands} round-trips for {ids.Count} canals.");

        // NEW pattern: batched GroupBy (as in Dashboard/SensorReadings).
        counter.Commands = 0;
        var assessments = (await db.FloodRiskAssessments
            .Where(a => ids.Contains(a.CanalId))
            .GroupBy(a => a.CanalId)
            .Select(g => g.OrderByDescending(a => a.AssessmentDate).FirstOrDefault())
            .ToListAsync()).Where(a => a != null).ToList();
        var readings = (await db.SensorReadings
            .Where(sr => ids.Contains(sr.CanalId))
            .GroupBy(sr => sr.CanalId)
            .Select(g => g.OrderByDescending(sr => sr.RecordedAt).FirstOrDefault())
            .ToListAsync()).Where(sr => sr != null).ToList();
        var newCommands = counter.Commands;
        _out.WriteLine($"NEW pattern: {newCommands} round-trips for {ids.Count} canals.");

        Assert.Equal(ids.Count, assessments.Count);
        Assert.Equal(ids.Count, readings.Count);
        Assert.Equal(2 * ids.Count, oldCommands); // 2N baseline confirmed.
        Assert.True(newCommands <= 4, $"Batched pattern should use O(1) round-trips, used {newCommands}.");
    }

    [Fact]
    public async Task GetLatestForCanalsAsync_SingleRoundTrip()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var counter = new CountingInterceptor();
        using var db = await SeedAsync(conn, counter);
        var svc = new FloodRiskService(db, NullLogger<FloodRiskService>.Instance);
        var ids = await db.Canals.Select(c => c.CanalId).ToListAsync();
        counter.Commands = 0;

        var latest = await svc.GetLatestForCanalsAsync(ids);

        _out.WriteLine($"GetLatestForCanalsAsync: {counter.Commands} round-trip(s) for {ids.Count} canals.");
        Assert.Equal(ids.Count, latest.Count);
        Assert.True(counter.Commands <= 2, $"Expected O(1) round-trips, used {counter.Commands}.");
    }
}
