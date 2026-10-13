using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class AlertsController : Controller
    {
        private readonly IAlertService _alertService;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILookupService _lookups;

        public AlertsController(IAlertService alertService,
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            ILookupService lookups)
        {
            _alertService = alertService;
            _context = context;
            _userManager = userManager;
            _lookups = lookups;
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
                if (int.TryParse(_userManager.GetUserId(User), out var userId))
                {
                    query = query.Where(a => a.AssignedToUserId == userId || a.Status == "Active");
                }
            }

            if (User.IsInRole("Resident"))
            {
                query = query.Where(a => a.Status == "Active" || a.Status == "Acknowledged");
            }

            if (!string.IsNullOrWhiteSpace(status)
                && !await _lookups.IsValidAsync(LookupService.Categories.AlertStatus, status))
            {
                status = null;
            }
            if (!string.IsNullOrWhiteSpace(severity)
                && !await _lookups.IsValidAsync(LookupService.Categories.AlertSeverity, severity))
            {
                severity = null;
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
            ViewBag.AlertStatuses = new SelectList(
                await _lookups.GetOptionsAsync(LookupService.Categories.AlertStatus), "Value", "DisplayName", status);
            ViewBag.AlertSeverities = new SelectList(
                await _lookups.GetOptionsAsync(LookupService.Categories.AlertSeverity), "Value", "DisplayName", severity);
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

            if (User.IsInRole("Resident")
                && alert.Status != "Active" && alert.Status != "Acknowledged")
            {
                return NotFound();
            }

            if (User.IsInRole("Maintenance"))
            {
                if (int.TryParse(_userManager.GetUserId(User), out var maintenanceId))
                {
                    if (alert.AssignedToUserId != maintenanceId && alert.Status != "Active")
                    {
                        return Forbid();
                    }
                }
            }

            await PopulateAssignedToDropDownAsync(alert.AssignedToUserId);
            ViewBag.AlertStatuses = new SelectList(
                await _lookups.GetOptionsAsync(LookupService.Categories.AlertStatus), "Value", "DisplayName", alert.Status);
            return View(alert);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Acknowledge(int id)
        {
            // Same barangay restriction as Details: a Barangay user may only
            // change alerts from their own barangay.
            if (User.IsInRole("Barangay"))
            {
                var scoped = await _alertService.GetByIdAsync(id);
                if (scoped == null)
                {
                    return NotFound();
                }
                var scopeUser = await _userManager.GetUserAsync(User);
                if (scoped.Canal?.Barangay != scopeUser?.Barangay)
                {
                    return Forbid();
                }
            }
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
            if (id <= 0 || assigneeId <= 0)
            {
                TempData["ErrorMessage"] = "Please select a valid user to assign.";
                return RedirectToAction(nameof(Details), new { id });
            }

            var alert = await _context.ObstructionAlerts.FindAsync(id);
            if (alert == null)
            {
                return NotFound();
            }

            var assignee = await _userManager.FindByIdAsync(assigneeId.ToString());
            if (assignee == null)
            {
                TempData["ErrorMessage"] = "Selected user does not exist.";
                return RedirectToAction(nameof(Details), new { id });
            }

            // Alerts may only be assigned to staff who can act on them —
            // never to Residents (or role-less accounts) via crafted posts.
            var assigneeRoles = await _userManager.GetRolesAsync(assignee);
            if (!assigneeRoles.Any(r =>
                r == "Admin" || r == "LGU" || r == "Barangay" || r == "Maintenance"))
            {
                TempData["ErrorMessage"] = "Alerts can only be assigned to staff (Admin, LGU, Barangay, or Maintenance).";
                return RedirectToAction(nameof(Details), new { id });
            }

            try
            {
                alert.AssignedToUserId = assigneeId;
                alert.Status = "In Progress";
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Alert assigned to {assignee.FullName ?? assignee.UserName}.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not assign the alert. Please try again.";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? notes)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(status)
                || !await _lookups.IsValidAsync(LookupService.Categories.AlertStatus, status))
            {
                TempData["ErrorMessage"] = "Invalid status selected.";
                return RedirectToAction(nameof(Details), new { id });
            }
            // Same barangay restriction as Details.
            if (User.IsInRole("Barangay"))
            {
                var scoped = await _alertService.GetByIdAsync(id);
                if (scoped == null)
                {
                    return NotFound();
                }
                var scopeUser = await _userManager.GetUserAsync(User);
                if (scoped.Canal?.Barangay != scopeUser?.Barangay)
                {
                    return Forbid();
                }
            }
            if (notes != null && notes.Length > 2000)
            {
                TempData["ErrorMessage"] = "Notes cannot exceed 2000 characters.";
                return RedirectToAction(nameof(Details), new { id });
            }
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                TempData["ErrorMessage"] = "Your session is invalid. Please sign in again.";
                return RedirectToAction(nameof(Details), new { id });
            }
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
