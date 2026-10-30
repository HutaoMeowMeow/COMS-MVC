using COMS_MVC.Data;
using COMS_MVC.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace COMS.Tests;

/// <summary>Constructs real Identity managers over a test DbContext.</summary>
internal static class TestUserManagers
{
    /// <summary>IdentityOptions mirroring Program.cs (lowercase not required;
    /// display usernames may contain spaces).</summary>
    public static IOptions<IdentityOptions> ProgramLikeOptions()
    {
        var options = new IdentityOptions
        {
            Password =
            {
                RequireDigit = true,
                RequiredLength = 6,
                RequireNonAlphanumeric = true,
                RequireUppercase = true,
                RequireLowercase = false,
                RequiredUniqueChars = 1
            },
            Lockout =
            {
                DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5),
                MaxFailedAccessAttempts = 5
            }
        };
        options.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+ ";
        return Options.Create(options);
    }

    public static UserManager<ApplicationUser> CreateUserManager(
        ApplicationDbContext db, IOptions<IdentityOptions>? options = null,
        IDataProtectionProvider? protectionProvider = null)
    {
        var store = new UserStore<ApplicationUser, IdentityRole<int>, ApplicationDbContext, int>(db);
        var manager = new UserManager<ApplicationUser>(
            store,
            options ?? ProgramLikeOptions(),
            new PasswordHasher<ApplicationUser>(),
            new List<IUserValidator<ApplicationUser>> { new UserValidator<ApplicationUser>() },
            new List<IPasswordValidator<ApplicationUser>> { new PasswordValidator<ApplicationUser>() },
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        // Token providers (password-reset / change-email tokens). Production
        // wires these via AddDefaultTokenProviders(); tests use an ephemeral
        // in-memory protector — same token format, no persistence. Pass a
        // shared provider when two managers must agree on tokens.
        manager.RegisterTokenProvider(
            TokenOptions.DefaultProvider,
            new DataProtectorTokenProvider<ApplicationUser>(
                protectionProvider ?? new EphemeralDataProtectionProvider(),
                Options.Create(new DataProtectionTokenProviderOptions()),
                NullLogger<DataProtectorTokenProvider<ApplicationUser>>.Instance));
        return manager;
    }

    public static RoleManager<IdentityRole<int>> CreateRoleManager(ApplicationDbContext db)
    {
        var store = new RoleStore<IdentityRole<int>, ApplicationDbContext, int>(db);
        return new RoleManager<IdentityRole<int>>(
            store,
            Array.Empty<IRoleValidator<IdentityRole<int>>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole<int>>>.Instance);
    }
}
