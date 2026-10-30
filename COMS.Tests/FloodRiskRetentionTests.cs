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

public sealed class FloodRiskRetentionTests
{
    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ServiceProvider Provider { get; }

        public Ctx()
        {
            Conn.Open();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(Conn));
            Provider = services.BuildServiceProvider();

            using var scope = Provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
        }

        public void Dispose()
        {
            Provider.Dispose();
            Conn.Dispose();
        }
    }

    private static FloodRiskAssessment Assessment(int canalId, DateTime when) => new()
    {
        CanalId = canalId,
        RiskScore = 50,
        RiskLevel = "Moderate",
        AssessmentDetails = "Test assessment",
        AssessmentDate = when,
        ValidUntil = when.AddHours(5)
    };

    private static FloodRiskRetentionService CreateService(
        IServiceScopeFactory scopeFactory, int retentionHours = 5)
    {
        return new FloodRiskRetentionService(
            scopeFactory,
            NullLogger<FloodRiskRetentionService>.Instance,
            Options.Create(new FloodRiskRetentionOptions
            {
                RetentionHours = retentionHours,
                CleanupIntervalMinutes = 30,
                CleanupBatchSize = 100
            }));
    }

    [Fact]
    public async Task Cleanup_DeletesExpiredHistory_ButPreservesLatestPerCanal()
    {
        using var ctx = new Ctx();
        int canalOneId;
        int canalTwoId;

        using (var scope = ctx.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var canalOne = new Canal
            {
                CanalName = "Retention One",
                Location = "Cebu",
                Barangay = "Centro",
                City = "Cebu City",
                Status = "Normal",
                CreatedAt = DateTime.UtcNow
            };
            var canalTwo = new Canal
            {
                CanalName = "Retention Two",
                Location = "Cebu",
                Barangay = "Centro",
                City = "Cebu City",
                Status = "Normal",
                CreatedAt = DateTime.UtcNow
            };
            db.Canals.AddRange(canalOne, canalTwo);
            await db.SaveChangesAsync();
            canalOneId = canalOne.CanalId;
            canalTwoId = canalTwo.CanalId;

            var now = DateTime.UtcNow;
            db.FloodRiskAssessments.AddRange(
                Assessment(canalOneId, now.AddHours(-1)),
                Assessment(canalOneId, now.AddHours(-2)),
                Assessment(canalOneId, now.AddHours(-6)),
                Assessment(canalOneId, now.AddHours(-12)),
                Assessment(canalTwoId, now.AddHours(-10)));

            await db.SaveChangesAsync();
        }

        var service = CreateService(ctx.Provider.GetRequiredService<IServiceScopeFactory>());
        var result = await service.CleanupOldAssessmentsAsync(CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(2, result.PreservedLatest);

        using var verifyScope = ctx.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var remaining = await verifyDb.FloodRiskAssessments
            .OrderBy(a => a.AssessmentDate)
            .ToListAsync();

        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, a => a.CanalId == canalOneId
            && a.AssessmentDate > DateTime.UtcNow.AddHours(-5));
        Assert.Contains(remaining, a => a.CanalId == canalTwoId);
        Assert.DoesNotContain(remaining, a => a.CanalId == canalOneId
            && a.AssessmentDate < DateTime.UtcNow.AddHours(-5));
    }

    [Fact]
    public async Task Cleanup_DoesNotTouchSensorReadingsOrAlerts()
    {
        using var ctx = new Ctx();
        int canalId;
        int sensorId;

        using (var scope = ctx.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var canal = new Canal
            {
                CanalName = "Retention Sensor",
                Location = "Cebu",
                Barangay = "Centro",
                City = "Cebu City",
                Status = "Normal",
                CreatedAt = DateTime.UtcNow
            };
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            canalId = canal.CanalId;

            var sensor = new Sensor
            {
                SensorCode = "RET-1",
                SensorType = "Water Level",
                CanalId = canalId,
                Status = "Online"
            };
            db.Sensors.Add(sensor);
            await db.SaveChangesAsync();
            sensorId = sensor.SensorId;

            db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensorId,
                CanalId = canalId,
                WaterLevel = 1.2,
                RecordedAt = DateTime.UtcNow.AddDays(-30)
            });
            db.ObstructionAlerts.Add(new ObstructionAlert
            {
                CanalId = canalId,
                Title = "Old alert",
                Description = "Should be retained by its own policy",
                AlertType = "Flood",
                ObstructionType = "Debris",
                Severity = "Low",
                Status = "Resolved",
                DetectedAt = DateTime.UtcNow.AddDays(-30)
            });
            db.FloodRiskAssessments.Add(Assessment(canalId, DateTime.UtcNow.AddDays(-30)));
            await db.SaveChangesAsync();
        }

        var service = CreateService(ctx.Provider.GetRequiredService<IServiceScopeFactory>());
        await service.CleanupOldAssessmentsAsync(CancellationToken.None);

        using var verifyScope = ctx.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.SensorReadings.CountAsync());
        Assert.Equal(1, await verifyDb.ObstructionAlerts.CountAsync());
        Assert.Equal(1, await verifyDb.FloodRiskAssessments.CountAsync());
    }
}
