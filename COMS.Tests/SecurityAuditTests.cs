using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Security-audit regression tests (isolated Sqlite in-memory unless noted).
/// Covers: Resident demo-seeding removal, Admin Maintenance creation via the
/// existing Users interface, Barangay isolation (sensors/alerts), broadcast
/// group policy, forgot-password throttling, email-change verification, and
/// session-stamp invalidation on role/password change.
/// </summary>
public sealed class SecurityAuditTests
{
    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class FakeEmail : IEmailService
    {
        public int Sends;
        public string LastTo = string.Empty;
        public string LastSubject = string.Empty;
        public Task SendEmailAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            Sends++;
            LastTo = to;
            LastSubject = subject;
            return Task.CompletedTask;
        }
        public Task SendPasswordResetEmailAsync(string to, string link, CancellationToken ct = default)
            => SendEmailAsync(to, "reset", link, ct);
        public Task SendPasswordResetCodeAsync(string to, string code, CancellationToken ct = default)
            => SendEmailAsync(to, "code", code, ct);
        public Task SendOtpEmailAsync(string to, string code, CancellationToken ct = default)
            => SendEmailAsync(to, "otp", code, ct);
    }

    private sealed class FakeLookups : ILookupService
    {
        public Task<List<string>> GetValuesAsync(string category) => Task.FromResult(new List<string>());
        public Task<List<LookupOption>> GetOptionsAsync(string category) => Task.FromResult(new List<LookupOption>());
        public Task<bool> IsValidAsync(string category, string? value) => Task.FromResult(true);
        public void Invalidate(string? category = null) { }
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

    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ApplicationDbContext Db { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

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
            Cache.Dispose();
            Conn.Dispose();
        }
    }

    private static void AsUser(Controller controller, ApplicationUser user, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
        controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        ApplicationDbContext db, string name, string email, string role)
    {
        var mgr = TestUserManagers.CreateUserManager(db);
        var user = new ApplicationUser
        {
            UserName = name, Email = email, FullName = name,
            EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(user, "Secret@123!")).Succeeded);
        if (!await db.Roles.AnyAsync(r => r.Name == role))
        {
            await TestUserManagers.CreateRoleManager(db)
                .CreateAsync(new IdentityRole<int> { Name = role });
        }
        Assert.True((await mgr.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private static async Task<(int ownCanal, int ownSensor, int ownAlert, int otherAlert)> SeedBarangayDataAsync(
        ApplicationDbContext db)
    {
        var ownCanal = new Canal
        {
            CanalName = "Own Canal", Location = "L", Barangay = "Centro", City = "Cebu City",
            Latitude = 10.3, Longitude = 123.9, NormalWaterLevel = 1,
            WarningWaterLevel = 2, CriticalWaterLevel = 3, Status = "Normal",
            CreatedAt = DateTime.UtcNow
        };
        var otherCanal = new Canal
        {
            CanalName = "Other Canal", Location = "L", Barangay = "Lahug", City = "Cebu City",
            Latitude = 10.3, Longitude = 123.9, NormalWaterLevel = 1,
            WarningWaterLevel = 2, CriticalWaterLevel = 3, Status = "Normal",
            CreatedAt = DateTime.UtcNow
        };
        db.Canals.AddRange(ownCanal, otherCanal);
        await db.SaveChangesAsync();
        var ownSensor = new Sensor
        {
            SensorCode = "OWN-1", SensorType = "WaterLevel", CanalId = ownCanal.CanalId,
            Latitude = 10.3, Longitude = 123.9, Status = "Online"
        };
        var otherSensor = new Sensor
        {
            SensorCode = "OTHER-1", SensorType = "WaterLevel", CanalId = otherCanal.CanalId,
            Latitude = 10.3, Longitude = 123.9, Status = "Online"
        };
        db.Sensors.AddRange(ownSensor, otherSensor);
        await db.SaveChangesAsync();
        var ownAlert = new ObstructionAlert
        {
            CanalId = ownCanal.CanalId, AlertType = "WaterLevelWarning", Severity = "Medium",
            Title = "Own", Description = "d", ObstructionType = "t", WaterLevel = 2.5,
            DetectedAt = DateTime.UtcNow, Status = "Active"
        };
        var otherAlert = new ObstructionAlert
        {
            CanalId = otherCanal.CanalId, AlertType = "WaterLevelWarning", Severity = "Medium",
            Title = "Other", Description = "d", ObstructionType = "t", WaterLevel = 2.5,
            DetectedAt = DateTime.UtcNow, Status = "Active"
        };
        db.ObstructionAlerts.AddRange(ownAlert, otherAlert);
        await db.SaveChangesAsync();
        return (ownCanal.CanalId, ownSensor.SensorId, ownAlert.ObstructionAlertId, otherAlert.ObstructionAlertId);
    }

    // ---- M2: Resident demo seeding removed, role preserved ----

    [Fact]
    public async Task Seeder_CreatesNoResidentDemo_ButKeepsResidentRole()
    {
        using var ctx = new Ctx();
        var seeder = new DataSeeder(
            TestUserManagers.CreateUserManager(ctx.Db),
            TestUserManagers.CreateRoleManager(ctx.Db),
            ctx.Db, NullLogger<DataSeeder>.Instance);
        await seeder.SeedAsync();

        var roleNames = await ctx.Db.Roles.Select(r => r.Name).ToListAsync();
        Assert.Contains("Resident", roleNames);
        Assert.Contains("Admin", roleNames);
        Assert.Contains("Maintenance", roleNames);
        Assert.Null(await ctx.Db.Users.FirstOrDefaultAsync(u => u.Email == "resident@coms.gov"));
        Assert.Null(await ctx.Db.Users.FirstOrDefaultAsync(u => u.UserName == "resident"));
        Assert.Equal(4, await ctx.Db.Users.CountAsync());
    }

    // ---- M3: Admin creates Maintenance via existing Users interface ----

    private static UsersController UsersControllerFor(
        ApplicationDbContext db, FakeEmail email, ApplicationUser admin)
    {
        var otp = new OtpService(email, NullLogger<OtpService>.Instance);
        var controller = new UsersController(
            TestUserManagers.CreateUserManager(db),
            TestUserManagers.CreateRoleManager(db),
            db, otp, NullLogger<UsersController>.Instance);
        AsUser(controller, admin, "Admin");
        return controller;
    }

    [Fact]
    public async Task Admin_CanInviteMaintenanceAccount_WithOtpVerification()
    {
        using var ctx = new Ctx();
        foreach (var r in new[] { "Admin", "Maintenance", "Resident" })
        {
            await TestUserManagers.CreateRoleManager(ctx.Db)
                .CreateAsync(new IdentityRole<int> { Name = r });
        }
        var email = new FakeEmail();
        var admin = await CreateUserAsync(ctx.Db, "boss", "boss@example.com", "Admin");
        var controller = UsersControllerFor(ctx.Db, email, admin);

        var get = await controller.Create();
        Assert.IsType<ViewResult>(get);

        var post = await controller.Create(new RegisterViewModel
        {
            FullName = "Maint Tech", UserName = "mainttech", Email = "tech@example.com",
            PhoneNumber = "09123456789", Password = "Secret@123!", ConfirmPassword = "Secret@123!",
            Role = "Maintenance"
        });
        Assert.IsType<RedirectToActionResult>(post);
        var pending = await ctx.Db.PendingRegistrations.SingleOrDefaultAsync();
        Assert.NotNull(pending);
        Assert.Equal("Maintenance", pending.Role);
        Assert.Equal(1, email.Sends);
        // No account exists until OTP verification completes.
        Assert.Empty(await ctx.Db.Users.Where(u => u.Email == "tech@example.com").ToListAsync());
    }

    [Fact]
    public async Task Admin_CannotGrantAdminRole_ThroughCreate()
    {
        using var ctx = new Ctx();
        foreach (var r in new[] { "Admin", "Maintenance" })
        {
            await TestUserManagers.CreateRoleManager(ctx.Db)
                .CreateAsync(new IdentityRole<int> { Name = r });
        }
        var email = new FakeEmail();
        var admin = await CreateUserAsync(ctx.Db, "boss", "boss@example.com", "Admin");
        var controller = UsersControllerFor(ctx.Db, email, admin);

        var post = await controller.Create(new RegisterViewModel
        {
            FullName = "Sneaky", UserName = "sneaky", Email = "sneaky@example.com",
            PhoneNumber = "09123456789", Password = "Secret@123!", ConfirmPassword = "Secret@123!",
            Role = "Admin"
        });
        Assert.IsType<ViewResult>(post);
        Assert.True(controller.ModelState.TryGetValue("Role", out var entry) && entry!.Errors.Count > 0);
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, email.Sends);
    }

    [Fact]
    public void UsersController_IsAdminOnly()
    {
        var auth = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>().FirstOrDefault();
        Assert.NotNull(auth);
        Assert.Contains("Admin", (auth.Roles ?? string.Empty).Split(','));
    }

    // ---- H3: Barangay isolation ----

    [Fact]
    public async Task BarangayUser_SeesOnlyOwnSensors()
    {
        using var ctx = new Ctx();
        await SeedBarangayDataAsync(ctx.Db);
        var bg = await CreateUserAsync(ctx.Db, "bguser", "bg@example.com", "Barangay");
        bg.Barangay = "Centro";
        await ctx.Db.SaveChangesAsync();

        var controller = new SensorsController(
            ctx.Db, TestUserManagers.CreateUserManager(ctx.Db), new FakeLookups());
        AsUser(controller, bg, "Barangay");
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var sensors = Assert.IsAssignableFrom<IEnumerable<Sensor>>(result.Model);
        Assert.Single(sensors);
        Assert.Equal("OWN-1", sensors.Single().SensorCode);
    }

    [Fact]
    public async Task BarangayUser_CannotOpenForeignSensorDetails()
    {
        using var ctx = new Ctx();
        var ids = await SeedBarangayDataAsync(ctx.Db);
        var bg = await CreateUserAsync(ctx.Db, "bguser", "bg@example.com", "Barangay");
        bg.Barangay = "Centro";
        await ctx.Db.SaveChangesAsync();

        var controller = new SensorsController(
            ctx.Db, TestUserManagers.CreateUserManager(ctx.Db), new FakeLookups());
        AsUser(controller, bg, "Barangay");
        var otherSensorId = await ctx.Db.Sensors
            .Where(s => s.SensorCode == "OTHER-1").Select(s => s.SensorId).SingleAsync();
        Assert.IsType<ForbidResult>(await controller.Details(otherSensorId));
        Assert.IsType<ViewResult>(await controller.Details(ids.ownSensor));
    }

    [Fact]
    public async Task BarangayUser_CannotAcknowledgeForeignAlert()
    {
        using var ctx = new Ctx();
        var ids = await SeedBarangayDataAsync(ctx.Db);
        var bg = await CreateUserAsync(ctx.Db, "bguser", "bg@example.com", "Barangay");
        bg.Barangay = "Centro";
        await ctx.Db.SaveChangesAsync();

        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var hub = new NullHubContext();
        var notifications = new NotificationService(
            ctx.Db, NullLogger<NotificationService>.Instance, hub, mgr);
        var alerts = new AlertService(
            ctx.Db, NullLogger<AlertService>.Instance, hub, notifications, mgr, new FakeLookups());
        var controller = new AlertsController(alerts, ctx.Db, mgr, new FakeLookups());
        AsUser(controller, bg, "Barangay");

        Assert.IsType<ForbidResult>(await controller.Acknowledge(ids.otherAlert));
        var own = await controller.Acknowledge(ids.ownAlert);
        Assert.IsType<RedirectToActionResult>(own);
        Assert.Equal("Acknowledged",
            (await ctx.Db.ObstructionAlerts.FindAsync(ids.ownAlert))!.Status);
        Assert.Equal("Active",
            (await ctx.Db.ObstructionAlerts.FindAsync(ids.otherAlert))!.Status);
    }

    [Fact]
    public async Task BarangayUser_CannotUpdateForeignAlertStatus()
    {
        using var ctx = new Ctx();
        var ids = await SeedBarangayDataAsync(ctx.Db);
        var bg = await CreateUserAsync(ctx.Db, "bguser", "bg@example.com", "Barangay");
        bg.Barangay = "Centro";
        await ctx.Db.SaveChangesAsync();

        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var hub = new NullHubContext();
        var notifications = new NotificationService(
            ctx.Db, NullLogger<NotificationService>.Instance, hub, mgr);
        var alerts = new AlertService(
            ctx.Db, NullLogger<AlertService>.Instance, hub, notifications, mgr, new FakeLookups());
        var controller = new AlertsController(alerts, ctx.Db, mgr, new FakeLookups());
        AsUser(controller, bg, "Barangay");

        Assert.IsType<ForbidResult>(await controller.UpdateStatus(ids.otherAlert, "Resolved", null));
        Assert.Equal("Active",
            (await ctx.Db.ObstructionAlerts.FindAsync(ids.otherAlert))!.Status);
    }

    // ---- H2: broadcast group policy ----

    [Fact]
    public void StaffBroadcastGroups_ExcludeNonStaff()
    {
        Assert.Contains("role_Admin", MonitoringHub.StaffGroups);
        Assert.Contains("role_LGU", MonitoringHub.StaffGroups);
        Assert.Contains("role_Barangay", MonitoringHub.StaffGroups);
        Assert.Contains("role_Maintenance", MonitoringHub.StaffGroups);
        Assert.DoesNotContain("role_Resident", MonitoringHub.StaffGroups);
        Assert.DoesNotContain("role_CustomerService", MonitoringHub.StaffGroups);
        Assert.DoesNotContain("role_Anonymous", MonitoringHub.StaffGroups);
    }

    // ---- M1: forgot-password throttle ----

    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => throw new NotSupportedException();
        public string? Action(UrlActionContext actionContext) => "https://test.local/confirm";
        public string? Action(string? action, string? controller, object? values, string? protocol, string? host, string? fragment) => "https://test.local/confirm";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => false;
        public string? Link(string? routeName, object? values) => "https://test.local/confirm";
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private static AccountController AccountControllerFor(
        ApplicationDbContext db, FakeEmail email, MemoryCache cache)
    {
        var otp = new OtpService(email, NullLogger<OtpService>.Instance);
        var controller = new AccountController(
            TestUserManagers.CreateUserManager(db), null!,
            TestUserManagers.CreateRoleManager(db), email, otp, db, cache,
            NullLogger<AccountController>.Instance);
        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
        controller.Url = new FakeUrlHelper();
        controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
        return controller;
    }

    [Fact]
    public async Task ForgotPassword_SecondImmediateRequest_IsThrottled_NoExtraEmail()
    {
        using var ctx = new Ctx();
        await CreateUserAsync(ctx.Db, "victim", "victim@example.com", "Resident");
        var email = new FakeEmail();
        var controller = AccountControllerFor(ctx.Db, email, ctx.Cache);

        var first = await controller.ForgotPassword(new ForgotPasswordViewModel { Email = "victim@example.com" });
        Assert.IsType<RedirectToActionResult>(first);
        Assert.Equal(1, email.Sends);

        var second = await controller.ForgotPassword(new ForgotPasswordViewModel { Email = "victim@example.com" });
        Assert.IsType<ViewResult>(second);
        Assert.Equal(1, email.Sends);
    }

    // ---- H1: email change requires confirmation ----

    [Fact]
    public async Task ProfileEmailChange_DoesNotApply_UntilConfirmed()
    {
        using var ctx = new Ctx();
        var user = await CreateUserAsync(ctx.Db, "resident", "old@example.com", "Resident");
        var email = new FakeEmail();
        var controller = AccountControllerFor(ctx.Db, email, ctx.Cache);
        AsUser(controller, user, "Resident");

        var post = await controller.Profile(new ProfileViewModel
        {
            UserName = user.UserName ?? string.Empty,
            FullName = "Resident Person",
            Email = "new@example.com"
        });
        Assert.IsType<RedirectToActionResult>(post);

        using var check = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(ctx.Conn).Options);
        Assert.Equal("old@example.com", (await check.Users.FindAsync(user.Id))!.Email);
        Assert.Equal(1, email.Sends);
        Assert.Equal("new@example.com", email.LastTo);
    }

    // ---- M4: stamp invalidation on role/password change ----    [Fact]
    public async Task RoleChange_BumpsSecurityStamp()
    {
        using var ctx = new Ctx();
        foreach (var r in new[] { "Admin", "Maintenance", "Resident" })
        {
            await TestUserManagers.CreateRoleManager(ctx.Db)
                .CreateAsync(new IdentityRole<int> { Name = r });
        }
        var admin = await CreateUserAsync(ctx.Db, "boss", "boss@example.com", "Admin");
        var target = await CreateUserAsync(ctx.Db, "tech", "tech@example.com", "Resident");
        var before = (await ctx.Db.Users.FindAsync(target.Id))!.SecurityStamp;

        var email = new FakeEmail();
        var otp = new OtpService(email, NullLogger<OtpService>.Instance);
        var controller = new UsersController(
            TestUserManagers.CreateUserManager(ctx.Db),
            TestUserManagers.CreateRoleManager(ctx.Db),
            ctx.Db, otp, NullLogger<UsersController>.Instance);
        AsUser(controller, admin, "Admin");

        var roles = await TestUserManagers.CreateUserManager(ctx.Db).GetRolesAsync(target);
        var post = await controller.Edit(target.Id, new UserEditViewModel
        {
            Id = target.Id,
            UserName = target.UserName ?? string.Empty,
            FullName = "Tech",
            Email = "tech@example.com",
            SelectedRoles = new List<string> { "Maintenance" },
            AllRoles = new List<string> { "Admin", "Maintenance", "Resident" }
        });
        Assert.IsType<RedirectToActionResult>(post);
        ctx.Db.Entry(target).Reload();
        var after = (await ctx.Db.Users.FindAsync(target.Id))!.SecurityStamp;
        Assert.NotEqual(before, after);
        Assert.Contains("Maintenance",
            await TestUserManagers.CreateUserManager(ctx.Db).GetRolesAsync(target));
    }

    private sealed class StubSchemeProvider : Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider
    {
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultForbidSchemeAsync() => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultSignInSchemeAsync() => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public Task<IEnumerable<Microsoft.AspNetCore.Authentication.AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult(Enumerable.Empty<Microsoft.AspNetCore.Authentication.AuthenticationScheme>());
        public Task<IEnumerable<Microsoft.AspNetCore.Authentication.AuthenticationScheme>> GetRequestHandlerSchemesAsync() => Task.FromResult(Enumerable.Empty<Microsoft.AspNetCore.Authentication.AuthenticationScheme>());
        public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        public void AddScheme(Microsoft.AspNetCore.Authentication.AuthenticationScheme scheme) { }
        public void RemoveScheme(string name) { }
    }

    private sealed class AlwaysConfirmed : IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(true);
    }

    private static SignInManager<ApplicationUser> MakeSignInManager(
        ApplicationDbContext db, UserManager<ApplicationUser> mgr, HttpContext? http = null)
    {
        var claimsFactory = new UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<int>>(
            mgr, TestUserManagers.CreateRoleManager(db), TestUserManagers.ProgramLikeOptions());
        return new SignInManager<ApplicationUser>(
            mgr, new HttpContextAccessor { HttpContext = http ?? new DefaultHttpContext() },
            claimsFactory, TestUserManagers.ProgramLikeOptions(),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new StubSchemeProvider(), new AlwaysConfirmed());
    }

    private sealed class StubAuthService : Microsoft.AspNetCore.Authentication.IAuthenticationService
    {
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
        public Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
    }

    [Fact]
    public async Task ConfirmEmailChange_AppliesEmail_AfterOwnershipProof()
    {
        using var ctx = new Ctx();
        var protector = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var mgr = TestUserManagers.CreateUserManager(ctx.Db, null, protector);
        var user = await CreateUserAsync(ctx.Db, "resident", "old@example.com", "Resident");
        var token = await mgr.GenerateChangeEmailTokenAsync(user, "new@example.com");
        var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            System.Text.Encoding.UTF8.GetBytes(token));

        var email = new FakeEmail();
        // SignInManager must share the controller's HttpContext (which carries
        // the stubbed RequestServices for the refresh sign-in).
        AccountController controller;
        {
            var mgr2 = TestUserManagers.CreateUserManager(ctx.Db, null, protector);
            var http = new DefaultHttpContext();
            var signIn = MakeSignInManager(ctx.Db, mgr2, http);
            controller = new AccountController(
                mgr2, signIn,
                TestUserManagers.CreateRoleManager(ctx.Db), email,
                new OtpService(email, NullLogger<OtpService>.Instance),
                ctx.Db, ctx.Cache, NullLogger<AccountController>.Instance);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = http,
                RouteData = new RouteData(),
                ActionDescriptor = new ControllerActionDescriptor()
            };
            controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.UserName ?? string.Empty),
                new(ClaimTypes.Role, "Resident")
            };
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
        controller.Url = new FakeUrlHelper();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService>(new StubAuthService());
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();

        await controller.ConfirmEmailChange(user.Id, "new@example.com", code);

        using var check = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(ctx.Conn).Options);
        var updated = await check.Users.FindAsync(user.Id);
        Assert.NotNull(updated);
        Assert.Equal("new@example.com", updated.Email);
        Assert.True(updated.EmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmailChange_RejectsMismatchedUser()
    {
        using var ctx = new Ctx();
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var user = await CreateUserAsync(ctx.Db, "resident", "old@example.com", "Resident");
        var other = await CreateUserAsync(ctx.Db, "other", "other@example.com", "Resident");

        var email = new FakeEmail();
        var controller = new AccountController(
            mgr, MakeSignInManager(ctx.Db, mgr),
            TestUserManagers.CreateRoleManager(ctx.Db), email,
            new OtpService(email, NullLogger<OtpService>.Instance),
            ctx.Db, ctx.Cache, NullLogger<AccountController>.Instance);
        AsUser(controller, user, "Resident");
        controller.Url = new FakeUrlHelper();

        // Attacker replays their own link bytes but another account's id.
        var result = await controller.ConfirmEmailChange(other.Id, "new@example.com", "bogus");
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("old@example.com", (await ctx.Db.Users.FindAsync(user.Id))!.Email);
    }
}
