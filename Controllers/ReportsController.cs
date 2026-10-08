using Microsoft.AspNetCore.Mvc.Rendering;
using SixLabors.ImageSharp;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notificationService;
        private readonly IImageVerificationService _imageVerification;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportsController> _logger;
        private readonly IWebHostEnvironment _env;

        private static readonly List<string> ReportTypes = new()
        {
            "Garbage / Debris",
            "Blocked Canal",
            "Flooding",
            "Damaged Canal",
            "Unusual Water Level"
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
            IImageVerificationService imageVerification,
            IServiceScopeFactory scopeFactory,
            ILogger<ReportsController> logger,
            IWebHostEnvironment env)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
            _imageVerification = imageVerification;
            _scopeFactory = scopeFactory;
            _logger = logger;
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
                    query = query.Where(r => r.Canal != null && r.Canal.Barangay == user.Barangay);
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
                    // IDOR guard: same response as a missing report so Account A
                    // cannot confirm Account B's report ID exists, and no data leaks.
                    return NotFound();
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
            ViewBag.ReportTypes = new SelectList(ReportTypes);

            var model = new CommunityReportFormViewModel
            {
                CanalId = canalId,
                ReportType = "Garbage / Debris",
                CreatedAt = DateTime.UtcNow,
                Latitude = 10.3157,
                Longitude = 123.8854
            };

            // Optional deep-link: staff/canal pages may suggest a canal, but the
            // resident is never required to pick one. Location pin is the source
            // of truth for where the problem is.
            if (canalId.HasValue)
            {
                var canal = await _context.Canals.FindAsync(canalId.Value);
                if (canal != null && canal.Status != "Inactive")
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
                else
                {
                    model.CanalId = null;
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
            model.ReportType = (model.ReportType ?? string.Empty).Trim();

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

            ViewBag.ReportTypes = new SelectList(ReportTypes, model.ReportType);

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
            {
                return Challenge();
            }

            // Canal is optional: a resident reports a physical location, and
            // LGU/Admin identifies the canal afterwards. Only validate when one
            // was actually supplied (e.g. via a deep-link).
            if (model.CanalId.HasValue)
            {
                var canal = await _context.Canals.FindAsync(model.CanalId.Value);
                if (canal == null || canal.Status == "Inactive")
                {
                    ModelState.AddModelError(nameof(model.CanalId), "Selected canal is invalid.");
                    return View(model);
                }
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

                    // Decode the actual bytes: rejects executables/renamed files
                    // that pass extension + client MIME checks. Never trust the
                    // client-provided content type alone.
                    try
                    {
                        using var probe = Image.Load(filePath);
                        if (probe.Width < 16 || probe.Height < 16)
                        {
                            throw new InvalidDataException("Image dimensions too small.");
                        }
                    }
                    catch
                    {
                        try { System.IO.File.Delete(filePath); } catch { }
                        ModelState.AddModelError(nameof(model.Photo), "The uploaded file is not a valid image. Please upload a real JPG, PNG, or GIF photo.");
                        return View(model);
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
                    // Advisory only: analyzed in the background after save.
                    ImageVerificationStatus = photoPath != null && _imageVerification.IsEnabled ? "Pending" : null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.CommunityReports.Add(report);
                await _context.SaveChangesAsync();

                // Authenticity check runs after the report is safely stored, on a
                // background task with its own scope. Any failure only marks the
                // verification Failed — the report itself always succeeds.
                if (photoPath != null && _imageVerification.IsEnabled && !string.IsNullOrEmpty(_env.WebRootPath))
                {
                    var savedId = report.CommunityReportId;
                    var savedDiskPath = Path.Combine(_env.WebRootPath, photoPath.TrimStart('/'));
                    var savedName = Path.GetFileName(savedDiskPath);
                    _ = Task.Run(() => AnalyzePhotoInBackgroundAsync(savedId, savedDiskPath, savedName));
                }

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

        /// <summary>
        /// Background photo-authenticity check. Best-effort by design: every
        /// failure path marks the verification Failed/UnableToDetermine and
        /// never affects the already-submitted report.
        /// </summary>
        private async Task AnalyzePhotoInBackgroundAsync(int reportId, string diskPath, string fileName)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var verifier = scope.ServiceProvider.GetRequiredService<IImageVerificationService>();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                ImageVerificationResult result;
                try
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(diskPath);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    result = await verifier.AnalyzeAsync(bytes, fileName, cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background photo analysis I/O failed.");
                    result = new ImageVerificationResult { Status = "Failed", Reason = "Photo could not be read for analysis." };
                }

                try
                {
                    var stored = await db.CommunityReports.FindAsync(reportId);
                    if (stored is null || stored.PhotoPath is null)
                    {
                        return;
                    }
                    stored.ImageVerificationStatus = result.Status;
                    stored.ImageVerificationConfidence = result.Confidence;
                    stored.ImageVerificationReason = result.Reason;
                    stored.ImageVerificationAnalyzedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background photo analysis save failed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background photo analysis crashed.");
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
        /// <summary>Optional. Residents report a location; staff may link a canal.</summary>
        public int? CanalId { get; set; }

        [Required(ErrorMessage = "Report type is required")]
        [StringLength(50, ErrorMessage = "Report type cannot exceed 50 characters")]
        public string ReportType { get; set; } = "Garbage / Debris";

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
