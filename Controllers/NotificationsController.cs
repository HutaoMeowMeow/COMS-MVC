using COMS_MVC.Helpers;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class NotificationsController : Controller
    {
        private readonly INotificationService _notificationService;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILookupService _lookups;

        public NotificationsController(INotificationService notificationService,
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            ILookupService lookups)
        {
            _notificationService = notificationService;
            _context = context;
            _userManager = userManager;
            _lookups = lookups;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int page = 1)
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return RedirectToAction("Login", "Account");
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return RedirectToAction("Login", "Account");
            }
            // Server-side pagination: only one page of rows is ever loaded.
            var pageModel = await _notificationService.GetPageAsync(userId, page, 12);
            ViewBag.UnreadCount = pageModel.UnreadCount;
            return View(pageModel);
        }

        /// <summary>
        /// Opens the record/module a notification points to. Ownership is
        /// enforced from the authenticated identity, the notification is
        /// marked read only after that check, and the destination is validated
        /// server-side so deleted or unauthorized records never produce a raw
        /// 404/500 from a notification click.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Open(int id)
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

            var notification = await _context.Notifications
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.NotificationId == id && n.UserId == userId);

            if (notification == null)
            {
                // Same response for "not found" and "someone else's row" so
                // another user's notification IDs are not disclosed.
                TempData["ErrorMessage"] = "Notification not found.";
                return RedirectToAction(nameof(Index));
            }

            // Ownership already verified by the predicate above.
            await _notificationService.MarkAsReadAsync(id, userId);

            var (entityType, entityId) = NotificationLinks.ResolveEntity(notification);
            var target = NotificationLinks.GetTarget(entityType, entityId, User, notification.Type);
            if (target == null)
            {
                // Informational notification: nothing to open.
                return RedirectToAction(nameof(Index));
            }

            var currentUser = await _userManager.GetUserAsync(User);
            var problem = await ValidateDestinationAsync(entityType, entityId, currentUser, userId);
            if (problem != null)
            {
                TempData["AlertMessage"] = problem;
                var fallback = NotificationLinks.Fallback(entityType, User);
                return RedirectToAction(fallback.Action, fallback.Controller);
            }

            var route = target.Id.HasValue ? new { id = target.Id.Value } : null;
            return RedirectToAction(target.Action, target.Controller, route);
        }

        /// <summary>
        /// Returns null when the linked record exists and the current user may
        /// open it; otherwise a user-facing explanation. Mirrors the
        /// authorization rules of the destination controllers.
        /// </summary>
        private async Task<string?> ValidateDestinationAsync(
            string? entityType, int? entityId, ApplicationUser? currentUser, int userId)
        {
            if (string.IsNullOrWhiteSpace(entityType) || !entityId.HasValue)
            {
                // Module-level destination; the module applies its own rules.
                return null;
            }

            switch (entityType)
            {
                case NotificationLinks.Report:
                {
                    var report = await _context.CommunityReports
                        .AsNoTracking()
                        .Where(r => r.CommunityReportId == entityId.Value)
                        .Select(r => new
                        {
                            r.UserId,
                            Barangay = r.Canal != null ? r.Canal.Barangay : null
                        })
                        .FirstOrDefaultAsync();

                    if (report == null)
                    {
                        return "The linked report is no longer available. Showing your reports instead.";
                    }
                    if (User.IsInRole("Resident") && report.UserId != userId)
                    {
                        return "You do not have access to that report. Showing your reports instead.";
                    }
                    if (User.IsInRole("Barangay") && report.Barangay != null
                        && !string.Equals(report.Barangay, currentUser?.Barangay, StringComparison.OrdinalIgnoreCase))
                    {
                        return "That report belongs to another barangay. Showing the reports you can access.";
                    }
                    return null;
                }

                case NotificationLinks.Alert:
                {
                    var alert = await _context.ObstructionAlerts
                        .AsNoTracking()
                        .Where(a => a.ObstructionAlertId == entityId.Value)
                        .Select(a => new
                        {
                            a.Status,
                            a.AssignedToUserId,
                            Barangay = a.Canal != null ? a.Canal.Barangay : null
                        })
                        .FirstOrDefaultAsync();

                    if (alert == null)
                    {
                        return "The linked alert is no longer available (alerts expire after the retention window). Showing active alerts instead.";
                    }
                    if (User.IsInRole("Resident")
                        && alert.Status != "Active" && alert.Status != "Acknowledged")
                    {
                        return "That alert is no longer active. Showing current alerts instead.";
                    }
                    if (User.IsInRole("Barangay") && alert.Barangay != null
                        && !string.Equals(alert.Barangay, currentUser?.Barangay, StringComparison.OrdinalIgnoreCase))
                    {
                        return "That alert belongs to another barangay. Showing the alerts you can access.";
                    }
                    if (User.IsInRole("Maintenance")
                        && !User.IsInRole("Admin") && !User.IsInRole("LGU")
                        && alert.AssignedToUserId != userId && alert.Status != "Active")
                    {
                        return "That alert is assigned to another maintenance user. Showing the alerts you can access.";
                    }
                    return null;
                }

                case NotificationLinks.SupportTicket:
                {
                    var ticket = await _context.SupportTickets
                        .AsNoTracking()
                        .Where(t => t.SupportTicketId == entityId.Value)
                        .Select(t => new { t.UserId })
                        .FirstOrDefaultAsync();

                    if (ticket == null)
                    {
                        return "The linked support ticket no longer exists. Opening the support inbox instead.";
                    }
                    if (User.IsInRole("Resident"))
                    {
                        return ticket.UserId == userId
                            ? null
                            : "You do not have access to that support conversation.";
                    }
                    if (!NotificationLinks.IsSupportStaff(User))
                    {
                        return "You do not have access to support conversations.";
                    }
                    return null;
                }

                case NotificationLinks.Announcement:
                {
                    var announcement = await _context.Announcements
                        .AsNoTracking()
                        .Where(a => a.AnnouncementId == entityId.Value)
                        .Select(a => new { a.TargetAudience })
                        .FirstOrDefaultAsync();

                    if (announcement == null)
                    {
                        return "The linked announcement is no longer available. Opening the announcement list instead.";
                    }
                    if (!User.IsInRole("Admin") && !User.IsInRole("LGU"))
                    {
                        if (currentUser == null)
                        {
                            return "You do not have access to that announcement.";
                        }
                        var roles = await _userManager.GetRolesAsync(currentUser);
                        var allowed = NotificationLinks.AllowedAnnouncementAudiences(roles);
                        if (!allowed.Contains(announcement.TargetAudience))
                        {
                            return "That announcement is not available to your role. Opening the announcements you can see.";
                        }
                    }
                    return null;
                }

                default:
                    // Unknown structured type: no record route was produced.
                    return null;
            }
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> All(int page = 1)
        {
            const int pageSize = 25;
            page = Math.Max(1, page);
            var query = _context.Notifications
                .AsNoTracking()
                .Include(n => n.User)
                .Include(n => n.RelatedAlert)
                .Include(n => n.RelatedReport);

            var total = await query.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            page = Math.Min(page, totalPages);

            var notifications = await query
                .OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Page = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalCount = total;
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
                var updated = await _notificationService.MarkAllAsReadAsync(userId);
                TempData["SuccessMessage"] = updated > 0
                    ? $"Marked {updated} notification{(updated == 1 ? "" : "s")} as read."
                    : "All notifications are already read.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not update notifications. Please try again.";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Deletes ALL of the authenticated user's notifications in one
        /// statement. Never accepts a user id from the request.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAll()
        {
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, message = "Please sign in again." });
                }
                return RedirectToAction("Login", "Account");
            }
            try
            {
                var deleted = await _notificationService.DeleteAllAsync(userId);
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = true, deleted, unreadCount = 0 });
                }
                TempData["SuccessMessage"] = deleted > 0
                    ? $"Deleted {deleted} notification{(deleted == 1 ? "" : "s")}."
                    : "There were no notifications to delete.";
            }
            catch (Exception)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, message = "Could not delete your notifications. Please try again." });
                }
                TempData["ErrorMessage"] = "Could not delete your notifications. Please try again.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";

            if (id <= 0)
            {
                if (isAjax) return Json(new { success = false, message = "Invalid notification." });
                TempData["ErrorMessage"] = "Invalid notification.";
                return RedirectToAction(nameof(Index));
            }
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                if (isAjax) return Json(new { success = false, message = "Please sign in again." });
                return RedirectToAction("Login", "Account");
            }

            try
            {
                var ok = await _notificationService.DeleteAsync(id, userId);
                if (!ok)
                {
                    if (isAjax) return Json(new { success = false, message = "Notification not found." });
                    TempData["ErrorMessage"] = "Notification not found.";
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception)
            {
                if (isAjax) return Json(new { success = false, message = "Could not delete the notification. Please try again." });
                TempData["ErrorMessage"] = "Could not delete the notification. Please try again.";
                return RedirectToAction(nameof(Index));
            }

            if (isAjax)
            {
                var unread = await _notificationService.GetUnreadCountAsync(userId);
                return Json(new { success = true, notificationId = id, unreadCount = unread });
            }

            TempData["SuccessMessage"] = "Notification deleted.";
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
                items = recent.Select(n =>
                {
                    var target = NotificationLinks.GetTarget(n, User);
                    // The preview must use the same resolver as the list so a
                    // click marks the row read and validates the destination.
                    var url = target == null
                        ? null
                        : Url.Action("Open", "Notifications", new { id = n.NotificationId });
                    return new
                    {
                        id = n.NotificationId,
                        title = n.Title,
                        message = n.Message.Length > 90 ? n.Message.Substring(0, 90) + "…" : n.Message,
                        type = n.Type,
                        createdAtUtc = COMS_MVC.Helpers.PhTime.AsUtc(n.CreatedAt).ToString("o"),
                        createdAtPh = COMS_MVC.Helpers.PhTime.ToPhString(n.CreatedAt),
                        relative = COMS_MVC.Helpers.PhTime.ToRelative(n.CreatedAt),
                        isRead = n.IsRead,
                        url
                    };
                })
            });
        }

        // ================= Admin: user-specific notification =================

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
            ViewBag.SendTypes = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.NotificationType), "Value", "DisplayName");
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
            if (!await _lookups.IsValidAsync(LookupService.Categories.NotificationType, model.NotificationType))
            {
                ModelState.AddModelError(nameof(model.NotificationType), "Please select a valid type.");
            }

            var roles = await _userManager.GetRolesAsync(recipient);
            model.RecipientId = recipient.Id;
            model.RecipientUserName = recipient.UserName ?? string.Empty;
            model.RecipientFullName = recipient.FullName ?? string.Empty;
            model.RecipientEmail = recipient.Email ?? string.Empty;
            model.RecipientRoles = roles.ToList();
            ViewBag.SendTypes = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.NotificationType), "Value", "DisplayName", model.NotificationType);

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
