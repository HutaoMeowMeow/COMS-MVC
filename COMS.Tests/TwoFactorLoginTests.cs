using System.Security.Claims;
using System.Security.Cryptography;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Helpers;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

public sealed class TwoFactorLoginTests
{
    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ApplicationDbContext Db { get; }
        public UserManager<ApplicationUser> UserManager { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public TrustedDeviceService TrustedDevices { get; }
        public DefaultHttpContext Http { get; } = new();
        public AccountController Controller { get; }

        public Ctx()
        {
            Conn.Open();
            Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Conn).Options);
            Db.Database.EnsureCreated();

            UserManager = TestUserManagers.CreateUserManager(Db);
            UserManager.RegisterTokenProvider(
                TokenOptions.DefaultAuthenticatorProvider,
                new AuthenticatorTokenProvider<ApplicationUser>());
            var roleManager = TestUserManagers.CreateRoleManager(Db);
            var signIn = CreateSignInManager(UserManager, roleManager, Http);
            TrustedDevices = new TrustedDeviceService(
                Db,
                NullLogger<TrustedDeviceService>.Instance,
                Options.Create(new TwoFactorOptions { TrustedDeviceDays = 30 }),
                TimeProvider.System);

            Controller = new AccountController(
                UserManager,
                signIn,
                roleManager,
                null!,
                null!,
                Db,
                Cache,
                NullLogger<AccountController>.Instance,
                new FakeWebHostEnvironment(),
                TrustedDevices,
                Options.Create(new TwoFactorOptions { TrustedDeviceDays = 30 }));

            Http.Request.Scheme = "https";
            Http.RequestServices = BuildServices();
            Controller.ControllerContext = new ControllerContext
            {
                HttpContext = Http,
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
            };
            Controller.TempData = new TempDataDictionary(Http, new FakeTempDataProvider());
            Controller.Url = new FakeUrlHelper();
        }

        public async Task<ApplicationUser> CreatePlainUserAsync()
        {
            var user = new ApplicationUser
            {
                UserName = "twofactor@example.com",
                NormalizedUserName = "TWOFACTOR@EXAMPLE.COM",
                Email = "twofactor@example.com",
                NormalizedEmail = "TWOFACTOR@EXAMPLE.COM",
                EmailConfirmed = true,
                FullName = "Two Factor User",
                CreatedAt = DateTime.UtcNow
            };

            var created = await UserManager.CreateAsync(user, "Secret@123");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            return user;
        }

        public async Task<ApplicationUser> CreateTwoFactorUserAsync()
        {
            var user = await CreatePlainUserAsync();
            var key = await UserManager.ResetAuthenticatorKeyAsync(user);
            Assert.True(key.Succeeded);
            var enabled = await UserManager.SetTwoFactorEnabledAsync(user, true);
            Assert.True(enabled.Succeeded);
            return user;
        }

        public void SignInAs(ApplicationUser user)
        {
            Http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.UserName ?? string.Empty)
                },
                "Test"));
        }

        public void Dispose()
        {
            Cache.Dispose();
            Db.Dispose();
            Conn.Dispose();
        }
    }

    private static IServiceProvider BuildServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new StubAuthenticationService());
        return services.BuildServiceProvider();
    }

    private static SignInManager<ApplicationUser> CreateSignInManager(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        HttpContext http)
    {
        var claimsFactory = new UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<int>>(
            userManager, roleManager, TestUserManagers.ProgramLikeOptions());
        return new SignInManager<ApplicationUser>(
            userManager,
            new HttpContextAccessor { HttpContext = http },
            claimsFactory,
            TestUserManagers.ProgramLikeOptions(),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new StubSchemeProvider(),
            new AlwaysConfirmed());
    }

    private static async Task<(string Challenge, IActionResult Result)> StartLoginAsync(
        Ctx ctx, ApplicationUser user)
    {
        var result = await ctx.Controller.Login(new LoginViewModel
        {
            Username = user.Email!,
            Password = "Secret@123"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.TwoFactor), redirect.ActionName);
        var challenge = Assert.IsType<string>(redirect.RouteValues!["challenge"]);
        return (challenge, result);
    }

    [Fact]
    public async Task Login_WithTwoFactorEnabled_IssuesChallengeInsteadOfSigningIn()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();

        var (challenge, _) = await StartLoginAsync(ctx, user);

        Assert.Equal(32, challenge.Length);
        var view = Assert.IsType<ViewResult>(await ctx.Controller.TwoFactor(challenge));
        var model = Assert.IsType<TwoFactorLoginViewModel>(view.Model);
        Assert.Equal(challenge, model.ChallengeId);
    }

    [Fact]
    public async Task TwoFactor_InvalidCodes_LockChallengeAfterFiveAttempts()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();
        var (challenge, _) = await StartLoginAsync(ctx, user);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ctx.Controller.ModelState.Clear();
            var result = await ctx.Controller.TwoFactor(new TwoFactorLoginViewModel
            {
                ChallengeId = challenge,
                Code = "ABCDE-FGHIJ"
            });
            Assert.IsType<ViewResult>(result);
        }

        ctx.Controller.ModelState.Clear();
        var locked = await ctx.Controller.TwoFactor(new TwoFactorLoginViewModel
        {
            ChallengeId = challenge,
            Code = "ABCDE-FGHIJ"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(locked);
        Assert.Equal(nameof(AccountController.Login), redirect.ActionName);
        Assert.IsType<RedirectToActionResult>(
            await ctx.Controller.TwoFactor(new TwoFactorLoginViewModel
            {
                ChallengeId = challenge,
                Code = "ABCDE-FGHIJ"
            }));
    }

    [Fact]
    public async Task TwoFactor_WithValidAuthenticatorCode_SignsInAndTrustsDevice()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();
        var (challenge, _) = await StartLoginAsync(ctx, user);
        Assert.True(await ctx.UserManager.GetTwoFactorEnabledAsync(user));
        var key = await ctx.UserManager.GetAuthenticatorKeyAsync(user);
        Assert.False(string.IsNullOrEmpty(key));
        var code = ComputeTotp(key!);

        ctx.Controller.ModelState.Clear();
        var result = await ctx.Controller.TwoFactor(new TwoFactorLoginViewModel
        {
            ChallengeId = challenge,
            Code = code,
            RememberDevice = true,
            DeviceName = "Unit test browser"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Dashboard", redirect.ControllerName);
        var devices = await ctx.TrustedDevices.GetDevicesAsync(user.Id);
        Assert.Single(devices);
        Assert.Equal("Unit test browser", devices[0].DeviceName);
        Assert.Contains(
            TrustedDeviceCookie.Name,
            ctx.Http.Response.Headers.SetCookie.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithValidTrustedDevice_SkipsTwoFactorChallenge()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();
        var token = TrustedDeviceCookie.NewToken();
        await ctx.TrustedDevices.CreateAsync(user.Id, "Test browser", "UnitTest", token);
        ctx.Http.Request.Headers.Cookie = TrustedDeviceCookie.Name + "=" + token;

        var result = await ctx.Controller.Login(new LoginViewModel
        {
            Username = user.Email!,
            Password = "Secret@123"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Dashboard", redirect.ControllerName);
    }

    [Fact]
    public async Task Login_WithExpiredTrustedDevice_StillRequiresTwoFactor()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();
        var token = TrustedDeviceCookie.NewToken();
        var device = await ctx.TrustedDevices.CreateAsync(
            user.Id, "Expired browser", "UnitTest", token);
        device.ExpiresAtUtc = DateTime.UtcNow.AddDays(-1);
        await ctx.Db.SaveChangesAsync();
        ctx.Http.Request.Headers.Cookie = TrustedDeviceCookie.Name + "=" + token;

        var result = await ctx.Controller.Login(new LoginViewModel
        {
            Username = user.Email!,
            Password = "Secret@123"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.TwoFactor), redirect.ActionName);
    }

    [Fact]
    public async Task TwoFactor_WithRecoveryCode_SignsIn()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreateTwoFactorUserAsync();
        var recoveryCodes = await ctx.UserManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        Assert.NotNull(recoveryCodes);
        var recoveryCode = recoveryCodes!.First();
        var (challenge, _) = await StartLoginAsync(ctx, user);

        ctx.Controller.ModelState.Clear();
        var result = await ctx.Controller.TwoFactor(new TwoFactorLoginViewModel
        {
            ChallengeId = challenge,
            Code = recoveryCode.ToLowerInvariant().Replace("-", " ")
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Dashboard", redirect.ControllerName);
        Assert.Equal(9, await ctx.UserManager.CountRecoveryCodesAsync(user));
    }

    [Fact]
    public async Task EnableTwoFactor_WithValidCode_EnablesAndReturnsRecoveryCodes()
    {
        using var ctx = new Ctx();
        var user = await ctx.CreatePlainUserAsync();
        ctx.SignInAs(user);

        var get = Assert.IsType<ViewResult>(await ctx.Controller.EnableTwoFactor());
        var setup = Assert.IsType<EnableTwoFactorViewModel>(get.Model);
        Assert.False(string.IsNullOrWhiteSpace(setup.SharedKey));
        Assert.StartsWith("data:image/png;base64,", setup.QrCodeUrl);

        var key = await ctx.UserManager.GetAuthenticatorKeyAsync(user);
        Assert.False(string.IsNullOrEmpty(key));

        ctx.Controller.ModelState.Clear();
        var post = Assert.IsType<ViewResult>(await ctx.Controller.EnableTwoFactor(
            new EnableTwoFactorViewModel { Code = ComputeTotp(key!) }));

        Assert.Equal("TwoFactorRecoveryCodes", post.ViewName);
        var recovery = Assert.IsType<TwoFactorRecoveryCodesViewModel>(post.Model);
        Assert.Equal(10, recovery.RecoveryCodes.Count);
        Assert.True(await ctx.UserManager.GetTwoFactorEnabledAsync(user));
        Assert.Equal(10, await ctx.UserManager.CountRecoveryCodesAsync(user));
    }

    private static string ComputeTotp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var keyBytes = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in base32Key.TrimEnd('=').ToUpperInvariant())
        {
            var value = alphabet.IndexOf(c);
            if (value < 0)
            {
                continue;
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                keyBytes.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(timestep & 0xff);
            timestep >>= 8;
        }

        using var hmac = new HMACSHA1(keyBytes.ToArray());
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private sealed class FakeUrlHelper : IUrlHelper
    {
        public Microsoft.AspNetCore.Mvc.ActionContext ActionContext => throw new NotSupportedException();

        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext)
            => "/Dashboard";

        public string? Action(string? action, string? controller, object? values,
            string? protocol, string? host, string? fragment) => "/Dashboard";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => url != null && url.StartsWith('/');

        public string? Link(string? routeName, object? values) => "/Dashboard";

        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => null;
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class AlwaysConfirmed : IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(true);
    }

    private sealed class StubSchemeProvider : IAuthenticationSchemeProvider
    {
        public Task<AuthenticationScheme?> GetSchemeAsync(string name)
            => Task.FromResult<AuthenticationScheme?>(null);

        public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
            => Task.FromResult<IEnumerable<AuthenticationScheme>>(Array.Empty<AuthenticationScheme>());

        public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
            => Task.FromResult<IEnumerable<AuthenticationScheme>>(Array.Empty<AuthenticationScheme>());

        public Task<bool> CanHandleSchemeAsync(string name) => Task.FromResult(false);

        public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync()
            => Task.FromResult<AuthenticationScheme?>(null);

        public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync()
            => Task.FromResult<AuthenticationScheme?>(null);

        public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync()
            => Task.FromResult<AuthenticationScheme?>(null);

        public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync()
            => Task.FromResult<AuthenticationScheme?>(null);

        public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync()
            => Task.FromResult<AuthenticationScheme?>(null);

        public void AddScheme(AuthenticationScheme scheme) { }

        public void RemoveScheme(string name) { }
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }
}


