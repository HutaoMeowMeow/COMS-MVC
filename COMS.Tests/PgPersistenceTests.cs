using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Live PostgreSQL persistence verification (risk B) against an ISOLATED
/// schema (<c>coms_verify</c> inside the local dev database — never the
/// shared <c>public</c> schema and never production data).
/// Requires a reachable PostgreSQL; connection string comes from the
/// COMS_VERIFY_CONN environment variable, falling back to the repo's own
/// development default (localhost / comsuser / 123).
/// Covers: clean-database migration, CRUD, restart persistence, FK
/// restrict behavior, transactions, duplicate prevention, concurrency,
/// and honest failure surfacing (no false success on DB errors).
/// </summary>
[Collection(nameof(PgCollection))]
public sealed class PgPersistenceTests
{
    private readonly PgFixture _fx;

    public PgPersistenceTests(PgFixture fx) => _fx = fx;

    private ApplicationDbContext Open() => _fx.Open();

    private static Canal NewCanal(string tag) => new()
    {
        CanalName = $"Verify Canal {tag}",
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

    [Fact]
    public async Task Migrations_AllApplied_OnCleanDatabase()
    {
        using var db = Open();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Canal_FullCrud_RoundTrips()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int id;
        using (var db = Open())
        {
            var canal = NewCanal(tag);
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            id = canal.CanalId;
            Assert.True(id > 0);
        }
        using (var db = Open())
        {
            var read = await db.Canals.FindAsync(id);
            Assert.NotNull(read);
            Assert.Equal($"Verify Canal {tag}", read.CanalName);
            read.Status = "Maintenance";
            await db.SaveChangesAsync();
        }
        using (var db = Open())
        {
            var read = await db.Canals.FindAsync(id);
            Assert.NotNull(read);
            Assert.Equal("Maintenance", read.Status);
            db.Canals.Remove(read);
            await db.SaveChangesAsync();
        }
        using (var db = Open())
        {
            Assert.Null(await db.Canals.FindAsync(id));
        }
    }

    [Fact]
    public async Task Reading_Survives_NewContext_RestartSimulation()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int readingId;
        using (var db = Open())
        {
            var canal = NewCanal(tag);
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            var sensor = new Sensor
            {
                SensorCode = $"VERIFY-{tag}",
                SensorType = "WaterLevel",
                CanalId = canal.CanalId,
                Latitude = 10.3,
                Longitude = 123.9,
                Status = "Online"
            };
            db.Sensors.Add(sensor);
            await db.SaveChangesAsync();
            var reading = new SensorReading
            {
                SensorId = sensor.SensorId,
                CanalId = canal.CanalId,
                WaterLevel = 1.23,
                FlowRate = 2.5,
                DebrisLevel = 10,
                Turbidity = 5,
                Temperature = 28,
                RecordedAt = DateTime.UtcNow,
                IsSimulated = true
            };
            db.SensorReadings.Add(reading);
            await db.SaveChangesAsync();
            readingId = reading.SensorReadingId;
        }
        // "Restart": completely new context, same database.
        using (var db = Open())
        {
            var back = await db.SensorReadings.FindAsync(readingId);
            Assert.NotNull(back);
            Assert.Equal(1.23, back.WaterLevel);
        }
    }

    [Fact]
    public async Task Sensor_DuplicateCode_IsRejected()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = Open();
        var canal = NewCanal(tag);
        db.Canals.Add(canal);
        await db.SaveChangesAsync();
        db.Sensors.Add(new Sensor
        {
            SensorCode = $"DUP-{tag}",
            SensorType = "WaterLevel",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        });
        await db.SaveChangesAsync();

        db.Sensors.Add(new Sensor
        {
            SensorCode = $"DUP-{tag}",
            SensorType = "Flow",
            CanalId = canal.CanalId,
            Latitude = 10.3,
            Longitude = 123.9,
            Status = "Online"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Canal_WithChildren_CannotBeDeleted_RestrictEnforced()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int canalId;
        using (var db = Open())
        {
            var canal = NewCanal(tag);
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            canalId = canal.CanalId;
            db.Sensors.Add(new Sensor
            {
                SensorCode = $"CHILD-{tag}",
                SensorType = "WaterLevel",
                CanalId = canalId,
                Latitude = 10.3,
                Longitude = 123.9,
                Status = "Online"
            });
            await db.SaveChangesAsync();
        }

        // Mirror the app's delete path: fresh context, children untracked —
        // PostgreSQL must reject the delete via the Restrict FK.
        using (var db = Open())
        {
            var canal = await db.Canals.FindAsync(canalId);
            Assert.NotNull(canal);
            db.Canals.Remove(canal);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Related records are intact (no corrupt partial delete).
        using var check = Open();
        Assert.NotNull(await check.Canals.FindAsync(canalId));
    }

    [Fact]
    public async Task Transaction_Rollback_LeavesNoTrace()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Canals.Add(NewCanal(tag));
        await db.SaveChangesAsync();
        await tx.RollbackAsync();

        using var check = Open();
        Assert.Empty(await check.Canals.Where(c => c.CanalName == $"Verify Canal {tag}").ToListAsync());
    }

    [Fact]
    public async Task Concurrent_Submissions_BothPersist()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int canalId, sensorId;
        using (var db = Open())
        {
            var canal = NewCanal(tag);
            db.Canals.Add(canal);
            await db.SaveChangesAsync();
            canalId = canal.CanalId;
            var sensor = new Sensor
            {
                SensorCode = $"CONC-{tag}",
                SensorType = "WaterLevel",
                CanalId = canalId,
                Latitude = 10.3,
                Longitude = 123.9,
                Status = "Online"
            };
            db.Sensors.Add(sensor);
            await db.SaveChangesAsync();
            sensorId = sensor.SensorId;
        }
        var t1 = Task.Run(async () =>
        {
            using var db = Open();
            db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensorId, CanalId = canalId, WaterLevel = 1.1,
                FlowRate = 2, DebrisLevel = 5, Turbidity = 2, Temperature = 28,
                RecordedAt = DateTime.UtcNow, IsSimulated = true
            });
            await db.SaveChangesAsync();
        });
        var t2 = Task.Run(async () =>
        {
            using var db = Open();
            db.SensorReadings.Add(new SensorReading
            {
                SensorId = sensorId, CanalId = canalId, WaterLevel = 1.2,
                FlowRate = 2, DebrisLevel = 6, Turbidity = 2, Temperature = 28,
                RecordedAt = DateTime.UtcNow, IsSimulated = true
            });
            await db.SaveChangesAsync();
        });
        await Task.WhenAll(t1, t2);

        using var check = Open();
        var count = await check.SensorReadings.CountAsync(r => r.CanalId == canalId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DatabaseFailure_SurfacesException_NoFalseSuccess()
    {
        // A dead connection string must throw — callers may never report success.
        var bad = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=invalid.invalid;Port=5432;Database=nope;Username=nope;Password=nope;Timeout=5")
            .Options;
        using var db = new ApplicationDbContext(bad);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Canals.CountAsync());
    }

    [Fact]
    public async Task SensorService_MissingCanal_ThrowsHonestError()
    {
        using var db = Open();
        var svc = new SensorService(db,
            NullLogger<SensorService>.Instance,
            new DummyServiceProvider());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GenerateSimulatedReadingAsync(-999));
    }

    private sealed class DummyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

[CollectionDefinition(nameof(PgCollection))]
public sealed class PgCollection : ICollectionFixture<PgFixture>;

public sealed class PgFixture : IAsyncLifetime
{
    private const string Schema = "coms_verify";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("COMS_VERIFY_CONN")
        ?? $"Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=123;Timeout=15;Search Path={Schema},public";

    public ApplicationDbContext Open()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new ApplicationDbContext(options);
    }

    public async Task InitializeAsync()
    {
        // Schema-level isolation inside the LOCAL DEV database (never the
        // shared public schema): reset to a clean schema, then migrate.
        var adminCs = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            SearchPath = "public"
        }.ToString();
        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", admin).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CREATE SCHEMA \"{Schema}\"", admin).ExecuteNonQueryAsync();
        }
        // Apply all migrations to the clean schema.
        using var db = Open();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
