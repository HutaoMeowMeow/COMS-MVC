using System.Reflection;
using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Helpers;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Canal access rules: Admin-only mutations, LGU redirected from the
/// management list, and the Canal Monitor (Map) projection on SQLite.
/// </summary>
public sealed class CanalAccessTests : IDisposable
{
    // Fixture helpers in SecurityAuditTests are private; this is the minimal copy.
    private sealed class FakeLookups : ILookupService
    {
        public Task<List<string>> GetValuesAsync(string category) => Task.FromResult(new List<string>());
        public Task<List<LookupOption>> GetOptionsAsync(string category) => Task.FromResult(new List<LookupOption>());
        public Task<bool> IsValidAsync(string category, string? value) => Task.FromResult(true);
        public void Invalidate(string? category = null) { }
    }

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly List<string> _sql = new();
    private readonly ApplicationDbContext _db;

    public CanalAccessTests()
    {
        _conn.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn)
            .LogTo(_sql.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _conn.Dispose();
    }

    private CanalsController ControllerFor(ApplicationUser user, params string[] roles)
    {
        var controller = new CanalsController(_db, TestUserManagers.CreateUserManager(_db), new FakeLookups(),
            _cache, CanalSubmissionAndBoundaryTests.CreateBoundary());
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
        return controller;
    }

    private async Task<ApplicationUser> CreateUserAsync(string name, string role, string? barangay = null)
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = name, Email = $"{name}@example.com", FullName = name,
            EmailConfirmed = true, CreatedAt = DateTime.UtcNow, Barangay = barangay
        };
        Assert.True((await mgr.CreateAsync(user, "Secret@123!")).Succeeded);
        if (!await _db.Roles.AnyAsync(r => r.Name == role))
        {
            await TestUserManagers.CreateRoleManager(_db).CreateAsync(new IdentityRole<int> { Name = role });
        }
        Assert.True((await mgr.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private static Canal NewCanal(string name, string barangay) => new()
    {
        CanalName = name, Location = "L", Barangay = barangay, City = "Cebu City",
        Latitude = 10.3, Longitude = 123.9, NormalWaterLevel = 1,
        WarningWaterLevel = 2, CriticalWaterLevel = 3, Status = "Normal", CreatedAt = DateTime.UtcNow
    };

    private static ObstructionAlert NewAlert(int canalId, string status) => new()
    {
        CanalId = canalId, AlertType = "WaterLevelWarning", Severity = "Medium", Title = "t",
        Description = "d", ObstructionType = "t", WaterLevel = 2.5, DetectedAt = DateTime.UtcNow, Status = status
    };

    [Fact]
    public void Mutating_actions_are_admin_only_and_posts_need_antiforgery()
    {
        var actions = typeof(CanalsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name is "Create" or "Edit" or "Delete")
            .ToList();
        Assert.Equal(6, actions.Count);
        foreach (var m in actions)
        {
            var auth = m.GetCustomAttributes<AuthorizeAttribute>().ToList();
            Assert.Contains(auth, a => a.Roles == "Admin");
            Assert.All(auth, a => Assert.Equal("Admin", a.Roles));
            if (m.GetCustomAttribute<HttpPostAttribute>() != null)
            {
                Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            }
        }
        // Every POST on the controller (not just the three above) must carry the token check.
        Assert.All(typeof(CanalsController).GetMethods().Where(m => m.GetCustomAttribute<HttpPostAttribute>() != null),
            m => Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }

    [Fact]
    public async Task Lgu_index_redirects_to_map()
    {
        var lgu = await CreateUserAsync("lgu", "LGU");
        var result = Assert.IsType<RedirectToActionResult>(await ControllerFor(lgu, "LGU").Index());
        Assert.Equal(nameof(CanalsController.Map), result.ActionName);
    }

    [Fact]
    public async Task Admin_who_is_also_lgu_keeps_management_list()
    {
        var both = await CreateUserAsync("both", "Admin");
        Assert.IsType<ViewResult>(await ControllerFor(both, "LGU", "Admin").Index());
    }

    [Fact]
    public async Task Maintenance_index_still_shows_list()
    {
        _db.Canals.Add(NewCanal("A", "Centro"));
        await _db.SaveChangesAsync();
        var tech = await CreateUserAsync("tech", "Maintenance");
        var view = Assert.IsType<ViewResult>(await ControllerFor(tech, "Maintenance").Index());
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<Canal>>(view.Model));
    }

    [Fact]
    public async Task Map_projects_monitor_items_and_applies_barangay_filter()
    {
        var own = NewCanal("Own", "Centro");
        var other = NewCanal("Other", "Lahug");
        var empty = NewCanal("Empty", "Centro");
        _db.Canals.AddRange(own, other, empty);
        await _db.SaveChangesAsync();

        var s1 = new Sensor { SensorCode = "B-2", SensorType = "WaterLevel", CanalId = own.CanalId, Latitude = 10.3, Longitude = 123.9, Status = "Online" };
        var s2 = new Sensor { SensorCode = "A-1", SensorType = "Debris", CanalId = own.CanalId, Latitude = 10.3, Longitude = 123.9, Status = "Offline" };
        var s3 = new Sensor { SensorCode = "X-1", SensorType = "WaterLevel", CanalId = other.CanalId, Latitude = 10.3, Longitude = 123.9, Status = "Online" };
        _db.Sensors.AddRange(s1, s2, s3);
        await _db.SaveChangesAsync();

        var newest = new DateTime(2026, 11, 20, 6, 30, 0, DateTimeKind.Utc);
        _db.SensorReadings.AddRange(
            new SensorReading { SensorId = s1.SensorId, CanalId = own.CanalId, WaterLevel = 1.1, RecordedAt = newest.AddHours(-2) },
            new SensorReading { SensorId = s1.SensorId, CanalId = own.CanalId, WaterLevel = 2.4, RecordedAt = newest },
            new SensorReading { SensorId = s1.SensorId, CanalId = own.CanalId, WaterLevel = 0.9, RecordedAt = newest.AddHours(-1) },
            new SensorReading { SensorId = s3.SensorId, CanalId = other.CanalId, WaterLevel = 9.9, RecordedAt = newest.AddHours(1) });
        _db.ObstructionAlerts.AddRange(
            NewAlert(own.CanalId, "Active"), NewAlert(own.CanalId, "Acknowledged"),
            NewAlert(own.CanalId, "In Progress"), NewAlert(own.CanalId, "Resolved"),
            NewAlert(other.CanalId, "Active"));
        await _db.SaveChangesAsync();

        var admin = await CreateUserAsync("admin", "Admin");
        _sql.Clear();
        var all = ItemsOf(await ControllerFor(admin, "Admin").Map());
        Assert.Equal(3, all.Count);
        Assert.Single(_sql); // one SQL command, no Include / split query
        Assert.DoesNotContain("ROW_NUMBER", _sql[0]);

        var o = all.Single(i => i.CanalId == own.CanalId);
        Assert.Equal(2, o.SensorTotal);
        Assert.Equal(1, o.SensorOnline);
        Assert.Equal(3, o.ActiveAlertCount);
        Assert.Equal(2.4, o.LatestWaterLevel);
        Assert.Equal(newest, DateTime.SpecifyKind(o.LatestReadingAt!.Value, DateTimeKind.Utc));
        Assert.Equal(PhTime.ToPhString(newest), o.LatestReadingText);
        Assert.Equal(new[] { "A-1", "B-2" }, o.Sensors.Select(s => s.SensorCode));
        Assert.Equal("Offline", o.Sensors[0].Status);

        var e = all.Single(i => i.CanalId == empty.CanalId);
        Assert.Equal(0, e.SensorTotal);
        Assert.Null(e.LatestWaterLevel);
        Assert.Null(e.LatestReadingAt);
        Assert.Null(e.LatestReadingText);
        Assert.Empty(e.Sensors);

        var bg = await CreateUserAsync("bg", "Barangay", "Centro");
        var scoped = ItemsOf(await ControllerFor(bg, "Barangay").Map());
        Assert.Equal(new[] { empty.CanalId, own.CanalId }.OrderBy(x => x), scoped.Select(i => i.CanalId).OrderBy(x => x));
    }

    private static List<CanalMonitorItem> ItemsOf(IActionResult result)
        => Assert.IsAssignableFrom<IEnumerable<CanalMonitorItem>>(Assert.IsType<ViewResult>(result).Model).ToList();
}
