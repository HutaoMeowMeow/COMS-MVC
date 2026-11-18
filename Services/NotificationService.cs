using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using COMS_MVC.Hubs;

namespace COMS_MVC.Services
{
    public interface INotificationService
    {
        Task CreateNotificationAsync(int userId, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null);
        Task CreateNotificationForRoleAsync(string role, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null);
        Task CreateNotificationForRolesAsync(string[] roles, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null);
         Task<bool> MarkAsReadAsync(int notificationId, int userId);
        Task<bool> DeleteAsync(int notificationId, int userId);
        Task<int> GetUnreadCountAsync(int userId);
        Task<List<Notification>> GetRecentAsync(int userId, int count = 10);

        Task<List<Notification>> GetAllAsync(int userId);

        /// <summary>Server-side page of the user's own notifications.</summary>
        Task<NotificationPage> GetPageAsync(int userId, int page, int pageSize);

        /// <summary>Marks all of the user's unread notifications read.</summary>
        Task<int> MarkAllAsReadAsync(int userId);

        /// <summary>Deletes all of the user's own notifications (bulk).</summary>
        Task<int> DeleteAllAsync(int userId);
    }

    public sealed class NotificationPage
    {
        public List<Notification> Items { get; set; } = new();

        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 10;

        public int TotalCount { get; set; }

        public int UnreadCount { get; set; }

        public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    }

    public class NotificationService : INotificationService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NotificationService> _logger;
        private readonly IHubContext<MonitoringHub> _hubContext;
        private readonly UserManager<ApplicationUser> _userManager;

        public NotificationService(ApplicationDbContext context,
            ILogger<NotificationService> logger,
            IHubContext<MonitoringHub> hubContext,
            UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _logger = logger;
            _hubContext = hubContext;
            _userManager = userManager;
        }

        public async Task CreateNotificationAsync(int userId, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null)
        {
            title = (title ?? string.Empty).Trim();
            if (title.Length > 150) title = title.Substring(0, 150);
            message = (message ?? string.Empty).Trim();
            if (message.Length > 2000) message = message.Substring(0, 2000);
            type = string.IsNullOrWhiteSpace(type) ? "General" : type.Trim();
            var now = DateTime.UtcNow;

            // Structured destination: callers that only know about an alert or
            // report keep working; the entity reference is inferred from them.
            entityType = string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim();
            if (entityType == null)
            {
                if (alertId.HasValue) entityType = "Alert";
                else if (reportId.HasValue) entityType = "Report";
            }
            entityId ??= alertId ?? reportId;
            if (entityType != null && entityType.Length > 50)
            {
                entityType = entityType.Substring(0, 50);
            }

            // Idempotency: skip exact duplicates created in the last 60 seconds
            // (double-submit, retry, or reconnect replay).
            var cutoff = now.AddSeconds(-60);
            var duplicate = await _context.Notifications.AnyAsync(n =>
                n.UserId == userId && n.Title == title && n.Message == message &&
                n.Type == type && n.CreatedAt >= cutoff);
            if (duplicate)
            {
                _logger.LogInformation("Skipped duplicate notification for user {UserId}.", userId);
                return;
            }

            var notification = new Notification
            {
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                RelatedAlertId = alertId,
                RelatedReportId = reportId,
                EntityType = entityType,
                EntityId = entityId,
                IsRead = false,
                CreatedAt = now
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationReceived", new
            {
                notification.NotificationId,
                notification.Title,
                notification.Message,
                notification.Type,
                notification.CreatedAt
            });

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationCountUpdated", await GetUnreadCountAsync(userId));
        }

        public async Task CreateNotificationForRoleAsync(string role, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null)
        {
            await CreateNotificationForRolesAsync(new[] { role }, title, message, type, alertId, reportId, entityType, entityId);
        }

        public async Task CreateNotificationForRolesAsync(string[] roles, string title, string message, string type, int? alertId = null, int? reportId = null, string? entityType = null, int? entityId = null)
        {
            var users = new List<ApplicationUser>();
            foreach (var role in roles)
            {
                var roleUsers = await _userManager.GetUsersInRoleAsync(role);
                users.AddRange(roleUsers);
            }

            var uniqueUsers = users.GroupBy(u => u.Id).Select(g => g.First()).ToList();

            foreach (var user in uniqueUsers)
            {
                await CreateNotificationAsync(user.Id, title, message, type, alertId, reportId, entityType, entityId);
            }
        }

        public async Task<bool> MarkAsReadAsync(int notificationId, int userId)
        {
            // Set-based update: no entity is loaded, and ownership is part of
            // the WHERE clause so another user's row can never be touched.
            var updated = await _context.Notifications
                .Where(n => n.NotificationId == notificationId && n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

            if (updated == 0)
            {
                // Already read or not owned: report ownership accurately.
                return await _context.Notifications
                    .AsNoTracking()
                    .AnyAsync(n => n.NotificationId == notificationId && n.UserId == userId);
            }

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationRead", notificationId);
            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationCountUpdated", await GetUnreadCountAsync(userId));

            return true;
        }

        public async Task<bool> DeleteAsync(int notificationId, int userId)
        {
            var deleted = await _context.Notifications
                .Where(n => n.NotificationId == notificationId && n.UserId == userId)
                .ExecuteDeleteAsync();

            if (deleted == 0)
            {
                return false;
            }

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationCountUpdated", await GetUnreadCountAsync(userId));

            return true;
        }

        public async Task<int> MarkAllAsReadAsync(int userId)
        {
            var updated = await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationCountUpdated", 0);
            return updated;
        }

        public async Task<int> DeleteAllAsync(int userId)
        {
            // Bulk delete in a single statement, scoped to the authenticated
            // user only. Other users' and staff notifications are untouched.
            var deleted = await _context.Notifications
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync();

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationCountUpdated", 0);
            return deleted;
        }

        public async Task<int> GetUnreadCountAsync(int userId)
        {
            return await _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserId == userId && !n.IsRead)
                .CountAsync();
        }

        public async Task<List<Notification>> GetRecentAsync(int userId, int count = 10)
        {
            return await _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<Notification>> GetAllAsync(int userId)
        {
            return await _context.Notifications
                .AsNoTracking()
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport)
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();
        }

        public async Task<NotificationPage> GetPageAsync(int userId, int page, int pageSize)
        {
            pageSize = Math.Clamp(pageSize, 1, 50);
            page = Math.Max(1, page);
            var query = _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserId == userId);

            var total = await query.CountAsync();
            var unread = await query.CountAsync(n => !n.IsRead);
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            if (page > totalPages)
            {
                page = totalPages;
            }

            var items = await query
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport)
                .OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return new NotificationPage
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                UnreadCount = unread
            };
        }
    }
}
