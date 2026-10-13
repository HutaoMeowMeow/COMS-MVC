using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Bug 1 verification: Admin account deletion through the real
/// <see cref="UsersController"/> against the isolated PostgreSQL schema.
/// Covers: eligible delete, delete with inbox rows, blocked delete with
/// historical records, nonexistent id, self-delete refusal, Admin-only
/// authorization, and role-management regression.
/// </summary>
[Collection(nameof(PgCollection))]
public sealed class UserDeletionTests
{
    private readonly PgFixture _fx;

    public UserDeletionTests(PgFixture fx) => _fx = fx;

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static UserManager<ApplicationUser> MakeUserManager(ApplicationDbContext db)
    {
        var store = new UserStore<ApplicationUser, IdentityRole<int>, ApplicationDbContext, int>(db);
        return new UserManager<ApplicationUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    private static RoleManager<IdentityRole<int>> MakeRoleManager(ApplicationDbContext db)
    {
        var store = new RoleStore<IdentityRole<int>, ApplicationDbContext, int>(db);
        return new RoleManager<IdentityRole<int>>(
            store,
            Array.Empty<IRoleValidator<IdentityRole<int>>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole<int>>>.Instance);
    }

    private static UsersController ControllerFor(
        ApplicationDbContext db, int actingAdminId, out TempDataDictionary tempData)
    {
        var controller = new UsersController(
            MakeUserManager(db), MakeRoleManager(db), db, null!, 
            NullLogger<UsersController>.Instance);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, actingAdminId.ToString()) }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        tempData = new TempDataDictionary(http, new FakeTempDataProvider());
        controller.TempData = tempData;
        return controller;
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        ApplicationDbContext db, string tag, string role)
    {
        var mgr = MakeUserManager(db);
        if (!await db.Roles.AnyAsync(r => r.Name == role))
        {
            await MakeRoleManager(db).CreateAsync(new IdentityRole<int> { Name = role });
        }
        var user = new ApplicationUser
        {
            UserName = $"deltest_{tag}",
            Email = $"deltest_{tag}@example.com",
            FullName = $"Delete Test {tag}",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };
        var created = await mgr.CreateAsync(user, "Test@1234!");
        Assert.True(created.Succeeded);
        var inRole = await mgr.AddToRoleAsync(user, role);
        Assert.True(inRole.Succeeded);
        return user;
    }

    [Fact]
    public async Task Delete_EligibleAccount_RemovesUser_AndReportsSuccess()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int adminId, targetId;
        using (var db = _fx.Open())
        {
            var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
            var target = await CreateUserAsync(db, $"t{tag}", "Resident");
            adminId = admin.Id;
            targetId = target.Id;
        }
        using (var db = _fx.Open())
        {
            var controller = ControllerFor(db, adminId, out var tempData);
            var result = await controller.Delete(targetId, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()));

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(UsersController.Index), redirect.ActionName);
            Assert.True(tempData.ContainsKey("SuccessMessage"));
        }
        using (var db = _fx.Open())
        {
            Assert.Null(await MakeUserManager(db).FindByIdAsync(targetId.ToString()));
        }
    }

    [Fact]
    public async Task Delete_AccountWithInbox_RemovesUserAndInbox()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int adminId, targetId;
        using (var db = _fx.Open())
        {
            var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
            var target = await CreateUserAsync(db, $"t{tag}", "Resident");
            adminId = admin.Id;
            targetId = target.Id;
            db.Notifications.Add(new Notification
            {
                UserId = targetId, Title = "Hello", Message = "inbox row",
                Type = "General", IsRead = false, CreatedAt = DateTime.UtcNow
            });
            db.Notifications.Add(new Notification
            {
                UserId = targetId, Title = "Hello 2", Message = "inbox row 2",
                Type = "Alert", IsRead = true, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using (var db = _fx.Open())
        {
            var controller = ControllerFor(db, adminId, out var tempData);
            var result = await controller.Delete(targetId, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()));
            Assert.IsType<RedirectToActionResult>(result);
            Assert.True(tempData.ContainsKey("SuccessMessage"));
        }
        using (var db = _fx.Open())
        {
            Assert.Null(await MakeUserManager(db).FindByIdAsync(targetId.ToString()));
            Assert.Empty(await db.Notifications.Where(n => n.UserId == targetId).ToListAsync());
        }
    }

    [Fact]
    public async Task Delete_AccountWithReport_IsBlocked_AndPreservesHistory()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int adminId, targetId, reportId;
        using (var db = _fx.Open())
        {
            var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
            var target = await CreateUserAsync(db, $"t{tag}", "Resident");
            adminId = admin.Id;
            targetId = target.Id;
            var report = new CommunityReport
            {
                UserId = targetId,
                ReportType = "Garbage / Debris",
                Title = $"History {tag}",
                Description = "must be preserved",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.CommunityReports.Add(report);
            await db.SaveChangesAsync();
            reportId = report.CommunityReportId;
        }
        using (var db = _fx.Open())
        {
            var controller = ControllerFor(db, adminId, out var tempData);
            var result = await controller.Delete(targetId, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()));
            Assert.IsType<RedirectToActionResult>(result);
            Assert.True(tempData.ContainsKey("ErrorMessage"));
            var msg = tempData["ErrorMessage"]!.ToString();
            Assert.Contains("community report", msg, StringComparison.OrdinalIgnoreCase);
        }
        using (var db = _fx.Open())
        {
            Assert.NotNull(await MakeUserManager(db).FindByIdAsync(targetId.ToString()));
            Assert.NotNull(await db.CommunityReports.FindAsync(reportId));
        }
    }

    [Fact]
    public async Task Delete_NonexistentAccount_ReturnsNotFound()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = _fx.Open();
        var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
        var controller = ControllerFor(db, admin.Id, out _);
        var result = await controller.Delete(-999, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()));
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_OwnAccount_IsRefused()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = _fx.Open();
        var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
        var controller = ControllerFor(db, admin.Id, out var tempData);
        var result = await controller.Delete(admin.Id, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()));
        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(tempData.ContainsKey("ErrorMessage"));
        Assert.NotNull(await MakeUserManager(db).FindByIdAsync(admin.Id.ToString()));
    }

    [Fact]
    public void UsersController_RequiresAdminRole()
    {
        var auth = typeof(UsersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>()
            .FirstOrDefault();
        Assert.NotNull(auth);
        Assert.Contains("Admin", (auth.Roles ?? string.Empty).Split(','));
    }

    [Fact]
    public async Task RoleManagement_StillWorks_AfterChanges()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = _fx.Open();
        var user = await CreateUserAsync(db, $"t{tag}", "Resident");
        var mgr = MakeUserManager(db);
        var add = await mgr.AddToRoleAsync(user, "Admin");
        Assert.True(add.Succeeded);
        Assert.Contains("Admin", await mgr.GetRolesAsync(user));
        var remove = await mgr.RemoveFromRoleAsync(user, "Resident");
        Assert.True(remove.Succeeded);
        Assert.DoesNotContain("Resident", await mgr.GetRolesAsync(user));
    }

    [Fact]
    public async Task RootCause_NotificationRows_BreakRawDeleteAsync()
    {
        // Demonstrates the reported bug mechanism: under the old code path
        // (direct UserManager.DeleteAsync), a user holding ordinary inbox
        // notification rows could NOT be deleted — the operation throws
        // (severed Restrict association when tracked; FK 23503 at the
        // database when untracked) and the Admin only ever saw an error.
        // The fixed controller removes the transient inbox atomically instead
        // (see Delete_AccountWithInbox_RemovesUserAndInbox).
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var db = _fx.Open();
        var target = await CreateUserAsync(db, $"t{tag}", "Resident");
        db.Notifications.Add(new Notification
        {
            UserId = target.Id, Title = "Broadcast", Message = "announcement inbox copy",
            Type = "Announcement", IsRead = false, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeUserManager(db).DeleteAsync(target));
        Assert.NotNull(await MakeUserManager(db).FindByIdAsync(target.Id.ToString()));
    }

    [Fact]
    public async Task Delete_Get_ShowsBlockers_ForAccountWithHistory()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int adminId, targetId;
        using (var db = _fx.Open())
        {
            var admin = await CreateUserAsync(db, $"a{tag}", "Admin");
            var target = await CreateUserAsync(db, $"t{tag}", "Resident");
            adminId = admin.Id;
            targetId = target.Id;
            db.CommunityReports.Add(new CommunityReport
            {
                UserId = targetId,
                ReportType = "Garbage / Debris",
                Title = $"History {tag}",
                Description = "x",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using (var db = _fx.Open())
        {
            var controller = ControllerFor(db, adminId, out _);
            var result = await controller.Delete(targetId);
            var view = Assert.IsType<ViewResult>(result);
            Assert.NotNull(view.ViewData["Blockers"]);
        }
    }
}
