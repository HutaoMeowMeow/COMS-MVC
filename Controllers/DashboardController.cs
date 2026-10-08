using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notificationService;

        public DashboardController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notificationService)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
        }

        public async Task<IActionResult> Index()
        {
            if (User.IsInRole("Admin"))
                return RedirectToAction(nameof(Admin));
            if (User.IsInRole("LGU"))
                return RedirectToAction(nameof(Lgu));
            if (User.IsInRole("Barangay"))
                return RedirectToAction(nameof(Barangay));
            if (User.IsInRole("Maintenance"))
                return RedirectToAction(nameof(Maintenance));
            if (User.IsInRole("CustomerService"))
                return RedirectToAction("Dashboard", "CustomerService");
            if (User.IsInRole("Resident"))
                return RedirectToAction(nameof(Resident));

            return RedirectToAction("Login", "Account");
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Admin()
        {
            var model = await BuildDashboardViewModelAsync();
            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = "LGU")]
        public async Task<IActionResult> Lgu()
        {
            var model = await BuildDashboardViewModelAsync();
            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = "Barangay")]
        public async Task<IActionResult> Barangay()
        {
            var model = await BuildDashboardViewModelAsync();
            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = "Maintenance")]
        public async Task<IActionResult> Maintenance()
        {
            var model = await BuildDashboardViewModelAsync();
            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Resident()
        {
            var model = await BuildDashboardViewModelAsync();
            return View(model);
        }

        private async Task<DashboardViewModel> BuildDashboardViewModelAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            int? currentUserId = int.TryParse(_userManager.GetUserId(User), out var parsedUserId)
                ? parsedUserId
                : null;
            var isResident = User.IsInRole("Resident");

            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .Include(c => c.Alerts.Where(a => a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress"))
                .ToListAsync();

            var totalCanals = canals.Count;
            var totalSensors = await _context.Sensors.CountAsync();
            var onlineSensors = await _context.Sensors.CountAsync(s => s.Status == "Online");
            var activeAlerts = await _context.ObstructionAlerts
                .CountAsync(a => a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress");
            var pendingReports = await _context.CommunityReports
                .CountAsync(r => r.Status == "Pending" || r.Status == "Under Review");

            // User-scoped counts. Always computed from the server-side identity,
            // never from client input.
            var myReportsCount = currentUserId.HasValue
                ? await _context.CommunityReports.CountAsync(r => r.UserId == currentUserId.Value)
                : 0;
            var myPendingReportsCount = currentUserId.HasValue
                ? await _context.CommunityReports.CountAsync(r => r.UserId == currentUserId.Value
                    && (r.Status == "Pending" || r.Status == "Under Review"))
                : 0;
            var registeredUsers = await _context.Users.CountAsync();
            var highRiskCanals = await _context.FloodRiskAssessments
                .CountAsync(a => (a.RiskLevel == "High" || a.RiskLevel == "Critical") && a.ValidUntil > DateTime.UtcNow);

            var recentAlerts = await _context.ObstructionAlerts
                .Include(a => a.Canal)
                .OrderByDescending(a => a.DetectedAt)
                .Take(8)
                .ToListAsync();

            var recentReports = await _context.CommunityReports
                .Include(r => r.Canal)
                .Include(r => r.User)
                .OrderByDescending(r => r.CreatedAt)
                .Take(8)
                .ToListAsync();

            // Resident isolation: a Resident must only see their own reports and
            // their own pending count. Staff roles (Admin/LGU/Barangay/Maintenance)
            // intentionally keep the global view so they can triage all reports.
            if (isResident && currentUserId.HasValue)
            {
                pendingReports = myPendingReportsCount;
                recentReports = await _context.CommunityReports
                    .Include(r => r.Canal)
                    .Include(r => r.User)
                    .Where(r => r.UserId == currentUserId.Value)
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(8)
                    .ToListAsync();
            }

            var riskAssessments = new List<FloodRiskAssessment>();
            foreach (var canal in canals)
            {
                var latest = await _context.FloodRiskAssessments
                    .Where(a => a.CanalId == canal.CanalId)
                    .OrderByDescending(a => a.AssessmentDate)
                    .FirstOrDefaultAsync();
                if (latest != null)
                {
                    riskAssessments.Add(latest);
                }
            }

            var latestReadings = new List<SensorReading>();
            foreach (var canal in canals)
            {
                var reading = await _context.SensorReadings
                    .Include(sr => sr.Sensor)
                    .Where(sr => sr.CanalId == canal.CanalId)
                    .OrderByDescending(sr => sr.RecordedAt)
                    .FirstOrDefaultAsync();
                if (reading != null)
                {
                    latestReadings.Add(reading);
                }
            }

            var announcements = await _context.Announcements
                .Include(a => a.PostedBy)
                .OrderByDescending(a => a.CreatedAt)
                .Take(5)
                .ToListAsync();

            var unreadCount = user != null ? await _notificationService.GetUnreadCountAsync(user.Id) : 0;

            var userRoles = await _userManager.GetRolesAsync(user);

            return new DashboardViewModel
            {
                TotalCanals = totalCanals,
                TotalSensors = totalSensors,
                ActiveAlerts = activeAlerts,
                PendingReports = pendingReports,
                MyReportsCount = myReportsCount,
                MyPendingReportsCount = myPendingReportsCount,
                HighRiskCanals = highRiskCanals,
                RegisteredUsers = registeredUsers,
                OnlineSensors = onlineSensors,
                OfflineSensors = totalSensors - onlineSensors,
                RecentAlerts = recentAlerts,
                RecentReports = recentReports,
                RecentRiskAssessments = riskAssessments,
                CanalStatusOverview = canals,
                SensorStatus = await _context.Sensors.Include(s => s.Canal).ToListAsync(),
                LatestReadings = latestReadings,
                Announcements = announcements,
                CurrentRole = GetUserRole(),
                UserName = user?.FullName ?? User.Identity?.Name ?? "User",
                UserBarangay = user?.Barangay ?? string.Empty,
                UnreadNotifications = unreadCount
            };
        }

        private string GetUserRole()
        {
            if (User.IsInRole("Admin")) return "Admin";
            if (User.IsInRole("LGU")) return "LGU";
            if (User.IsInRole("Barangay")) return "Barangay";
            if (User.IsInRole("Maintenance")) return "Maintenance";
            if (User.IsInRole("Resident")) return "Resident";
            return "User";
        }

        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

        [HttpGet]
        public IActionResult HttpError(int? statusCode = null)
        {
            ViewData["StatusCode"] = statusCode ?? 404;
            return View();
        }

        [HttpGet]
        public IActionResult Error()
        {
            return View();
        }
    }
}
