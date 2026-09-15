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
            if (!User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Login", "Account");
            }

            var userId = int.Parse(_userManager.GetUserId(User)!);
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
            var userId = int.Parse(_userManager.GetUserId(User)!);
            await _notificationService.MarkAsReadAsync(id, userId);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = int.Parse(_userManager.GetUserId(User)!);
            var notifications = await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ToListAsync();

            foreach (var notification in notifications)
            {
                notification.IsRead = true;
            }
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Json(new { count = 0 });
            }

            var userId = int.Parse(_userManager.GetUserId(User)!);
            var count = await _notificationService.GetUnreadCountAsync(userId);
            return Json(new { count });
        }
    }
}
