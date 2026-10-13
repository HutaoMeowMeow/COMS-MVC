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
        private readonly IImageRelevanceService _imageRelevance;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportsController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly ILookupService _lookups;

        public ReportsController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notificationService,
            IImageVerificationService imageVerification,
            IImageRelevanceService imageRelevance,
            IServiceScopeFactory scopeFactory,
            ILogger<ReportsController> logger,
            IWebHostEnvironment env,
            ILookupService lookups)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
            _imageVerification = imageVerification;
            _imageRelevance = imageRelevance;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _env = env;
            _lookups = lookups;
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

            ViewBag.ReportTypes = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.ReportType), "Value", "DisplayName");
            ViewBag.ReportStatuses = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.ReportStatus), "Value", "DisplayName");

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

            // Status-update form on this page (staff roles only in the view).
            ViewBag.ReportStatuses = new SelectList(
                await _lookups.GetOptionsAsync(LookupService.Categories.ReportStatus), "Value", "DisplayName", report.Status);
            return View(report);
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Create(int? canalId = null)
        {
            var typeOptions = await _lookups.GetOptionsAsync(LookupService.Categories.ReportType);
            ViewBag.ReportTypes = new SelectList(typeOptions, "Value", "DisplayName");

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

            if (!await _lookups.IsValidAsync(LookupService.Categories.ReportType, model.ReportType))
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

            if (model.Photo != null && model.Photo.Length > 0)
            {
                var headerError = ImageUploadValidator.ValidateHeader(
                    model.Photo.FileName, model.Photo.ContentType, model.Photo.Length);
                if (headerError != null)
                {
                    ModelState.AddModelError(nameof(model.Photo), headerError);
                }
            }

            ViewBag.ReportTypes = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.ReportType), "Value", "DisplayName", model.ReportType);

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
            byte[]? photoBytes = null;
            string? photoExt = null;
            ImageRelevanceResult? photoRelevance = null;
            if (model.Photo != null && model.Photo.Length > 0)
            {
                if (string.IsNullOrEmpty(_env.WebRootPath))
                {
                    ModelState.AddModelError(nameof(model.Photo), "File uploads are not configured on this server.");
                    return View(model);
                }
                // Fully validated in memory BEFORE anything touches the disk:
                // renamed videos/GIFs and corrupt files are rejected here and
                // never saved anywhere.
                var (bytes, ext, error) = await ImageUploadValidator.ValidateAsync(model.Photo);
                if (error != null)
                {
                    ModelState.AddModelError(nameof(model.Photo), error);
                    return View(model);
                }
                // Content-relevance gate (server-side, before anything saves):
                // the photo must show a canal, drainage, flood, or related
                // condition. Rejected photos are discarded here and never
                // reach storage, so crafted requests cannot bypass the check.
                ImageRelevanceResult? relevance = null;
                if (_imageRelevance.IsEnabled)
                {
                    relevance = await _imageRelevance.AnalyzeAsync(bytes!);
                    if (relevance.Status == "Irrelevant")
                    {
                        ModelState.AddModelError(nameof(model.Photo),
                            "This image does not appear to show a canal obstruction, drainage issue, flooding, or another condition monitored by COMS. Please upload a relevant image.");
                        return View(model);
                    }
                    if (relevance.Status == "Uncertain")
                    {
                        ModelState.AddModelError(nameof(model.Photo),
                            relevance.Reason ?? "The photo could not be reliably assessed. Please upload a clearer photo.");
                        return View(model);
                    }
                }
                photoRelevance = relevance;
                photoBytes = bytes;
                photoExt = ext;
                try
                {
                    var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "reports");
                    Directory.CreateDirectory(uploadsFolder);

                    var uniqueName = $"report_{Guid.NewGuid():N}{photoExt}";
                    var filePath = Path.Combine(uploadsFolder, uniqueName);
                    await System.IO.File.WriteAllBytesAsync(filePath, photoBytes!);
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
                    ImageRelevanceStatus = photoRelevance?.Status,
                    ImageRelevanceConfidence = photoRelevance?.Confidence,
                    ImageRelevanceReason = photoRelevance?.Reason,
                    ImageSuggestedCategory = photoRelevance?.SuggestedCategory,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.CommunityReports.Add(report);
                await _context.SaveChangesAsync();

                // Category mismatch is advisory, never blocking: the photo is
                // relevant to COMS but suggests a different report type.
                if (photoRelevance?.SuggestedCategory != null
                    && !photoRelevance.SuggestedCategory.Equals(model.ReportType, StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AlertMessage"] = $"Photo accepted, but it looks more like '{photoRelevance.SuggestedCategory}' than '{model.ReportType}'. Please check the report type.";
                }

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
            ViewBag.ReportStatuses = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.ReportStatus), "Value", "DisplayName", report.Status);

            return View(report);
        }

        /// <summary>
        /// Staff status update. Only status is persisted: CommunityReport has
        /// no assignee or notes columns, so no assigneeId parameter is accepted
        /// (passing one would silently do nothing). The notes field posted by
        /// the Details form is accepted for UX compatibility and length-checked,
        /// but is not stored — persisting it requires a schema change (see report).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay")]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? notes)
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

            if (string.IsNullOrWhiteSpace(status) || !await _lookups.IsValidAsync(LookupService.Categories.ReportStatus, status))
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
