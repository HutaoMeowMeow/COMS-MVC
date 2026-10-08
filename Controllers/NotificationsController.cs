using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class NotificationsController : Controller
    {
        private readonly INotificationService _notificationService;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public NotificationsController(INotificationService notificationService,
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager)
        {
            _notificationService = notificationService;
            _context = context;
            _userManager = userManager;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return RedirectToAction("Login", "Account");
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return RedirectToAction("Login", "Account");
            }
            var notifications = await _notificationService.GetAllAsync(userId);
            return View(notifications);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> All()
        {
            var notifications = await _context.Notifications
                .Include(n => n.User)
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport)
                .OrderByDescending(n => n.CreatedAt)
                .Take(50)
                .ToListAsync();

            return View(notifications);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            if (id <= 0)
            {
                TempData["ErrorMessage"] = "Invalid notification.";
                return RedirectToAction(nameof(Index));
            }
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return RedirectToAction("Login", "Account");
            }
            var ok = await _notificationService.MarkAsReadAsync(id, userId);
            if (!ok)
            {
                TempData["ErrorMessage"] = "Notification not found.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return RedirectToAction("Login", "Account");
            }
            try
            {
                var notifications = await _context.Notifications
                    .Where(n => n.UserId == userId && !n.IsRead)
                    .ToListAsync();

                foreach (var notification in notifications)
                {
                    notification.IsRead = true;
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not update notifications. Please try again.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Json(new { count = 0 });
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return Json(new { count = 0 });
            }
            var count = await _notificationService.GetUnreadCountAsync(userId);
            return Json(new { count });
        }

        /// <summary>Scoped preview for the header bell dropdown (own notifications only).</summary>
        [HttpGet]
        public async Task<IActionResult> Recent()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Json(new { items = new object[0], unread = 0 });
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return Json(new { items = new object[0], unread = 0 });
            }

            var recent = await _notificationService.GetRecentAsync(userId, 6);
            var unread = await _notificationService.GetUnreadCountAsync(userId);
            return Json(new
            {
                unread,
                items = recent.Select(n => new
                {
                    id = n.NotificationId,
                    title = n.Title,
                    message = n.Message.Length > 90 ? n.Message.Substring(0, 90) + "…" : n.Message,
                    type = n.Type,
                    createdAt = n.CreatedAt.ToString("MMM dd, HH:mm"),
                    isRead = n.IsRead
                })
            });
        }

        // ================= Admin: user-specific notification =================

        private static readonly List<string> AllowedSendTypes = new()
        {
            "General", "Report Update", "Customer Service", "Account", "System", "Important"
        };

        /// <summary>Admin compose form. Recipient comes from the route and is
        /// re-validated server-side; never trusted from client input.</summary>
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Send(int id)
        {
            var recipient = await _userManager.FindByIdAsync(id.ToString());
            if (recipient == null)
            {
                return NotFound();
            }

            var roles = await _userManager.GetRolesAsync(recipient);
            ViewBag.SendTypes = new SelectList(AllowedSendTypes);
            return View(new SendNotificationViewModel
            {
                RecipientId = recipient.Id,
                RecipientUserName = recipient.UserName ?? string.Empty,
                RecipientFullName = recipient.FullName ?? string.Empty,
                RecipientEmail = recipient.Email ?? string.Empty,
                RecipientRoles = roles.ToList()
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Send(int id, SendNotificationViewModel model)
        {
            // Recipient identity is established from the route id via a
            // server-side lookup. Any posted recipient value is ignored.
            var recipient = await _userManager.FindByIdAsync(id.ToString());
            if (recipient == null)
            {
                return NotFound();
            }

            model.Title = (model.Title ?? string.Empty).Trim();
            model.Message = (model.Message ?? string.Empty).Trim();
            model.NotificationType = (model.NotificationType ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(model.Title) || model.Title.Length > 150)
            {
                ModelState.AddModelError(nameof(model.Title), "Title is required (max 150 characters).");
            }
            if (string.IsNullOrWhiteSpace(model.Message) || model.Message.Length > 2000)
            {
                ModelState.AddModelError(nameof(model.Message), "Message is required (max 2000 characters).");
            }
            if (!AllowedSendTypes.Contains(model.NotificationType))
            {
                ModelState.AddModelError(nameof(model.NotificationType), "Please select a valid type.");
            }

            var roles = await _userManager.GetRolesAsync(recipient);
            model.RecipientId = recipient.Id;
            model.RecipientUserName = recipient.UserName ?? string.Empty;
            model.RecipientFullName = recipient.FullName ?? string.Empty;
            model.RecipientEmail = recipient.Email ?? string.Empty;
            model.RecipientRoles = roles.ToList();
            ViewBag.SendTypes = new SelectList(AllowedSendTypes, model.NotificationType);

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                // Persists with UserId = recipient + pushes the real-time
                // SignalR toast/badge to that user only. Best-effort delivery.
                await _notificationService.CreateNotificationAsync(
                    recipient.Id, model.Title, model.Message, model.NotificationType);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not send the notification. Please try again.";
                return View(model);
            }

            TempData["SuccessMessage"] = $"Notification sent to {recipient.UserName}.";
            return RedirectToAction(nameof(All));
        }
    }

    public class SendNotificationViewModel
    {
        // Display only. The POST route id + server lookup decide the recipient.
        public int RecipientId { get; set; }

        public string RecipientUserName { get; set; } = string.Empty;

        public string RecipientFullName { get; set; } = string.Empty;

        public string RecipientEmail { get; set; } = string.Empty;

        public List<string> RecipientRoles { get; set; } = new();

        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "Message is required")]
        [StringLength(2000, ErrorMessage = "Message cannot exceed 2000 characters")]
        public string? Message { get; set; }

        [Required(ErrorMessage = "Type is required")]
        public string NotificationType { get; set; } = "General";
    }
}
