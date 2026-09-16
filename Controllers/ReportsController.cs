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
                if (int.TryParse(_userManager.GetUserId(User), out var userId))
                {
                    query = query.Where(r => r.UserId == userId);
                }
                else
                {
                    return Challenge();
                }
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

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return Challenge();
            }

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
                if (!int.TryParse(_userManager.GetUserId(User), out var userId))
                {
                    return Challenge();
                }
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
                .Select(c => new { c.CanalId, c.CanalName, c.Barangay, c.City, c.Latitude, c.Longitude })
                .ToListAsync();

            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName", canalId);
            ViewBag.ReportTypes = new SelectList(ReportTypes);
            ViewBag.CanalCoords = canals.ToDictionary(c => c.CanalId, c => new { lat = c.Latitude, lng = c.Longitude });

            var model = new CommunityReportFormViewModel
            {
                CanalId = canalId ?? 0,
                ReportType = "Other",
                CreatedAt = DateTime.UtcNow,
                Latitude = 10.3157,
                Longitude = 123.8854
            };

            if (canalId.HasValue)
            {
                var canal = await _context.Canals.FindAsync(canalId.Value);
                if (canal != null)
                {
                    model.Title = $"Issue at {canal.CanalName}";
                    model.Location = canal.Location;
                    if (canal.Latitude >= 9.30 && canal.Latitude <= 11.45
                        && canal.Longitude >= 123.20 && canal.Longitude <= 124.60)
                    {
                        model.Latitude = canal.Latitude;
                        model.Longitude = canal.Longitude;
                    }
                }
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Create(CommunityReportFormViewModel model)
        {
            model.Title = (model.Title ?? string.Empty).Trim();
            model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
            model.Location = string.IsNullOrWhiteSpace(model.Location) ? null : model.Location.Trim();
            model.ReportType = string.IsNullOrWhiteSpace(model.ReportType) ? "Other" : model.ReportType.Trim();

            if (!ReportTypes.Contains(model.ReportType))
            {
                ModelState.AddModelError(nameof(model.ReportType), "Please select a valid report type.");
            }

            if (model.Latitude == null || model.Longitude == null)
            {
                ModelState.AddModelError(nameof(model.Latitude), "Please pin the exact location on the Cebu map.");
            }
            else if (model.Latitude < 9.30 || model.Latitude > 11.45
                || model.Longitude < 123.20 || model.Longitude > 124.60)
            {
                ModelState.AddModelError(nameof(model.Latitude), "Pinned location must be inside Cebu.");
            }

            if (model.Photo != null && model.Photo.Length > 5 * 1024 * 1024)
            {
                ModelState.AddModelError(nameof(model.Photo), "Photo size must be less than 5MB.");
            }
            else if (model.Photo != null && model.Photo.Length > 0)
            {
                var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
                if (!allowedTypes.Contains(model.Photo.ContentType))
                {
                    ModelState.AddModelError(nameof(model.Photo), "Only JPG, PNG, and GIF images are allowed.");
                }
            }

            var canals = await _context.Canals
                .Where(c => c.Status != "Inactive")
                .Select(c => new { c.CanalId, c.CanalName, c.Barangay, c.City, c.Latitude, c.Longitude })
                .ToListAsync();
            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName", model.CanalId);
            ViewBag.ReportTypes = new SelectList(ReportTypes, model.ReportType);
            ViewBag.CanalCoords = canals.ToDictionary(c => c.CanalId, c => new { lat = c.Latitude, lng = c.Longitude });

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return Challenge();
            }

            var canal = await _context.Canals.FindAsync(model.CanalId);
            if (canal == null || canal.Status == "Inactive")
            {
                ModelState.AddModelError(nameof(model.CanalId), "Selected canal is invalid.");
                return View(model);
            }

            string? photoPath = null;
            if (model.Photo != null && model.Photo.Length > 0)
            {
                if (string.IsNullOrEmpty(_env.WebRootPath))
                {
                    ModelState.AddModelError(nameof(model.Photo), "File uploads are not configured on this server.");
                    return View(model);
                }
                try
                {
                    var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "reports");
                    Directory.CreateDirectory(uploadsFolder);

                    var ext = Path.GetExtension(model.Photo.FileName).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".gif")
                    {
                        ModelState.AddModelError(nameof(model.Photo), "Only JPG, PNG, and GIF images are allowed.");
                        return View(model);
                    }
                    var uniqueName = $"report_{Guid.NewGuid():N}{ext}";
                    var filePath = Path.Combine(uploadsFolder, uniqueName);
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await model.Photo.CopyToAsync(stream);
                    }
                    photoPath = $"/uploads/reports/{uniqueName}";
                }
                catch (Exception)
                {
                    ModelState.AddModelError(nameof(model.Photo), "Could not save the photo. Please try again.");
                    return View(model);
                }
            }

            try
            {
                var report = new CommunityReport
                {
                    UserId = userId,
                    CanalId = model.CanalId,
                    ReportType = model.ReportType,
                    Title = model.Title,
                    Description = model.Description ?? string.Empty,
                    PhotoPath = photoPath,
                    Location = model.Location,
                    Latitude = model.Latitude,
                    Longitude = model.Longitude,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.CommunityReports.Add(report);
                await _context.SaveChangesAsync();

                try
                {
                    await _notificationService.CreateNotificationForRolesAsync(
                        new[] { "Admin", "LGU", "Barangay" },
                        "New Community Report",
                        $"A new report '{model.Title}' has been submitted for review.",
                        "Report",
                        null,
                        report.CommunityReportId);
                }
                catch
                {
                    // Notifications are best-effort; report is already saved.
                }

                TempData["SuccessMessage"] = "Your report has been submitted successfully. It will be reviewed by authorized personnel.";
                return RedirectToAction(nameof(MyReports));
            }
            catch (Exception)
            {
                if (!string.IsNullOrEmpty(photoPath) && !string.IsNullOrEmpty(_env.WebRootPath))
                {
                    try
                    {
                        var saved = Path.Combine(_env.WebRootPath, photoPath.TrimStart('/'));
                        if (System.IO.File.Exists(saved)) System.IO.File.Delete(saved);
                    }
                    catch { }
                }
                ModelState.AddModelError(string.Empty, "Could not submit your report. Please check your input and try again.");
                return View(model);
            }
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

            if (string.IsNullOrWhiteSpace(status) || !ReportStatuses.Contains(status))
            {
                TempData["ErrorMessage"] = "Invalid status.";
                return RedirectToAction(nameof(Details), new { id });
            }
            if (notes != null && notes.Length > 2000)
            {
                TempData["ErrorMessage"] = "Notes cannot exceed 2000 characters.";
                return RedirectToAction(nameof(Details), new { id });
            }

            try
            {
                report.Status = status;
                report.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                try
                {
                    await _notificationService.CreateNotificationAsync(
                        report.UserId,
                        "Report Status Updated",
                        $"Your report '{report.Title}' has been updated to: {status}.",
                        "Report",
                        null,
                        report.CommunityReportId);
                }
                catch
                {
                    // Best-effort notification.
                }

                TempData["SuccessMessage"] = "Report status updated successfully.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not update the report status. Please try again.";
            }
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

            try
            {
                if (!string.IsNullOrEmpty(report.PhotoPath) && !string.IsNullOrEmpty(_env.WebRootPath))
                {
                    try
                    {
                        var filePath = Path.Combine(_env.WebRootPath, report.PhotoPath.TrimStart('/'));
                        if (System.IO.File.Exists(filePath))
                        {
                            System.IO.File.Delete(filePath);
                        }
                    }
                    catch
                    {
                        // File cleanup is best-effort.
                    }
                }

                _context.CommunityReports.Remove(report);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "Report deleted successfully.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not delete the report. Please try again.";
            }
            return RedirectToAction(nameof(Index));
        }
    }

    public class CommunityReportFormViewModel
    {
        [Range(1, int.MaxValue, ErrorMessage = "Please select a canal")]
        public int CanalId { get; set; }

        [Required(ErrorMessage = "Report type is required")]
        [StringLength(50, ErrorMessage = "Report type cannot exceed 50 characters")]
        public string ReportType { get; set; } = "Other";

        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
        public string? Description { get; set; }

        public IFormFile? Photo { get; set; }

        [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
        public string? Location { get; set; }

        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
        public double? Latitude { get; set; }

        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
        public double? Longitude { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
