using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notificationService;
        private readonly IWebHostEnvironment _env;

        private static readonly List<string> ReportTypes = new()
        {
            "Garbage / Debris",
            "Blocked Canal",
            "Flooding",
            "Damaged Canal",
            "Unusual Water Level",
            "Other"
        };

        private static readonly List<string> ReportStatuses = new()
        {
            "Pending",
            "Under Review",
            "Verified",
            "Assigned",
            "In Progress",
            "Resolved",
            "Rejected"
        };

        public ReportsController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notificationService,
            IWebHostEnvironment env)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var query = _context.CommunityReports
                .Include(r => r.Canal)
                .Include(r => r.User)
                .AsQueryable();

            if (User.IsInRole("Resident"))
            {
                var userId = int.Parse(_userManager.GetUserId(User)!);
                query = query.Where(r => r.UserId == userId);
            }
            else if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                if (!string.IsNullOrEmpty(user?.Barangay))
                {
                    query = query.Where(r => r.Canal.Barangay == user.Barangay);
                }
            }

            ViewBag.ReportTypes = new SelectList(ReportTypes);
            ViewBag.ReportStatuses = new SelectList(ReportStatuses);

            var reports = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
            return View(reports);
        }

        [HttpGet]
        public async Task<IActionResult> MyReports()
        {
            if (!User.IsInRole("Resident") && !User.IsInRole("Maintenance"))
            {
                return Forbid();
            }

            var userId = int.Parse(_userManager.GetUserId(User)!);

            var reports = await _context.CommunityReports
                .Include(r => r.Canal)
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return View(reports);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var report = await _context.CommunityReports
                .Include(r => r.Canal)
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.CommunityReportId == id);

            if (report == null)
            {
                return NotFound();
            }

            if (User.IsInRole("Resident"))
            {
                var userId = int.Parse(_userManager.GetUserId(User)!);
                if (report.UserId != userId)
                {
                    return Forbid();
                }
            }

            if (User.IsInRole("Barangay") && report.Canal != null)
            {
                var user = await _userManager.GetUserAsync(User);
                if (report.Canal.Barangay != user?.Barangay)
                {
                    return Forbid();
                }
            }

            return View(report);
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Create(int? canalId = null)
        {
            var canals = await _context.Canals
                .Where(c => c.Status != "Inactive")
                .Select(c => new { c.CanalId, c.CanalName, c.Barangay, c.City })
                .ToListAsync();

            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName", canalId);
            ViewBag.ReportTypes = new SelectList(ReportTypes);

            var model = new CommunityReportFormViewModel
            {
                CanalId = canalId ?? 0,
                ReportType = "Other",
                CreatedAt = DateTime.UtcNow
            };

            if (canalId.HasValue)
            {
                var canal = await _context.Canals.FindAsync(canalId.Value);
                if (canal != null)
                {
                    model.Title = $"Issue at {canal.CanalName}";
                    model.Location = canal.Location;
                }
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Create(CommunityReportFormViewModel model)
        {
            if (model.CanalId <= 0)
            {
                ModelState.AddModelError("CanalId", "Please select a canal.");
            }

            var canals = await _context.Canals
                .Where(c => c.Status != "Inactive")
                .Select(c => new { c.CanalId, c.CanalName, c.Barangay, c.City })
                .ToListAsync();
            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName", model.CanalId);
            ViewBag.ReportTypes = new SelectList(ReportTypes, model.ReportType);

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var userId = int.Parse(_userManager.GetUserId(User)!);

            string? photoPath = null;
            if (model.Photo != null && model.Photo.Length > 0)
            {
                if (model.Photo.Length > 5 * 1024 * 1024)
                {
                    ModelState.AddModelError("Photo", "Photo size must be less than 5MB.");
                    return View(model);
                }

                var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
                if (!allowedTypes.Contains(model.Photo.ContentType))
                {
                    ModelState.AddModelError("Photo", "Only JPG, PNG, and GIF images are allowed.");
                    return View(model);
                }

                var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "reports");
                Directory.CreateDirectory(uploadsFolder);

                var uniqueName = $"report_{Guid.NewGuid():N}_{Path.GetFileName(model.Photo.FileName)}";
                var filePath = Path.Combine(uploadsFolder, uniqueName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await model.Photo.CopyToAsync(stream);
                }
                photoPath = $"/uploads/reports/{uniqueName}";
            }

            var canal = await _context.Canals.FindAsync(model.CanalId);
            if (canal == null)
            {
                ModelState.AddModelError("CanalId", "Selected canal is invalid.");
                return View(model);
            }

            var report = new CommunityReport
            {
                UserId = userId,
                CanalId = model.CanalId,
                ReportType = model.ReportType,
                Title = model.Title,
                Description = model.Description,
                PhotoPath = photoPath,
                Location = model.Location,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.CommunityReports.Add(report);
            await _context.SaveChangesAsync();

            await _notificationService.CreateNotificationForRolesAsync(
                new[] { "Admin", "LGU", "Barangay" },
                "New Community Report",
                $"Resident {model.Title} has been submitted for review.",
                "Report",
                null,
                report.CommunityReportId);

            TempData["SuccessMessage"] = "Your report has been submitted successfully. It will be reviewed by authorized personnel.";
            return RedirectToAction(nameof(MyReports));
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> StatusUpdate(int id)
        {
            var report = await _context.CommunityReports
                .Include(r => r.Canal)
                .FirstOrDefaultAsync(r => r.CommunityReportId == id);

            if (report == null)
            {
                return NotFound();
            }

            if (User.IsInRole("Barangay") && report.Canal != null)
            {
                var user = await _userManager.GetUserAsync(User);
                if (report.Canal.Barangay != user?.Barangay)
                {
                    return Forbid();
                }
            }

            var maintenanceUsers = await _userManager.GetUsersInRoleAsync("Maintenance");
            ViewBag.Assignees = new SelectList(maintenanceUsers, "Id", "FullName", report.Status == "Assigned" ? maintenanceUsers.FirstOrDefault()?.Id : null);
            ViewBag.ReportStatuses = new SelectList(ReportStatuses, report.Status);

            return View(report);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay")]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? notes, int? assigneeId)
        {
            var report = await _context.CommunityReports
                .Include(r => r.Canal)
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.CommunityReportId == id);

            if (report == null)
            {
                return NotFound();
            }

            if (User.IsInRole("Barangay") && report.Canal != null)
            {
                var user = await _userManager.GetUserAsync(User);
                if (report.Canal.Barangay != user?.Barangay)
                {
                    return Forbid();
                }
            }

            if (!ReportStatuses.Contains(status))
            {
                TempData["ErrorMessage"] = "Invalid status.";
                return RedirectToAction(nameof(Details), new { id });
            }

            report.Status = status;
            report.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _notificationService.CreateNotificationAsync(
                report.UserId,
                "Report Status Updated",
                $"Your report '{report.Title}' has been updated to: {status}.",
                "Report",
                null,
                report.CommunityReportId);

            TempData["SuccessMessage"] = "Report status updated successfully.";
            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var report = await _context.CommunityReports
                .Include(r => r.Canal)
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.CommunityReportId == id);

            if (report == null)
            {
                return NotFound();
            }

            return View(report);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id, IFormCollection collection)
        {
            var report = await _context.CommunityReports.FindAsync(id);
            if (report == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrEmpty(report.PhotoPath))
            {
                var filePath = Path.Combine(_env.WebRootPath, report.PhotoPath.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }

            _context.CommunityReports.Remove(report);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = "Report deleted successfully.";
            return RedirectToAction(nameof(Index));
        }
    }

    public class CommunityReportFormViewModel
    {
        public int CanalId { get; set; }

        public string ReportType { get; set; } = "Other";

        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        public string Title { get; set; }

        [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
        public string? Description { get; set; }

        public IFormFile? Photo { get; set; }

        public string? Location { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
