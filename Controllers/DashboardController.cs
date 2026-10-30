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
        private readonly AlertRetentionOptions _alertRetention;

        public DashboardController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notificationService,
            Microsoft.Extensions.Options.IOptions<AlertRetentionOptions>? alertRetentionOptions = null)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
            _alertRetention = alertRetentionOptions?.Value ?? AlertRetentionOptions.Default;
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

            // Authenticated but no known role: AccessDenied, never Login
            // (Login redirects authenticated users back here — that loop
            // would bounce forever).
            return RedirectToAction(nameof(AccessDenied));
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

            // Single retention cutoff (UTC) shared with AlertRetentionService:
            // anything older than the window is expired everywhere — counts,
            // recent lists, and the per-canal alert collections.
            var alertCutoff = _alertRetention.CutoffUtc;

            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .Include(c => c.Alerts.Where(a => (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress")
                                                  && a.DetectedAt >= alertCutoff))
                .ToListAsync();

            var totalCanals = canals.Count;
            var totalSensors = await _context.Sensors.CountAsync();
            var onlineSensors = await _context.Sensors.CountAsync(s => s.Status == "Online");
            var activeAlerts = await _context.ObstructionAlerts
                .CountAsync(a => (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress")
                                && a.DetectedAt >= alertCutoff);
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

            // Newest alerts only, bounded and retention-filtered: expired
            // alerts never appear in the Recent Alerts tables on any dashboard.
            var recentAlerts = await _context.ObstructionAlerts
                .Include(a => a.Canal)
                .Where(a => a.DetectedAt >= alertCutoff)
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

            // Batched single-query lookups (was 2N+1 round-trips, one per canal).
            var canalIds = canals.Select(c => c.CanalId).ToList();
            var riskAssessments = (await _context.FloodRiskAssessments
                .Where(a => canalIds.Contains(a.CanalId))
                .GroupBy(a => a.CanalId)
                .Select(g => g.OrderByDescending(a => a.AssessmentDate).FirstOrDefault())
                .ToListAsync())
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();

            var latestReadings = (await _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Where(sr => canalIds.Contains(sr.CanalId))
                .GroupBy(sr => sr.CanalId)
                .Select(g => g.OrderByDescending(sr => sr.RecordedAt).FirstOrDefault())
                .ToListAsync())
                .Where(sr => sr != null)
                .Select(sr => sr!)
                .ToList();

            var announcementsQuery = _context.Announcements
                .Include(a => a.PostedBy)
                .AsQueryable();
            if (user != null && !User.IsInRole("Admin") && !User.IsInRole("LGU"))
            {
                var userRoles = await _userManager.GetRolesAsync(user);
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "All" };
                foreach (var r in userRoles)
                {
                    allowed.Add(r);
                    allowed.Add(r switch
                    {
                        "Barangay" => "LGU_Barangay",
                        "Maintenance" => "Maintenance_Resident",
                        "Resident" => "Maintenance_Resident",
                        _ => "All"
                    });
                }
                announcementsQuery = announcementsQuery.Where(a => allowed.Contains(a.TargetAudience));
            }
            var announcements = await announcementsQuery
                .OrderByDescending(a => a.CreatedAt)
                .Take(5)
                .ToListAsync();

            var unreadCount = user != null ? await _notificationService.GetUnreadCountAsync(user.Id) : 0;

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
