using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Tests for the Water Level Trend chart endpoint (fix #2): verifies the
/// endpoint returns camelCase JSON the chart JavaScript expects, uses real
/// readings, and produces an empty state when there is no data.
/// </summary>
public sealed class SensorReadingsChartTests
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

    private static (Ctx ctx, SensorReadingsController controller) SetupWithReading(out int canalId)
    {
        var ctx = new Ctx();
        var canal = new Canal
        {
            CanalName = "Chart Canal",
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
        ctx.Db.Canals.Add(canal);
        ctx.Db.SaveChanges(); // generate CanalId before referencing it
        var sensor = new Sensor
        {
            SensorCode = "CHART-1",
            SensorType = "Water Level",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        };
        ctx.Db.Sensors.Add(sensor);
        ctx.Db.SaveChanges(); // generate SensorId before referencing it
        ctx.Db.SensorReadings.Add(new SensorReading
        {
            SensorId = sensor.SensorId,
            CanalId = canal.CanalId,
            WaterLevel = 2.75,
            FlowRate = 1.5,
            DebrisLevel = 10,
            Turbidity = 5,
            Temperature = 28,
            RecordedAt = DateTime.UtcNow,
            IsSimulated = true
        });
        ctx.Db.SaveChanges();
        canalId = canal.CanalId;

        var controller = new SensorReadingsController(ctx.Db, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Role, "Admin") }, "Test"))
                }
            }
        };
        return (ctx, controller);
    }

    [Fact]
    public async Task ChartData_ReturnsCamelCaseJson_WithRealReadings()
    {
        var (ctx, controller) = SetupWithReading(out var canalId);

        var result = Assert.IsType<JsonResult>(await controller.ChartData(canalId));
        var value = result.Value!;
        var serialized = JsonSerializer.Serialize(value);

        // Property names must match the chart JavaScript (camelCase), not
        // System.Text.Json's default PascalCase.
        Assert.Contains("\"waterLevel\"", serialized);
        Assert.Contains("\"recordedAt\"", serialized);
        Assert.Contains("\"canalName\"", serialized);

        var doc = JsonDocument.Parse(serialized);
        var readings = doc.RootElement.GetProperty("readings");
        Assert.Equal(1, readings.GetArrayLength());
        var first = readings[0];
        Assert.Equal(2.75, first.GetProperty("waterLevel").GetDouble());
        Assert.True(first.TryGetProperty("recordedAt", out var recordedAt));
        Assert.False(string.IsNullOrWhiteSpace(recordedAt.GetString()));
        Assert.Equal("Chart Canal", doc.RootElement.GetProperty("canalName").GetString());
    }

    [Fact]
    public async Task ChartData_DefaultWindow_OnlyReturnsLastFiveHours()
    {
        var (ctx, controller) = SetupWithReading(out var canalId);
        var sensorId = ctx.Db.Sensors.Select(s => s.SensorId).Single();
        ctx.Db.SensorReadings.Add(new SensorReading
        {
            SensorId = sensorId,
            CanalId = canalId,
            WaterLevel = 9.99,
            FlowRate = 9.99,
            DebrisLevel = 99,
            Turbidity = 99,
            Temperature = 35,
            RecordedAt = DateTime.UtcNow.AddHours(-6),
            IsSimulated = true
        });
        ctx.Db.SaveChanges();

        var result = Assert.IsType<JsonResult>(await controller.ChartData(canalId));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));
        var readings = doc.RootElement.GetProperty("readings");

        Assert.Equal(1, readings.GetArrayLength());
        Assert.Equal(2.75, readings[0].GetProperty("waterLevel").GetDouble());
        Assert.Equal(1, doc.RootElement.GetProperty("totalReadings").GetInt32());
    }

    [Fact]
    public async Task ChartData_LargeDataset_IsBoundedAndUsesActualPeakPoints()
    {
        var (ctx, controller) = SetupWithReading(out var canalId);
        var sensorId = ctx.Db.Sensors.Select(s => s.SensorId).Single();
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 600; i++)
        {
            ctx.Db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensorId,
                CanalId = canalId,
                WaterLevel = 1 + (i % 100) / 25.0,
                FlowRate = 1,
                DebrisLevel = 5,
                Turbidity = 5,
                Temperature = 28,
                RecordedAt = now.AddSeconds(-i * 20),
                IsSimulated = true
            });
        }
        ctx.Db.SaveChanges();

        var result = Assert.IsType<JsonResult>(
            await controller.ChartData(canalId, hours: 5, maxPoints: 20));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));
        var readings = doc.RootElement.GetProperty("readings");

        Assert.True(doc.RootElement.GetProperty("sampled").GetBoolean());
        Assert.True(readings.GetArrayLength() <= 20);
        Assert.True(doc.RootElement.GetProperty("totalReadings").GetInt32() >= 600);
        foreach (var point in readings.EnumerateArray())
        {
            Assert.False(point.GetProperty("isAggregate").GetBoolean());
            Assert.Equal("actual-peak", point.GetProperty("aggregation").GetString());
            Assert.True(point.TryGetProperty("bucketStartUtc", out _));
            Assert.True(point.TryGetProperty("bucketEndUtc", out _));
        }
    }

    [Fact]
    public async Task ChartData_EmptyDataset_ReturnsEmptyReadings()
    {
        var ctx = new Ctx();
        var canal = new Canal
        {
            CanalName = "Empty Canal",
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
        ctx.Db.Canals.Add(canal);
        ctx.Db.SaveChanges();

        var controller = new SensorReadingsController(ctx.Db, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Role, "Admin") }, "Test"))
                }
            }
        };

        var result = Assert.IsType<JsonResult>(await controller.ChartData(canal.CanalId));
        var serialized = JsonSerializer.Serialize(result.Value!);
        var doc = JsonDocument.Parse(serialized);
        Assert.Equal(0, doc.RootElement.GetProperty("readings").GetArrayLength());
    }

    // ---------------------------------------------------------------------
    // WaterLevelTrendJson (dashboard multi-canal trend chart)
    // ---------------------------------------------------------------------

    private static SensorReadingsController MakeController(Ctx ctx, string role = "Admin") =>
        new(ctx.Db, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Role, role) }, "Test"))
                }
            }
        };

    private static (int canalId, int sensorId) AddCanalWithSensor(
        Ctx ctx, string name, string barangay = "Centro")
    {
        var canal = new Canal
        {
            CanalName = name,
            Location = "Cebu Test",
            Barangay = barangay,
            City = "Cebu City",
            Latitude = 10.3,
            Longitude = 123.9,
            NormalWaterLevel = 1.0,
            WarningWaterLevel = 2.0,
            CriticalWaterLevel = 3.0,
            Status = "Normal",
            CreatedAt = DateTime.UtcNow
        };
        ctx.Db.Canals.Add(canal);
        ctx.Db.SaveChanges();

        var sensor = new Sensor
        {
            SensorCode = $"TREND-{canal.CanalId}",
            SensorType = "Water Level",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        };
        ctx.Db.Sensors.Add(sensor);
        ctx.Db.SaveChanges();
        return (canal.CanalId, sensor.SensorId);
    }

    private static void AddReading(Ctx ctx, int canalId, int sensorId,
        double waterLevel, DateTime recordedAt)
    {
        ctx.Db.SensorReadings.Add(new SensorReading
        {
            SensorId = sensorId,
            CanalId = canalId,
            WaterLevel = waterLevel,
            FlowRate = 1,
            DebrisLevel = 5,
            Turbidity = 5,
            Temperature = 28,
            RecordedAt = recordedAt,
            IsSimulated = true
        });
        ctx.Db.SaveChanges();
    }

    [Fact]
    public async Task WaterLevelTrendJson_MultipleReadings_ReturnsSortedPerCanalSeries()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalA, sensorA) = AddCanalWithSensor(ctx, "Canal A");
        var (canalB, sensorB) = AddCanalWithSensor(ctx, "Canal B");

        // Inserted out of chronological order on purpose.
        AddReading(ctx, canalA, sensorA, 1.7, now.AddMinutes(-10));
        AddReading(ctx, canalA, sensorA, 1.1, now.AddMinutes(-40));
        AddReading(ctx, canalA, sensorA, 1.4, now.AddMinutes(-25));
        AddReading(ctx, canalB, sensorB, 2.5, now.AddMinutes(-5));

        var controller = MakeController(ctx);
        var result = Assert.IsType<JsonResult>(await controller.WaterLevelTrendJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        // camelCase contract used by water-trend.js.
        Assert.True(doc.RootElement.TryGetProperty("canals", out var canals));
        Assert.True(doc.RootElement.TryGetProperty("rangeStartUtc", out _));
        Assert.True(doc.RootElement.TryGetProperty("rangeEndUtc", out _));
        Assert.True(doc.RootElement.TryGetProperty("totalReadings", out _));

        Assert.Equal(2, canals.GetArrayLength());
        Assert.Equal(4, doc.RootElement.GetProperty("totalReadings").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("returnedPoints").GetInt32());

        var seriesA = canals.EnumerateArray()
            .Single(c => c.GetProperty("canalId").GetInt32() == canalA);
        Assert.Equal("Canal A", seriesA.GetProperty("canalName").GetString());
        Assert.Equal(1.0, seriesA.GetProperty("normalLevel").GetDouble());
        Assert.Equal(2.0, seriesA.GetProperty("warningLevel").GetDouble());
        Assert.Equal(3.0, seriesA.GetProperty("criticalLevel").GetDouble());

        var points = seriesA.GetProperty("readings").EnumerateArray().ToList();
        Assert.Equal(3, points.Count);
        // Real values, chronologically ascending (not canal-name labels).
        Assert.Equal(new[] { 1.1, 1.4, 1.7 },
            points.Select(p => p.GetProperty("waterLevel").GetDouble()).ToArray());
        Assert.True(points[0].GetProperty("recordedAt").GetDateTime()
                    < points[1].GetProperty("recordedAt").GetDateTime());
        Assert.True(points[1].GetProperty("recordedAt").GetDateTime()
                    < points[2].GetProperty("recordedAt").GetDateTime());

        var seriesB = canals.EnumerateArray()
            .Single(c => c.GetProperty("canalId").GetInt32() == canalB);
        var pointB = Assert.Single(seriesB.GetProperty("readings").EnumerateArray().ToList());
        Assert.Equal(2.5, pointB.GetProperty("waterLevel").GetDouble());
    }

    [Fact]
    public async Task WaterLevelTrendJson_CanalWithoutReadings_IsOmittedAndNotFabricated()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalWithData, sensor) = AddCanalWithSensor(ctx, "Has Data");
        var (_, _) = AddCanalWithSensor(ctx, "No Data");
        AddReading(ctx, canalWithData, sensor, 1.25, now.AddMinutes(-15));

        var controller = MakeController(ctx);
        var result = Assert.IsType<JsonResult>(await controller.WaterLevelTrendJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));
        var canals = doc.RootElement.GetProperty("canals");

        var series = Assert.Single(canals.EnumerateArray().ToList());
        Assert.Equal("Has Data", series.GetProperty("canalName").GetString());
        Assert.Single(series.GetProperty("readings").EnumerateArray().ToList());
        Assert.Equal(1, doc.RootElement.GetProperty("totalReadings").GetInt32());
    }

    [Fact]
    public async Task WaterLevelTrendJson_SingleReading_ReturnsOneRealPoint()
    {
        using var ctx = new Ctx();
        var (canalId, sensorId) = AddCanalWithSensor(ctx, "Single Point");
        var recordedAt = DateTime.UtcNow.AddMinutes(-2);
        AddReading(ctx, canalId, sensorId, 2.05, recordedAt);

        var controller = MakeController(ctx);
        var result = Assert.IsType<JsonResult>(await controller.WaterLevelTrendJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        var series = Assert.Single(doc.RootElement.GetProperty("canals")
            .EnumerateArray().ToList());
        var point = Assert.Single(series.GetProperty("readings")
            .EnumerateArray().ToList());
        Assert.Equal(2.05, point.GetProperty("waterLevel").GetDouble());
        Assert.Equal(recordedAt, point.GetProperty("recordedAt").GetDateTime());
    }

    [Fact]
    public async Task WaterLevelTrendJson_NoCanals_ReturnsEmptyState()
    {
        using var ctx = new Ctx();
        var controller = MakeController(ctx);

        var result = Assert.IsType<JsonResult>(await controller.WaterLevelTrendJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        Assert.Equal(0, doc.RootElement.GetProperty("canals").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("totalReadings").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("returnedPoints").GetInt32());
    }

    [Fact]
    public async Task WaterLevelTrendJson_IgnoresReadingsOutsideWindow()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalId, sensorId) = AddCanalWithSensor(ctx, "Windowed");
        AddReading(ctx, canalId, sensorId, 1.5, now.AddMinutes(-30));
        AddReading(ctx, canalId, sensorId, 9.99, now.AddHours(-6));

        var controller = MakeController(ctx);
        var result = Assert.IsType<JsonResult>(
            await controller.WaterLevelTrendJson(hours: 5));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        var series = Assert.Single(doc.RootElement.GetProperty("canals")
            .EnumerateArray().ToList());
        var point = Assert.Single(series.GetProperty("readings")
            .EnumerateArray().ToList());
        Assert.Equal(1.5, point.GetProperty("waterLevel").GetDouble());
        Assert.Equal(1, doc.RootElement.GetProperty("totalReadings").GetInt32());
    }

    // ---------------------------------------------------------------------
    // LatestReadingsJson (Resident Dashboard canal water levels)
    // ---------------------------------------------------------------------

    [Fact]
    public void LatestReadingsJson_AllowsResidentAndRemainsRoleRestricted()
    {
        var method = typeof(SensorReadingsController)
            .GetMethod(nameof(SensorReadingsController.LatestReadingsJson));
        Assert.NotNull(method);

        var authorize = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>()
            .SingleOrDefault();
        Assert.NotNull(authorize);

        var roles = (authorize!.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("Resident", roles);
        Assert.Contains("Admin", roles);
        Assert.DoesNotContain("CustomerService", roles);
    }

    [Fact]
    public async Task LatestReadingsJson_ReturnsNewestReadingPerCanalWithClientFields()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalId, sensorId) = AddCanalWithSensor(ctx, "Resident Canal");
        AddReading(ctx, canalId, sensorId, 1.20, now.AddMinutes(-20));
        AddReading(ctx, canalId, sensorId, 2.40, now.AddMinutes(-2));

        var controller = MakeController(ctx, "Resident");
        var result = Assert.IsType<JsonResult>(await controller.LatestReadingsJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var reading = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal(canalId, reading.GetProperty("canalId").GetInt32());
        Assert.Equal("Resident Canal", reading.GetProperty("canalName").GetString());
        Assert.Equal(2.40, reading.GetProperty("waterLevel").GetDouble());
        Assert.Equal("Water Level", reading.GetProperty("sensorType").GetString());
        Assert.True(reading.TryGetProperty("recordedAt", out _));
    }

    [Fact]
    public async Task LatestReadingsJson_CanalWithoutReadings_IsOmitted()
    {
        using var ctx = new Ctx();
        var (canalWithData, sensor) = AddCanalWithSensor(ctx, "Has Reading");
        AddCanalWithSensor(ctx, "No Reading");
        AddReading(ctx, canalWithData, sensor, 1.75, DateTime.UtcNow.AddMinutes(-5));

        var controller = MakeController(ctx, "Resident");
        var result = Assert.IsType<JsonResult>(await controller.LatestReadingsJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        var reading = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("Has Reading", reading.GetProperty("canalName").GetString());
        Assert.Equal(1.75, reading.GetProperty("waterLevel").GetDouble());
    }

    [Fact]
    public async Task LatestReadingsJson_NoReadings_ReturnsEmptyArray()
    {
        using var ctx = new Ctx();
        var controller = MakeController(ctx, "Resident");

        var result = Assert.IsType<JsonResult>(await controller.LatestReadingsJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task LatestReadingsJson_IncludesCanalWaterLevelThresholds()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalId, sensorId) = AddCanalWithSensor(ctx, "Threshold Canal");
        AddReading(ctx, canalId, sensorId, 2.40, now.AddMinutes(-2));

        var controller = MakeController(ctx, "Resident");
        var result = Assert.IsType<JsonResult>(await controller.LatestReadingsJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        var reading = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.True(reading.TryGetProperty("normalLevel", out var normal));
        Assert.True(reading.TryGetProperty("warningLevel", out var warning));
        Assert.True(reading.TryGetProperty("criticalLevel", out var critical));
        Assert.Equal(1.0, normal.GetDouble());
        Assert.Equal(2.0, warning.GetDouble());
        Assert.Equal(3.0, critical.GetDouble());
    }

    [Fact]
    public async Task LatestReadingsJson_ReturnsAllGaugeClientFields()
    {
        using var ctx = new Ctx();
        var now = DateTime.UtcNow;
        var (canalId, sensorId) = AddCanalWithSensor(ctx, "Full Fields Canal");
        AddReading(ctx, canalId, sensorId, 1.75, now.AddMinutes(-3));

        var controller = MakeController(ctx, "Resident");
        var result = Assert.IsType<JsonResult>(await controller.LatestReadingsJson());
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value!));

        var reading = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        // All fields the resident-readings.js gauge renderer depends on.
        Assert.True(reading.TryGetProperty("canalId", out _));
        Assert.True(reading.TryGetProperty("canalName", out _));
        Assert.True(reading.TryGetProperty("waterLevel", out _));
        Assert.True(reading.TryGetProperty("sensorType", out _));
        Assert.True(reading.TryGetProperty("recordedAt", out _));
        Assert.True(reading.TryGetProperty("isSimulated", out _));
        Assert.True(reading.TryGetProperty("normalLevel", out _));
        Assert.True(reading.TryGetProperty("warningLevel", out _));
        Assert.True(reading.TryGetProperty("criticalLevel", out _));
    }
}
