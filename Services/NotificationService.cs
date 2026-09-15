using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using COMS_MVC.Hubs;

namespace COMS_MVC.Services
{
    public interface INotificationService
    {
        Task CreateNotificationAsync(int userId, string title, string message, string type, int? alertId = null, int? reportId = null);
        Task CreateNotificationForRoleAsync(string role, string title, string message, string type, int? alertId = null, int? reportId = null);
        Task CreateNotificationForRolesAsync(string[] roles, string title, string message, string type, int? alertId = null, int? reportId = null);
        Task<bool> MarkAsReadAsync(int notificationId, int userId);
        Task<int> GetUnreadCountAsync(int userId);
        Task<List<Notification>> GetRecentAsync(int userId, int count = 10);

        Task<List<Notification>> GetAllAsync(int userId);
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

        public async Task CreateNotificationAsync(int userId, string title, string message, string type, int? alertId = null, int? reportId = null)
        {
            var notification = new Notification
            {
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                RelatedAlertId = alertId,
                RelatedReportId = reportId,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
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

            await _hubContext.Clients.All.SendAsync("NotificationCountUpdated", userId);
        }

        public async Task CreateNotificationForRoleAsync(string role, string title, string message, string type, int? alertId = null, int? reportId = null)
        {
            await CreateNotificationForRolesAsync(new[] { role }, title, message, type, alertId, reportId);
        }

        public async Task CreateNotificationForRolesAsync(string[] roles, string title, string message, string type, int? alertId = null, int? reportId = null)
        {
            var users = new List<ApplicationUser>();
            foreach (var role in roles)
            {
                var roleUsers = await _userManager.GetUsersInRoleAsync(role);
                users.AddRange(roleUsers);
            }

            var uniqueUsers = users.Distinct().ToList();

            foreach (var user in uniqueUsers)
            {
                await CreateNotificationAsync(user.Id, title, message, type, alertId, reportId);
            }
        }

        public async Task<bool> MarkAsReadAsync(int notificationId, int userId)
        {
            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.UserId == userId);

            if (notification == null)
                return false;

            notification.IsRead = true;
            await _context.SaveChangesAsync();

            await _hubContext.Clients.User(userId.ToString()).SendAsync("NotificationRead", notificationId);
            await _hubContext.Clients.All.SendAsync("NotificationCountUpdated", userId);

            return true;
        }

        public async Task<int> GetUnreadCountAsync(int userId)
        {
            return await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .CountAsync();
        }

        public async Task<List<Notification>> GetRecentAsync(int userId, int count = 10)
        {
            return await _context.Notifications
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport)
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<Notification>> GetAllAsync(int userId)
        {
            return await _context.Notifications
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport)
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();
        }
    }
}
