using System.Security.Claims;
using System.Text.Json;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace COMS.Tests;

[Collection(nameof(PgCollection))]
public sealed class SensorReadingsFilterTests
{
    private readonly PgFixture _fx;

    public SensorReadingsFilterTests(PgFixture fx) => _fx = fx;

    [Fact]
    public async Task ChartData_DateRange_IsPhilippineInclusiveAndUtcConverted()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = _fx.Open();

        var canal = new Canal
        {
            CanalName = $"Filter Canal {tag}",
            Location = "Cebu Test",
            Barangay = "Centro",
            City = "Cebu City",
            Latitude = 10.3157,
            Longitude = 123.8854,
            Length = 100,
            Width = 5,
            Depth = 2,
            NormalWaterLevel = 1.0,
            WarningWaterLevel = 2.0,
            CriticalWaterLevel = 3.0,
            Status = "Normal",
            CreatedAt = DateTime.UtcNow
        };
        db.Canals.Add(canal);
        await db.SaveChangesAsync();

        var sensor = new Sensor
        {
            SensorCode = $"FILTER-{tag}",
            SensorType = "WaterLevel",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        };
        db.Sensors.Add(sensor);
        await db.SaveChangesAsync();

        // PH times: Oct 20 10:00, Oct 20 20:00, Oct 21 08:00.
        foreach (var recordedAtUtc in new[]
                 {
                     new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc),
                     new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
                     new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)
                 })
        {
            db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensor.SensorId,
                CanalId = canal.CanalId,
                WaterLevel = 1.5,
                FlowRate = 2,
                DebrisLevel = 3,
                Turbidity = 4,
                Temperature = 28,
                RecordedAt = recordedAtUtc,
                IsSimulated = true
            });
        }
        await db.SaveChangesAsync();

        var controller = NewAdminController(db);

        try
        {
            var json = Assert.IsType<JsonResult>(await controller.ChartData(
                canal.CanalId, fromDate: "2026-10-20", toDate: "2026-10-20"));
            var payload = JsonSerializer.SerializeToElement(json.Value);
            Assert.Equal(2, payload.GetProperty("readings").GetArrayLength());

            // Explicit end time is inclusive in Philippine wall-clock time.
            json = Assert.IsType<JsonResult>(await controller.ChartData(
                canal.CanalId, fromDate: "2026-10-20", fromTime: "20:00",
                toDate: "2026-10-20", toTime: "20:00"));
            payload = JsonSerializer.SerializeToElement(json.Value);
            Assert.Equal(1, payload.GetProperty("readings").GetArrayLength());

            Assert.IsType<BadRequestObjectResult>(await controller.ChartData(
                canal.CanalId, fromDate: "2026-10-21", toDate: "2026-10-20"));
            Assert.IsType<BadRequestObjectResult>(await controller.ChartData(
                canal.CanalId, fromDate: "not-a-date"));
        }
        finally
        {
            db.SensorReadings.RemoveRange(
                db.SensorReadings.Where(r => r.CanalId == canal.CanalId));
            db.Sensors.Remove(sensor);
            db.Canals.Remove(canal);
            await db.SaveChangesAsync();
        }
    }

    private static SensorReadingsController NewAdminController(ApplicationDbContext db) =>
        new(db, null!, null!)
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
}
