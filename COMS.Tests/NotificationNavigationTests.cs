using System.Security.Claims;
using COMS_MVC.Controllers;
using COMS_MVC.Data;
using COMS_MVC.Helpers;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Notification clicks must resolve to the real record/module, enforce
/// ownership, mark only the owner's row read, and degrade gracefully when
/// the linked record no longer exists.
/// </summary>
public sealed class NotificationNavigationTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;

    public NotificationNavigationTests()
    {
        _conn.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private sealed class NullProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullProxy();
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

    private static void AsUser(Controller controller, int userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Role, role)
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

    private NotificationsController MakeController()
    {
        var userManager = TestUserManagers.CreateUserManager(_db);
        var service = new NotificationService(_db, NullLogger<NotificationService>.Instance,
            new NullHubContext(), userManager);
        return new NotificationsController(service, _db, userManager, new FakeLookups());
    }

    private async Task<int> CreateUserAsync(string userName, string role)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.com",
            FullName = userName,
            EmailConfirmed = true
        };
        var result = await TestUserManagers.CreateUserManager(_db)
            .CreateAsync(user, "Secret@123!");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var roleManager = TestUserManagers.CreateRoleManager(_db);
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole<int>(role));
        }
        await TestUserManagers.CreateUserManager(_db).AddToRoleAsync(user, role);
        return user.Id;
    }

    private async Task<Notification> AddNotificationAsync(
        int userId, string type, string? entityType = null, int? entityId = null)
    {
        var notification = new Notification
        {
            UserId = userId,
            Title = "Test notification",
            Message = "Test message",
            Type = type,
            EntityType = entityType,
            EntityId = entityId,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        };
        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();
        return notification;
    }

    [Fact]
    public async Task Open_ReportNotification_RedirectsToThatReport_AndMarksRead()
    {
        var adminId = await CreateUserAsync("admin-report", "Admin");
        var residentId = await CreateUserAsync("resident-report", "Resident");
        var report = new CommunityReport
        {
            UserId = residentId,
            ReportType = "Garbage / Debris",
            Title = "Blocked canal",
            Description = "Plastic waste blocking the waterway.",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CommunityReports.Add(report);
        await _db.SaveChangesAsync();

        var notification = await AddNotificationAsync(
            adminId, "Report", NotificationLinks.Report, report.CommunityReportId);

        var controller = MakeController();
        AsUser(controller, adminId, "Admin");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Reports", redirect.ControllerName);
        Assert.Equal(report.CommunityReportId, redirect.RouteValues!["id"]);
        // ExecuteUpdate bypasses the change tracker: read the row fresh.
        _db.ChangeTracker.Clear();
        Assert.True((await _db.Notifications.AsNoTracking().SingleAsync(
            n => n.NotificationId == notification.NotificationId)).IsRead);
    }

    [Fact]
    public async Task Open_SupportTicketNotification_RedirectsStaffToManageConversation()
    {
        var adminId = await CreateUserAsync("admin-support", "Admin");
        var residentId = await CreateUserAsync("resident-support", "Resident");
        var ticket = new SupportTicket
        {
            UserId = residentId,
            Subject = "Cannot upload photo",
            Description = "The report form rejects my photo.",
            Category = "Technical",
            Priority = "Normal",
            Status = "Open",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync();

        var notification = await AddNotificationAsync(
            adminId, "Support", NotificationLinks.SupportTicket, ticket.SupportTicketId);

        var controller = MakeController();
        AsUser(controller, adminId, "Admin");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Manage", redirect.ActionName);
        Assert.Equal("CustomerService", redirect.ControllerName);
        Assert.Equal(ticket.SupportTicketId, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task Open_SupportTicketNotification_RedirectsResidentToOwnTicket()
    {
        var adminId = await CreateUserAsync("admin-own-ticket", "Admin");
        var residentId = await CreateUserAsync("resident-own-ticket", "Resident");
        var ticket = new SupportTicket
        {
            UserId = residentId,
            Subject = "Canal behind the house",
            Description = "Water does not drain.",
            Category = "Technical",
            Priority = "Normal",
            Status = "Open",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync();

        var notification = await AddNotificationAsync(
            residentId, "Support", NotificationLinks.SupportTicket, ticket.SupportTicketId);
        _ = adminId;

        var controller = MakeController();
        AsUser(controller, residentId, "Resident");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Ticket", redirect.ActionName);
        Assert.Equal("CustomerService", redirect.ControllerName);
        Assert.Equal(ticket.SupportTicketId, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task Open_AlertNotification_RedirectsToAlertDetails()
    {
        var adminId = await CreateUserAsync("admin-alert", "Admin");
        var canal = new Canal
        {
            CanalName = "Test Canal",
            Location = "Test Street",
            Barangay = "Centro",
            City = "Cebu City"
        };
        _db.Canals.Add(canal);
        await _db.SaveChangesAsync();

        var alert = new ObstructionAlert
        {
            CanalId = canal.CanalId,
            AlertType = "Obstruction",
            Severity = "High",
            Title = "Debris detected",
            Description = "Debris is blocking the canal.",
            ObstructionType = "Plastic",
            WaterLevel = 1.2,
            DetectedAt = DateTime.UtcNow,
            Status = "Active"
        };
        _db.ObstructionAlerts.Add(alert);
        await _db.SaveChangesAsync();

        var notification = await AddNotificationAsync(
            adminId, "Alert", NotificationLinks.Alert, alert.ObstructionAlertId);

        var controller = MakeController();
        AsUser(controller, adminId, "Admin");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Alerts", redirect.ControllerName);
        Assert.Equal(alert.ObstructionAlertId, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task Open_AnnouncementNotification_RedirectsResidentToDetails()
    {
        var adminId = await CreateUserAsync("admin-announce", "Admin");
        var residentId = await CreateUserAsync("resident-announce", "Resident");
        var announcement = new Announcement
        {
            Title = "Canal cleanup",
            Content = "Cleanup this Saturday.",
            TargetAudience = "Resident",
            PostedByUserId = adminId,
            CreatedAt = DateTime.UtcNow
        };
        _db.Announcements.Add(announcement);
        await _db.SaveChangesAsync();

        var notification = await AddNotificationAsync(
            residentId, "Announcement", NotificationLinks.Announcement, announcement.AnnouncementId);

        var controller = MakeController();
        AsUser(controller, residentId, "Resident");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Announcements", redirect.ControllerName);
        Assert.Equal(announcement.AnnouncementId, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task Open_AnotherUsersNotification_IsRejectedAndLeftUnread()
    {
        var ownerId = await CreateUserAsync("notification-owner", "Resident");
        var intruderId = await CreateUserAsync("notification-intruder", "Resident");
        var notification = await AddNotificationAsync(ownerId, "Report", NotificationLinks.Report, 123);

        var controller = MakeController();
        AsUser(controller, intruderId, "Resident");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(NotificationsController.Index), redirect.ActionName);
        Assert.NotNull(controller.TempData["ErrorMessage"]);
        _db.ChangeTracker.Clear();
        Assert.False((await _db.Notifications.AsNoTracking().SingleAsync(
            n => n.NotificationId == notification.NotificationId)).IsRead);
    }

    [Fact]
    public async Task Open_MissingSupportTicket_FallsBackToModuleWithFriendlyMessage()
    {
        var adminId = await CreateUserAsync("admin-missing", "Admin");
        var notification = await AddNotificationAsync(
            adminId, "Support", NotificationLinks.SupportTicket, 999999);

        var controller = MakeController();
        AsUser(controller, adminId, "Admin");
        var result = await controller.Open(notification.NotificationId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Dashboard", redirect.ActionName);
        Assert.Equal("CustomerService", redirect.ControllerName);
        Assert.NotNull(controller.TempData["AlertMessage"]);
        _db.ChangeTracker.Clear();
        Assert.True((await _db.Notifications.AsNoTracking().SingleAsync(
            n => n.NotificationId == notification.NotificationId)).IsRead);
    }

    [Fact]
    public async Task ResolveEntity_UsesLegacyAlertAndReportColumns()
    {
        var alertNotification = new Notification { Type = "Alert", RelatedAlertId = 42 };
        var reportNotification = new Notification { Type = "Report", RelatedReportId = 77 };

        Assert.Equal((NotificationLinks.Alert, (int?)42),
            NotificationLinks.ResolveEntity(alertNotification));
        Assert.Equal((NotificationLinks.Report, (int?)77),
            NotificationLinks.ResolveEntity(reportNotification));
    }
}
