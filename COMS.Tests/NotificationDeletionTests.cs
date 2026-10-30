using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for resident notification deletion (fix #4):
/// verifies ownership enforcement, AJAX JSON response, and graceful 404.
/// </summary>
public sealed class NotificationDeletionTests
{
    private sealed class Ctx : IDisposable
    {
        public SqliteConnection Conn { get; } = new("DataSource=:memory:");
        public ApplicationDbContext Db { get; }

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
            Conn.Dispose();
        }
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

    private sealed class FakeLookups : ILookupService
    {
        public Task<List<string>> GetValuesAsync(string category) => Task.FromResult(new List<string>());
        public Task<List<LookupOption>> GetOptionsAsync(string category) => Task.FromResult(new List<LookupOption>());
        public Task<bool> IsValidAsync(string category, string? value) => Task.FromResult(true);
        public void Invalidate(string? category = null) { }
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static void AsUser(Controller controller, int userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Role, "Resident")
        };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
        };
        controller.TempData = new TempDataDictionary(http, new FakeTempDataProvider());
    }

    private static NotificationService MakeNotificationService(ApplicationDbContext db)
    {
        var mgr = TestUserManagers.CreateUserManager(db);
        return new NotificationService(db, NullLogger<NotificationService>.Instance,
            new NullHubContext(), mgr);
    }

    private static NotificationsController MakeController(ApplicationDbContext db)
    {
        return new NotificationsController(
            MakeNotificationService(db),
            db,
            TestUserManagers.CreateUserManager(db),
            new FakeLookups());
    }

    /// <summary>
    /// Creates a real <see cref="ApplicationUser"/> (so the Notification FK to
    /// AspNetUsers is satisfied) and returns the user's integer Id.
    /// </summary>
    private static async Task<int> CreateUserIdAsync(ApplicationDbContext db, string userName)
    {
        var mgr = TestUserManagers.CreateUserManager(db);
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.com",
            FullName = userName,
            EmailConfirmed = true
        };
        var result = await mgr.CreateAsync(user, "Secret@123!");
        Assert.True(result.Succeeded, $"User creation failed: {string.Join("; ", result.Errors)}");
        return user.Id;
    }

    private static async Task<Notification> CreateNotificationAsync(
        ApplicationDbContext db, int userId, string title = "Test Alert")
    {
        var notification = new Notification
        {
            UserId = userId,
            Title = title,
            Message = "Something happened",
            Type = "Alert",
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification;
    }

    [Fact]
    public async Task Delete_JsonRequest_OwnNotification_DeletesAndReturnsCount()
    {
        using var ctx = new Ctx();
        var userId = await CreateUserIdAsync(ctx.Db, "resident1");
        var notification = await CreateNotificationAsync(ctx.Db, userId, "My Alert");

        var controller = MakeController(ctx.Db);
        AsUser(controller, userId);
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        var result = await controller.Delete(notification.NotificationId);
        var json = Assert.IsType<JsonResult>(result);
        var payload = json.Value as dynamic;

        Assert.True((bool)payload.success);
        Assert.Equal(notification.NotificationId, (int)payload.notificationId);
        Assert.Equal(0, (int)payload.unreadCount);
        Assert.False(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == notification.NotificationId));
    }

    [Fact]
    public async Task Delete_JsonRequest_OtherUsersNotification_ReturnsSuccessFalse()
    {
        using var ctx = new Ctx();
        var ownUserId = await CreateUserIdAsync(ctx.Db, "resident2");
        var otherUserId = await CreateUserIdAsync(ctx.Db, "resident3");
        var ownNotification = await CreateNotificationAsync(ctx.Db, ownUserId, "My Alert");
        var otherNotification = await CreateNotificationAsync(ctx.Db, otherUserId, "Someone else's Alert");

        var controller = MakeController(ctx.Db);
        AsUser(controller, ownUserId);
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        var result = await controller.Delete(otherNotification.NotificationId);
        var json = Assert.IsType<JsonResult>(result);
        var payload = json.Value as dynamic;

        Assert.False((bool)payload.success);
        Assert.True(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == otherNotification.NotificationId));
        Assert.True(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == ownNotification.NotificationId));
    }

    [Fact]
    public async Task Delete_JsonRequest_MissingNotification_ReturnsSuccessFalse()
    {
        using var ctx = new Ctx();
        var userId = await CreateUserIdAsync(ctx.Db, "resident4");
        var notification = await CreateNotificationAsync(ctx.Db, userId);

        var controller = MakeController(ctx.Db);
        AsUser(controller, userId);
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        var result = await controller.Delete(9999);
        var json = Assert.IsType<JsonResult>(result);
        var payload = json.Value as dynamic;

        Assert.False((bool)payload.success);
        Assert.Equal("Notification not found.", (string)payload.message);
        Assert.True(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == notification.NotificationId));
    }

    [Fact]
    public async Task Delete_JsonRequest_InvalidId_ReturnsBadRequestJson()
    {
        using var ctx = new Ctx();
        var userId = await CreateUserIdAsync(ctx.Db, "resident5");
        var controller = MakeController(ctx.Db);
        AsUser(controller, userId);
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        var result = await controller.Delete(0);
        var json = Assert.IsType<JsonResult>(result);
        var payload = json.Value as dynamic;

        Assert.False((bool)payload.success);
        Assert.Equal("Invalid notification.", (string)payload.message);
    }

    [Fact]
    public async Task Delete_NonAjaxRequest_OwnNotification_RedirectsWithSuccess()
    {
        using var ctx = new Ctx();
        var userId = await CreateUserIdAsync(ctx.Db, "resident6");
        var notification = await CreateNotificationAsync(ctx.Db, userId);

        var controller = MakeController(ctx.Db);
        AsUser(controller, userId);
        // No X-Requested-With header = non-AJAX

        var result = await controller.Delete(notification.NotificationId);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(NotificationsController.Index), redirect.ActionName);
        Assert.False(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == notification.NotificationId));
        Assert.Equal("Notification deleted.", controller.TempData["SuccessMessage"]);
    }

    [Fact]
    public async Task NotificationService_DeleteAsync_OwnershipEnforced()
    {
        using var ctx = new Ctx();
        var service = MakeNotificationService(ctx.Db);
        var ownerId = await CreateUserIdAsync(ctx.Db, "resident7");
        var intruderId = await CreateUserIdAsync(ctx.Db, "resident8");
        var notification = await CreateNotificationAsync(ctx.Db, ownerId, "Owned");

        var ownResult = await service.DeleteAsync(notification.NotificationId, ownerId);
        Assert.True(ownResult);
        Assert.False(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == notification.NotificationId));

        var notification2 = await CreateNotificationAsync(ctx.Db, ownerId, "Other's notification");
        var otherResult = await service.DeleteAsync(notification2.NotificationId, intruderId);
        Assert.False(otherResult);
        Assert.True(await ctx.Db.Notifications.AnyAsync(n => n.NotificationId == notification2.NotificationId));
    }
}
