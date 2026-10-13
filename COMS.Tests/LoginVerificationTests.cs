using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the login-vs-registration TempData leak:
/// an existing account logging in must never see "No pending registration
/// found" / "Please verify your email" on the dashboard.
/// </summary>
public sealed class LoginVerificationTests : IDisposable
{
    private sealed class FakeEmail : IEmailService
    {
        public int Sends;
        public string LastOtpTo = string.Empty;
        public string LastOtp = string.Empty;
        public Task SendEmailAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            Sends++;
            return Task.CompletedTask;
        }
        public Task SendPasswordResetEmailAsync(string to, string link, CancellationToken ct = default)
            => SendEmailAsync(to, "reset", link, ct);
        public Task SendPasswordResetCodeAsync(string to, string code, CancellationToken ct = default)
            => SendEmailAsync(to, "code", code, ct);
        public Task SendOtpEmailAsync(string to, string code, CancellationToken ct = default)
        {
            Sends++;
            LastOtpTo = to;
            LastOtp = code;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => throw new NotSupportedException();
        public string? Action(UrlActionContext actionContext) => null;
        public string? Action(string? action, string? controller, object? values, string? protocol, string? host, string? fragment) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => false;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class StubSchemeProvider : IAuthenticationSchemeProvider
    {
        public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => Task.FromResult<AuthenticationScheme?>(null);
        public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
        public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() => Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
        public Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(null);
        public void AddScheme(AuthenticationScheme scheme) { }
        public void RemoveScheme(string name) { }
    }

    private sealed class AlwaysConfirmed : IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(true);
    }

    private sealed class StubAuthService : IAuthenticationService
    {
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task CreateNotificationAsync(int userId, string title, string message, string type, int? alertId = null, int? reportId = null) => Task.CompletedTask;
        public Task CreateNotificationForRoleAsync(string role, string title, string message, string type, int? alertId = null, int? reportId = null) => Task.CompletedTask;
        public Task CreateNotificationForRolesAsync(string[] roles, string title, string message, string type, int? alertId = null, int? reportId = null) => Task.CompletedTask;
        public Task<bool> MarkAsReadAsync(int notificationId, int userId) => Task.FromResult(true);
        public Task<int> GetUnreadCountAsync(int userId) => Task.FromResult(0);
        public Task<List<Notification>> GetRecentAsync(int userId, int count = 10) => Task.FromResult(new List<Notification>());
        public Task<List<Notification>> GetAllAsync(int userId) => Task.FromResult(new List<Notification>());
    }

    private readonly SqliteConnection _conn;
    private readonly ApplicationDbContext _db;
    private readonly FakeEmail _email = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public LoginVerificationTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _conn.Dispose();
    }

    private AccountController NewAccountController()
    {
        var userManager = TestUserManagers.CreateUserManager(_db);
        var roleManager = TestUserManagers.CreateRoleManager(_db);
        var otp = new OtpService(_email, NullLogger<OtpService>.Instance);
        var http = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = http };
        var claimsFactory = new UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<int>>(
            userManager, roleManager, TestUserManagers.ProgramLikeOptions());
        var signIn = new SignInManager<ApplicationUser>(
            userManager, accessor, claimsFactory, TestUserManagers.ProgramLikeOptions(),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new StubSchemeProvider(), new AlwaysConfirmed());
        var controller = new AccountController(
            userManager, signIn, roleManager, _email, otp, _db, _cache,
            NullLogger<AccountController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new StubAuthService());
        http.RequestServices = services.BuildServiceProvider();
        controller.Url = new FakeUrlHelper();
        controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
        return controller;
    }

    private async Task<ApplicationUser> CreateUserAsync(
        string name, string email, string password, bool confirmed, string? role = null)
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = name, Email = email, FullName = name,
            PhoneNumber = "09123456789",
            EmailConfirmed = confirmed, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(user, password)).Succeeded);
        if (role is not null)
        {
            if (!await _db.Roles.AnyAsync(r => r.Name == role))
                await TestUserManagers.CreateRoleManager(_db)
                    .CreateAsync(new IdentityRole<int> { Name = role });
            Assert.True((await mgr.AddToRoleAsync(user, role)).Succeeded);
        }
        return user;
    }

    private static string ModelErrors(AccountController c) =>
        string.Join("; ", c.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));

    // 1. Verified account logs in with no pending row: dashboard redirect, no stale messages.
    [Fact]
    public async Task VerifiedLogin_WithoutPending_RedirectsToDashboard_AndClearsStaleTempData()
    {
        await CreateUserAsync("admin", "admin@coms.gov", "Admin@COMS123!", confirmed: true, role: "Admin");
        var controller = NewAccountController();

        // Simulate stale TempData left by an earlier anonymous VerifyEmail->Register chain.
        controller.TempData["ErrorMessage"] = "No pending registration found for this email. Please register first.";
        controller.TempData["AlertMessage"] = "Please verify your email with the code we sent before signing in.";

        var result = await controller.Login(
            new LoginViewModel { Username = "admin@coms.gov", Password = "Admin@COMS123!" }, null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Dashboard", redirect.ControllerName);
        Assert.False(controller.TempData.ContainsKey("ErrorMessage"));
        Assert.False(controller.TempData.ContainsKey("AlertMessage"));
        Assert.Equal(0, await _db.PendingRegistrations.CountAsync());
        Assert.Equal(0, _email.Sends);
    }

    // 2. Unverified existing account: OTP seeded from its own record, no duplicate, not auto-verified.
    [Fact]
    public async Task UnverifiedLogin_SeedsPending_AndRedirectsToVerifyEmail_WithoutDuplicate()
    {
        await CreateUserAsync("legacy", "legacy@example.com", "Secret@123!", confirmed: false, role: "Resident");
        var controller = NewAccountController();

        var result = await controller.Login(
            new LoginViewModel { Username = "legacy@example.com", Password = "Secret@123!" }, null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.VerifyEmail), redirect.ActionName);
        Assert.Equal(1, await _db.PendingRegistrations.CountAsync());
        Assert.Equal(1, _email.Sends);
        Assert.NotEmpty(_email.LastOtp);
        // No duplicate account, still unverified until the code is entered.
        Assert.Equal(1, await _db.Users.CountAsync());
        Assert.False((await _db.Users.SingleAsync()).EmailConfirmed);
        Assert.True(controller.TempData.ContainsKey("AlertMessage"));
    }

    // 2b. Full legacy verify flow: correct OTP confirms the existing account in place.
    [Fact]
    public async Task UnverifiedLogin_FullVerifyFlow_ConfirmsExistingAccountWithoutDuplicate()
    {
        await CreateUserAsync("legacy", "legacy@example.com", "Secret@123!", confirmed: false, role: "Resident");
        var controller = NewAccountController();

        var login = await controller.Login(
            new LoginViewModel { Username = "legacy", Password = "Secret@123!" }, null);
        Assert.IsType<RedirectToActionResult>(login);

        controller.ModelState.Clear();
        var verify = await controller.VerifyEmail(new VerifyOtpViewModel
        {
            Email = "legacy@example.com",
            Code = _email.LastOtp
        });

        var redirect = Assert.IsType<RedirectToActionResult>(verify);
        Assert.Equal(nameof(AccountController.Login), redirect.ActionName);
        Assert.Equal(1, await _db.Users.CountAsync());
        var user = await _db.Users.SingleAsync();
        Assert.True(user.EmailConfirmed);
        Assert.Equal(0, await _db.PendingRegistrations.CountAsync());
        // Original password and role preserved.
        var mgr = TestUserManagers.CreateUserManager(_db);
        Assert.True(await mgr.CheckPasswordAsync(user, "Secret@123!"));
        Assert.Contains("Resident", await mgr.GetRolesAsync(user));

        // And now login succeeds to the dashboard with no stale messages.
        var controller2 = NewAccountController();
        var relogin = await controller2.Login(
            new LoginViewModel { Username = "legacy@example.com", Password = "Secret@123!" }, null);
        var dash = Assert.IsType<RedirectToActionResult>(relogin);
        Assert.Equal("Index", dash.ActionName);
        Assert.Equal("Dashboard", dash.ControllerName);
    }

    // 3. Failed login shows the login error only (no registration redirect, no dashboard TempData).
    [Fact]
    public async Task FailedLogin_ShowsLoginErrorOnly()
    {
        await CreateUserAsync("admin", "admin@coms.gov", "Admin@COMS123!", confirmed: true, role: "Admin");
        var controller = NewAccountController();

        var result = await controller.Login(
            new LoginViewModel { Username = "admin", Password = "Wrong@123!" }, null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Contains("Invalid username or password.", ModelErrors(controller));
        Assert.False(controller.TempData.ContainsKey("ErrorMessage"));
        Assert.False(controller.TempData.ContainsKey("AlertMessage"));
        Assert.Equal(0, await _db.PendingRegistrations.CountAsync());
    }

    // Lockout preserved after repeated failures.
    [Fact]
    public async Task RepeatedFailedLogins_LockOut_WithoutBypass()
    {
        await CreateUserAsync("victim", "victim@example.com", "Secret@123!", confirmed: true, role: "Resident");
        var controller = NewAccountController();

        IActionResult? last = null;
        for (var i = 0; i < 6; i++)
        {
            controller.ModelState.Clear();
            last = await controller.Login(
                new LoginViewModel { Username = "victim", Password = "Wrong@123!" }, null);
            Assert.IsType<ViewResult>(last);
        }
        Assert.Contains("temporarily locked", ModelErrors(controller));
    }

    // 4. OTP request with no pending and unknown address stays in the registration workflow.
    [Fact]
    public async Task VerifyEmailGet_UnknownEmail_StaysInRegistrationWorkflow()
    {
        var controller = NewAccountController();
        var result = await controller.VerifyEmail("nobody@example.com");
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.Register), redirect.ActionName);
        Assert.Equal("No pending registration found for this email. Please register first.",
            controller.TempData["ErrorMessage"]?.ToString());
    }

    [Fact]
    public async Task ResendOtp_UnknownEmail_StaysInRegistrationWorkflow()
    {
        var controller = NewAccountController();
        var result = await controller.ResendOtp("nobody@example.com");
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.Register), redirect.ActionName);
        Assert.Equal("No pending registration found for this email. Please register first.",
            controller.TempData["ErrorMessage"]?.ToString());
    }

    // 4b. ...but a confirmed address is directed to sign-in, never to registration.
    [Fact]
    public async Task VerifyEmailGet_ConfirmedEmail_RedirectsToLogin_NotRegister()
    {
        await CreateUserAsync("admin", "admin@coms.gov", "Admin@COMS123!", confirmed: true, role: "Admin");
        var controller = NewAccountController();

        var result = await controller.VerifyEmail("admin@coms.gov");
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.Login), redirect.ActionName);
    }

    [Fact]
    public async Task ResendOtp_ConfirmedEmail_RedirectsToLogin_NotRegister()
    {
        await CreateUserAsync("admin", "admin@coms.gov", "Admin@COMS123!", confirmed: true, role: "Admin");
        var controller = NewAccountController();

        var result = await controller.ResendOtp("admin@coms.gov");
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.Login), redirect.ActionName);
    }

    // 6. Role redirects and permissions intact.
    [Theory]
    [InlineData("Admin", "Admin")]
    [InlineData("LGU", "Lgu")]
    [InlineData("Barangay", "Barangay")]
    [InlineData("Maintenance", "Maintenance")]
    [InlineData("Resident", "Resident")]
    public async Task DashboardIndex_Redirects_PerRole(string role, string action)
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = "u_" + role, Email = role + "@example.com",
            FullName = role, EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(user, "Secret@123!")).Succeeded);
        if (!await _db.Roles.AnyAsync(r => r.Name == role))
            await TestUserManagers.CreateRoleManager(_db)
                .CreateAsync(new IdentityRole<int> { Name = role });
        Assert.True((await mgr.AddToRoleAsync(user, role)).Succeeded);

        var controller = new DashboardController(_db, mgr, new FakeNotifications());
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Role, role)
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Index());
        Assert.Equal(action, redirect.ActionName);
    }

    [Fact]
    public async Task DashboardIndex_UnknownRole_GoesToAccessDenied_NeverLoginLoop()
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = "norole", Email = "norole@example.com",
            FullName = "No Role", EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(user, "Secret@123!")).Succeeded);

        var controller = new DashboardController(_db, mgr, new FakeNotifications());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName ?? string.Empty)
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Index());
        Assert.Equal(nameof(DashboardController.AccessDenied), redirect.ActionName);
    }
}
