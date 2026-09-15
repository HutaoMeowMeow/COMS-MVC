using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class AlertsController : Controller
    {
        private readonly IAlertService _alertService;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public AlertsController(IAlertService alertService,
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager)
        {
            _alertService = alertService;
            _context = context;
            _userManager = userManager;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? status = null, string? severity = null)
        {
            var query = _context.ObstructionAlerts
                .Include(a => a.Canal)
                .Include(a => a.AssignedToUser)
                .Include(a => a.SensorReading)
                .AsQueryable();

            if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                if (!string.IsNullOrEmpty(user?.Barangay))
                {
                    query = query.Where(a => a.Canal.Barangay == user.Barangay);
                }
            }

            if (User.IsInRole("Maintenance"))
            {
                var userId = int.Parse(_userManager.GetUserId(User)!);
                query = query.Where(a => a.AssignedToUserId == userId || a.Status == "Active");
            }

            if (User.IsInRole("Resident"))
            {
                query = query.Where(a => a.Status == "Active" || a.Status == "Acknowledged");
            }

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(a => a.Status == status);
            }

            if (!string.IsNullOrEmpty(severity))
            {
                query = query.Where(a => a.Severity == severity);
            }

            var alerts = await query.OrderByDescending(a => a.DetectedAt).ToListAsync();
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentSeverity = severity;
            return View(alerts);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var alert = await _alertService.GetByIdAsync(id);
            if (alert == null)
            {
                return NotFound();
            }

            if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                if (alert.Canal?.Barangay != user?.Barangay)
                {
                    return Forbid();
                }
            }

            await PopulateAssignedToDropDownAsync(alert.AssignedToUserId);
            return View(alert);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Acknowledge(int id)
        {
            var result = await _alertService.AcknowledgeAsync(id, 0);
            if (!result)
            {
                TempData["ErrorMessage"] = "Unable to acknowledge this alert.";
            }
            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,Maintenance")]
        public async Task<IActionResult> Assign(int id, int assigneeId)
        {
            var alert = await _context.ObstructionAlerts.FindAsync(id);
            if (alert == null)
            {
                return NotFound();
            }

            alert.AssignedToUserId = assigneeId;
            alert.Status = "In Progress";
            await _context.SaveChangesAsync();

            var assignee = await _userManager.FindByIdAsync(assigneeId.ToString());
            var assigneeName = assignee?.UserName ?? "the selected user";
            TempData["SuccessMessage"] = $"Alert assigned to {assigneeName}.";

            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? notes)
        {
            var userId = int.Parse(_userManager.GetUserId(User)!);
            var result = await _alertService.UpdateStatusAsync(id, status, userId, notes);

            if (result)
            {
                TempData["SuccessMessage"] = $"Alert status updated to '{status}'.";
            }
            else
            {
                TempData["ErrorMessage"] = "Unable to update alert status.";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AssignUser(int id)
        {
            var alert = await _context.ObstructionAlerts
                .Include(a => a.Canal)
                .FirstOrDefaultAsync(a => a.ObstructionAlertId == id);

            if (alert == null)
            {
                return NotFound();
            }

            var maintenanceUsers = await _userManager.GetUsersInRoleAsync("Maintenance");
            var lguUsers = await _userManager.GetUsersInRoleAsync("LGU");
            var barangayUsers = await _userManager.GetUsersInRoleAsync("Barangay");

            var assignees = maintenanceUsers.Concat(lguUsers).Concat(barangayUsers).ToList();
            ViewBag.Assignees = new SelectList(assignees, "Id", "FullName", alert.AssignedToUserId);

            return View(alert);
        }

        private async Task PopulateAssignedToDropDownAsync(int? selectedId)
        {
            var maintenanceUsers = await _userManager.GetUsersInRoleAsync("Maintenance");
            var assignees = maintenanceUsers.Select(u => new { u.Id, u.FullName }).ToList();
            ViewBag.Assignees = new SelectList(assignees, "Id", "FullName", selectedId);
        }
    }
}
