using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Problems 1/4/5: end-to-end registration workflow through the real
/// AccountController (direct POSTs, so browser validation is bypassed by
/// construction). Uses Sqlite in-memory + mocked email. Verifies invalid
/// data stays on the registration page with field errors and causes NO
/// pending row and NO email, while valid data reaches OTP verification.
/// </summary>
public sealed class RegistrationWorkflowTests
{
    private sealed class FakeEmail : COMS_MVC.Services.IEmailService
    {
        public int Sends;
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
            => SendEmailAsync(to, "otp", code, ct);
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ApplicationDbContext Db { get; private set; }
        public AccountController Controller { get; private set; }
        public FakeEmail Email { get; } = new();

        public Ctx()
        {
            Conn.Open();
            Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Conn).Options);
            Db.Database.EnsureCreated();
            var otp = new COMS_MVC.Services.OtpService(Email, NullLogger<COMS_MVC.Services.OtpService>.Instance);
            Controller = new AccountController(
                TestUserManagers.CreateUserManager(Db),
                null!,
                TestUserManagers.CreateRoleManager(Db),
                Email, otp, Db,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                NullLogger<AccountController>.Instance);
            var http = new DefaultHttpContext();
            Controller.ControllerContext = new ControllerContext
            {
                HttpContext = http,
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
            };
            Controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
        }

        public void Dispose()
        {
            Db.Dispose();
            Conn.Dispose();
        }
    }

    private static RegisterViewModel Valid(string tag) => new()
    {
        FullName = "Maria Santos",
        UserName = $"user{tag}",
        Email = "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM",
        PhoneNumber = "09123456789",
        Password = "Secret@123",
        ConfirmPassword = "Secret@123",
        Barangay = "Centro",
        City = "Cebu City",
        Address = "123 Mango St.",
        Role = "Resident"
    };

    private static void AssertFieldError(AccountController c, string field)
    {
        Assert.True(c.ModelState.TryGetValue(field, out var entry)
            && entry!.Errors.Count > 0, $"Expected a '{field}' error.");
    }

    [Fact]
    public async Task ValidRegistration_ProceedsToOtp_WithPendingAndEmail()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var result = await ctx.Controller.Register(Valid(tag));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.VerifyEmail), redirect.ActionName);
        Assert.Equal(1, ctx.Email.Sends);
        var pending = await ctx.Db.PendingRegistrations
            .SingleOrDefaultAsync(p => p.NormalizedEmail == "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM");
        Assert.NotNull(pending);
        Assert.Equal($"user{tag}", pending.UserName);
        Assert.Equal("Resident", pending.Role);
        Assert.NotNull(pending.OtpHash);
        Assert.True(pending.OtpExpiresAtUtc > DateTime.UtcNow);
        // No real account exists yet.
        Assert.Empty(await ctx.Db.Users.ToListAsync());
    }

    [Fact]
    public async Task UsernameWithSpaces_Accepted_EndToEnd()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.UserName = "Manok Manok";

        var result = await ctx.Controller.Register(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.VerifyEmail), redirect.ActionName);
        Assert.Equal(1, ctx.Email.Sends);
        var pending = await ctx.Db.PendingRegistrations
            .SingleOrDefaultAsync(p => p.NormalizedEmail == "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM");
        Assert.NotNull(pending);
        Assert.Equal("Manok Manok", pending.UserName);
    }

    [Fact]
    public async Task Username_TooLong_Rejected_NoSideEffects()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.UserName = new string('a', 31);

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.UserName));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task InvalidEmail_Rejected_WithoutOtp()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.Email = "not-an-email";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.Email));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task InvalidPhone_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.PhoneNumber = "abc123";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.PhoneNumber));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task MissingRequiredFields_Rejected()
    {
        using var ctx = new Ctx();
        var model = new RegisterViewModel
        {
            Password = "Secret@123",
            ConfirmPassword = "Secret@123",
            Role = "Resident"
        };
        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.FullName));
        AssertFieldError(ctx.Controller, nameof(model.UserName));
        AssertFieldError(ctx.Controller, nameof(model.Email));
        AssertFieldError(ctx.Controller, nameof(model.PhoneNumber));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task OverlongFields_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.FullName = new string('a', 101);
        model.UserName = new string('a', 257);
        model.Address = new string('a', 251);

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.FullName));
        AssertFieldError(ctx.Controller, nameof(model.UserName));
        AssertFieldError(ctx.Controller, nameof(model.Address));
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task WeakPassword_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.Password = "weakpass1";
        model.ConfirmPassword = "weakpass1";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.Password));
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task MismatchedConfirmation_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.ConfirmPassword = "Different@123";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.ConfirmPassword));
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task DuplicateEmail_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var existing = new ApplicationUser
        {
            UserName = $"someone{tag}", Email = $"dup{tag}@example.com",
            FullName = "Someone", EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(existing, "Secret@123!")).Succeeded);

        var model = Valid(tag);
        model.Email = $"dup{tag}@example.com";
        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.Email));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task DuplicateUsername_CaseInsensitive_Rejected()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var existing = new ApplicationUser
        {
            UserName = $"TestUser{tag}", Email = $"other{tag}@example.com",
            FullName = "Someone", EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(existing, "Secret@123!")).Succeeded);

        var model = Valid(tag);
        model.UserName = $"testuser{tag}";
        var result = await ctx.Controller.Register(model);

        Assert.IsType<ViewResult>(result);
        AssertFieldError(ctx.Controller, nameof(model.UserName));
        Assert.Equal(0, ctx.Email.Sends);
    }

    [Fact]
    public async Task DuplicatePending_Reused_WithoutDuplicates()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var first = await ctx.Controller.Register(Valid(tag));
        Assert.IsType<RedirectToActionResult>(first);
        Assert.Equal(1, await ctx.Db.PendingRegistrations.CountAsync());

        // Simulate a fresh POST (new ModelState) for the same email.
        ctx.Controller.ModelState.Clear();
        var second = await ctx.Controller.Register(Valid(tag));
        Assert.IsType<RedirectToActionResult>(second);
        Assert.Equal(1, await ctx.Db.PendingRegistrations.CountAsync());
    }

    [Fact]
    public async Task ForgedAdminRole_FallsBackToResident()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.Role = "Admin";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<RedirectToActionResult>(result);
        var pending = await ctx.Db.PendingRegistrations
            .SingleAsync(p => p.NormalizedEmail == "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM");
        Assert.Equal("Resident", pending.Role);
    }

    [Fact]
    public async Task PhoneSeparators_Normalized_WithoutChangingNumber()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var model = Valid(tag);
        model.PhoneNumber = "+63 912 345 6789";

        var result = await ctx.Controller.Register(model);

        Assert.IsType<RedirectToActionResult>(result);
        var pending = await ctx.Db.PendingRegistrations
            .SingleAsync(p => p.NormalizedEmail == "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM");
        Assert.Equal("+639123456789", pending.PhoneNumber);
    }

    [Fact]
    public async Task FullRegister_Verify_CreatesConfirmedUser()
    {
        using var ctx = new Ctx();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await TestUserManagers.CreateRoleManager(ctx.Db)
            .CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole<int> { Name = "Resident" });

        var registered = await ctx.Controller.Register(Valid(tag));
        Assert.IsType<RedirectToActionResult>(registered);
        var pending = await ctx.Db.PendingRegistrations
            .SingleAsync(p => p.NormalizedEmail == "USER" + tag.ToUpperInvariant() + "@EXAMPLE.COM");

        // Recover the issued code (hash check, same as OtpServiceTests).
        string? code = null;
        for (var n = 0; n < 1_000_000; n++)
        {
            var c = n.ToString("D6");
            if (c != "000000" && COMS_MVC.Services.OtpService.Hash(c, pending.Email) == pending.OtpHash)
            {
                code = c;
                break;
            }
        }
        Assert.NotNull(code);

        var verified = await ctx.Controller.VerifyEmail(new VerifyOtpViewModel
        {
            Email = pending.Email,
            Code = code!
        });
        var redirect = Assert.IsType<RedirectToActionResult>(verified);
        Assert.Equal(nameof(AccountController.Login), redirect.ActionName);

        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var user = await mgr.FindByEmailAsync(pending.Email);
        Assert.NotNull(user);
        Assert.True(user.EmailConfirmed);
        Assert.Contains("Resident", await mgr.GetRolesAsync(user));
        Assert.Equal(0, await ctx.Db.PendingRegistrations.CountAsync());
    }

    [Fact]
    public void NormalizePhone_StripsSeparators_Only()
    {
        Assert.Equal("+639123456789", AccountController.NormalizePhone("+63 912 345 6789"));
        Assert.Equal("09123456789", AccountController.NormalizePhone("0912-345-6789"));
        Assert.Equal("09123456789", AccountController.NormalizePhone("(0912) 345 6789"));
        Assert.Equal("09123456789", AccountController.NormalizePhone("09123456789"));
        Assert.Equal(string.Empty, AccountController.NormalizePhone("   "));
        Assert.Equal(string.Empty, AccountController.NormalizePhone(null));
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

    private sealed class AlwaysConfirmed : Microsoft.AspNetCore.Identity.IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(
            Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task ExistingUsers_CanStillLogIn_WithUsernamesAndPasswords()
    {
        using var ctx = new Ctx();
        // Pre-existing style accounts (no spaces) plus a spaced display name.
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        foreach (var (name, email, pwd) in new[]
        {
            ("admin", "admin@coms.gov", "Admin@COMS123!"),
            ("resident", "resident@coms.gov", "Resident@123!"),
            ("Manok Manok", "manok@example.com", "Secret@123!")
        })
        {
            var u = new ApplicationUser
            {
                UserName = name, Email = email, FullName = name,
                EmailConfirmed = true, CreatedAt = DateTime.UtcNow
            };
            Assert.True((await mgr.CreateAsync(u, pwd)).Succeeded);
        }

        var httpAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var claimsFactory = new UserClaimsPrincipalFactory<ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole<int>>(
            mgr, TestUserManagers.CreateRoleManager(ctx.Db), TestUserManagers.ProgramLikeOptions());
        var signIn = new SignInManager<ApplicationUser>(
            mgr, httpAccessor, claimsFactory, TestUserManagers.ProgramLikeOptions(),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new StubSchemeProvider(), new AlwaysConfirmed());

        // Correct credentials succeed (case-insensitive usernames, incl. spaces).
        foreach (var (login, pwd) in new[]
        {
            ("admin", "Admin@COMS123!"), ("ADMIN", "Admin@COMS123!"),
            ("resident", "Resident@123!"),
            ("Manok Manok", "Secret@123!"), ("manok manok", "Secret@123!")
        })
        {
            var user = await mgr.FindByNameAsync(login);
            Assert.NotNull(user);
            var check = await signIn.CheckPasswordSignInAsync(user, pwd, lockoutOnFailure: false);
            Assert.True(check.Succeeded, $"Login failed for '{login}'.");
        }

        // Wrong password still fails.
        var victim = await mgr.FindByNameAsync("admin");
        Assert.NotNull(victim);
        var bad = await signIn.CheckPasswordSignInAsync(victim, "Wrong@123!", lockoutOnFailure: false);
        Assert.False(bad.Succeeded);
    }

    [Fact]
    public async Task SpacedUsername_Uniqueness_IsCaseInsensitive()
    {
        using var ctx = new Ctx();
        var mgr = TestUserManagers.CreateUserManager(ctx.Db);
        var first = new ApplicationUser
        {
            UserName = "Manok Manok", Email = "one@example.com",
            FullName = "Manok", EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        Assert.True((await mgr.CreateAsync(first, "Secret@123!")).Succeeded);

        // Same name, different case → duplicate.
        Assert.NotNull(await mgr.FindByNameAsync("MANOK MANOK"));
        var dupe = new ApplicationUser
        {
            UserName = "MANOK MANOK", Email = "two@example.com",
            FullName = "Manok 2", EmailConfirmed = true, CreatedAt = DateTime.UtcNow
        };
        var result = await mgr.CreateAsync(dupe, "Secret@123!");
        Assert.False(result.Succeeded);
    }
}

