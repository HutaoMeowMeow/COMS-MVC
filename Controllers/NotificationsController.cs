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
    }
}
