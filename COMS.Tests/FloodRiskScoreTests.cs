using System.Reflection;
using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the FloodRisk scoring formula (fix #6):
/// normal / warning / critical / boundary / invalid readings, and proof
/// that a single water-level condition is never counted twice.
/// </summary>
public sealed class FloodRiskScoreTests
{
    private static readonly MethodInfo ComputeRiskScore = typeof(FloodRiskService)
        .GetMethod("ComputeRiskScore", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetRiskLevel = typeof(FloodRiskService)
        .GetMethod("GetRiskLevel", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static int Score(Canal canal, SensorReading? reading, int alerts = 0, int reports = 0)
        => (int)ComputeRiskScore.Invoke(null, new object?[] { canal, reading, alerts, reports })!;

    private static string Level(int score)
        => (string)GetRiskLevel.Invoke(null, new object[] { score })!;

    private static Canal TestCanal() => new()
    {
        CanalId = 1,
        CanalName = "Test Canal",
        Location = "Cebu",
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

    private static SensorReading Reading(double water, double flow = 5.0, double debris = 0.0) => new()
    {
        SensorReadingId = 1,
        SensorId = 1,
        CanalId = 1,
        WaterLevel = water,
        FlowRate = flow,
        DebrisLevel = debris,
        Turbidity = 1.0,
        Temperature = 28.0,
        RecordedAt = DateTime.UtcNow,
        IsSimulated = true
    };

    [Fact]
    public void NormalReading_ScoresNearZero()
    {
        var score = Score(TestCanal(), Reading(water: 1.0, debris: 10.0));
        // Water below warning (0) + normal flow (0) + 10% debris (2).
        Assert.Equal(2, score);
        Assert.Equal("Low", Level(score));
    }

    [Fact]
    public void WarningExceedance_AddsProportionalPoints()
    {
        // (2.5 / 2.0 - 1.0) * 30 = 7.5 -> (int) 7.
        Assert.Equal(7, Score(TestCanal(), Reading(water: 2.5)));
    }

    [Fact]
    public void CriticalExceedance_ReplacesWarningPoints_NotAdded()
    {
        // Water 4.5 with warning=2.0, critical=3.0 (exact in binary FP):
        // critical branch: (4.5/3.0 - 1.0) * 30 = 15.
        // If warning were ALSO counted: (4.5/2.0-1)*30 = 37 extra -> 52.
        Assert.Equal(15, Score(TestCanal(), Reading(water: 4.5)));
    }

    [Fact]
    public void Boundary_WaterExactlyAtWarning_ScoresZeroWaterPoints()
    {
        Assert.Equal(0, Score(TestCanal(), Reading(water: 2.0)));
    }

    [Fact]
    public void Boundary_WaterExactlyAtCritical_UsesWarningBranchOnly()
    {
        // criticalRatio == 1.0 is not > 1.0, so the warning branch applies:
        // (3.0 / 2.0 - 1.0) * 30 = 15.
        Assert.Equal(15, Score(TestCanal(), Reading(water: 3.0)));
    }

    [Fact]
    public void Invalid_NegativeReading_NeverScoresNegative()
    {
        var score = Score(TestCanal(), Reading(water: -1.0));
        Assert.Equal(0, score);
    }

    [Fact]
    public void NoReading_AddsElevatedMonitoringPenalty()
    {
        Assert.Equal(10, Score(TestCanal(), null));
    }

    [Fact]
    public void SlowFlow_AddsFifteenPoints()
    {
        Assert.Equal(15, Score(TestCanal(), Reading(water: 1.0, flow: 0.5)));
    }

    [Fact]
    public void AlertsAndReports_AreCapped()
    {
        // alerts: min(5*10,20)=20; reports: min(9*5,10)=10; no-reading base 10.
        Assert.Equal(40, Score(TestCanal(), null, alerts: 5, reports: 9));
    }

    [Fact]
    public void ExtremeInputs_StayWithinBounds()
    {
        // Max achievable: water 30 + slow flow 15 + debris 20 + alerts 20
        // + reports 10 = 95; the Math.Min(score,100) cap is defensive.
        var canal = TestCanal();
        var reading = Reading(water: 99.0, flow: 0.0, debris: 100.0);
        var score = Score(canal, reading, alerts: 10, reports: 10);
        Assert.Equal(95, score);
        Assert.InRange(score, 0, 100);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(25, "Low")]
    [InlineData(26, "Moderate")]
    [InlineData(50, "Moderate")]
    [InlineData(51, "High")]
    [InlineData(75, "High")]
    [InlineData(76, "Critical")]
    [InlineData(100, "Critical")]
    public void RiskLevel_Boundaries(int score, string expected)
    {
        Assert.Equal(expected, Level(score));
    }

    [Fact]
    public async Task CalculateRiskAsync_PersistsAssessment_EndToEnd()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(conn)
            .Options;
        using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var canal = TestCanal();
        canal.CanalId = 0;
        db.Canals.Add(canal);
        await db.SaveChangesAsync();
        var sensor = new Sensor
        {
            SensorCode = "TEST-001",
            SensorType = "WaterLevel",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        };
        db.Sensors.Add(sensor);
        await db.SaveChangesAsync();
        var reading = Reading(water: 4.5, flow: 0.5, debris: 50.0);
        reading.CanalId = canal.CanalId;
        reading.SensorId = sensor.SensorId;
        db.SensorReadings.Add(reading);
        await db.SaveChangesAsync();

        var svc = new FloodRiskService(db, NullLogger<FloodRiskService>.Instance);
        var assessment = await svc.CalculateRiskAsync(canal.CanalId);

        // water (4.5/3-1)*30=15 + slow flow 15 + debris 50%->10 = 40 -> Moderate.
        Assert.Equal(40, assessment.RiskScore);
        Assert.Equal("Moderate", assessment.RiskLevel);
        Assert.Equal("Rule-Based v1.0", assessment.ModelVersion);
        Assert.True(assessment.ValidUntil > DateTime.UtcNow);
    }
}
