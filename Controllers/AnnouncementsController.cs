using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class AnnouncementsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _env;

        private static readonly List<SelectListItem> Audiences = new()
        {
            new SelectListItem("All Users", "All"),
            new SelectListItem("Admin", "Admin"),
            new SelectListItem("LGU", "LGU"),
            new SelectListItem("Barangay", "Barangay"),
            new SelectListItem("Maintenance", "Maintenance"),
            new SelectListItem("Resident", "Resident"),
            new SelectListItem("LGU & Barangay", "LGU_Barangay"),
            new SelectListItem("Maintenance Personnel", "Maintenance_Resident")
        };

        public AnnouncementsController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment env)
        {
            _context = context;
            _userManager = userManager;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? audience = null)
        {
            var query = _context.Announcements
                .Include(a => a.PostedBy)
                .AsQueryable();

            var userRoles = await _userManager.GetRolesAsync(await _userManager.GetUserAsync(User));
            var isAdmin = userRoles.Contains("Admin");
            var isLGU = userRoles.Contains("LGU");

            if (!isAdmin && !isLGU)
            {
                if (audience != null)
                {
                    query = query.Where(a => a.TargetAudience == "All" || a.TargetAudience == audience);
                }
                else
                {
                    var primaryRole = userRoles.FirstOrDefault() ?? "All";
                    query = query.Where(a => a.TargetAudience == "All" ||
                                             a.TargetAudience == primaryRole ||
                                             a.TargetAudience == GetAudienceGroup(primaryRole));
                }
            }

            ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", audience ?? "All");

            var announcements = await query.OrderByDescending(a => a.CreatedAt).ToListAsync();
            return View(announcements);
        }

        private static string GetAudienceGroup(string role)
        {
            return role switch
            {
                "LGU" => "LGU_Barangay",
                "Barangay" => "LGU_Barangay",
                "Maintenance" => "Maintenance_Resident",
                "Resident" => "Maintenance_Resident",
                _ => "All"
            };
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var announcement = await _context.Announcements
                .Include(a => a.PostedBy)
                .FirstOrDefaultAsync(a => a.AnnouncementId == id);

            if (announcement == null)
            {
                return NotFound();
            }

            if (!User.IsInRole("Admin") && !User.IsInRole("LGU"))
            {
                var userRoles = await _userManager.GetRolesAsync(await _userManager.GetUserAsync(User));
                var primaryRole = userRoles.FirstOrDefault() ?? "All";

                if (announcement.TargetAudience != "All" &&
                    announcement.TargetAudience != primaryRole &&
                    announcement.TargetAudience != GetAudienceGroup(primaryRole))
                {
                    return Forbid();
                }
            }

            return View(announcement);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU")]
        public IActionResult Create()
        {
            ViewBag.Audiences = new SelectList(Audiences, "Value", "Text");
            return View(new Announcement { CreatedAt = DateTime.UtcNow });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> Create(Announcement announcement, IFormFile? imageFile)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
                return View(announcement);
            }

            var userId = int.Parse(_userManager.GetUserId(User)!);
            announcement.PostedByUserId = userId;
            announcement.CreatedAt = DateTime.UtcNow;

            if (imageFile != null && imageFile.Length > 0)
            {
                if (imageFile.Length > 5 * 1024 * 1024)
                {
                    ModelState.AddModelError("imageFile", "Image size must be less than 5MB.");
                    ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
                    return View(announcement);
                }

                var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
                if (!allowedTypes.Contains(imageFile.ContentType))
                {
                    ModelState.AddModelError("imageFile", "Only JPG, PNG, and GIF images are allowed.");
                    ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
                    return View(announcement);
                }

                var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "announcements");
                Directory.CreateDirectory(uploadsFolder);

                var uniqueName = $"announce_{Guid.NewGuid():N}_{Path.GetFileName(imageFile.FileName)}";
                var filePath = Path.Combine(uploadsFolder, uniqueName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(stream);
                }
                announcement.ImagePath = $"/uploads/announcements/{uniqueName}";
            }

            _context.Announcements.Add(announcement);
            await _context.SaveChangesAsync();

            await CreateNotificationForAnnouncementAsync(announcement);

            TempData["SuccessMessage"] = "Announcement posted successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> Edit(int id)
        {
            var announcement = await _context.Announcements.FindAsync(id);
            if (announcement == null)
            {
                return NotFound();
            }

            ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
            return View(announcement);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> Edit(int id, Announcement announcement, IFormFile? imageFile)
        {
            if (id != announcement.AnnouncementId)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
                return View(announcement);
            }

            var existing = await _context.Announcements.AsNoTracking().FirstOrDefaultAsync(a => a.AnnouncementId == id);
            if (existing == null)
            {
                return NotFound();
            }

            try
            {
                if (imageFile != null && imageFile.Length > 0)
                {
                    if (existing.ImagePath != null)
                    {
                        var oldPath = Path.Combine(_env.WebRootPath, existing.ImagePath.TrimStart('/'));
                        if (System.IO.File.Exists(oldPath))
                        {
                            System.IO.File.Delete(oldPath);
                        }
                    }

                    var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "announcements");
                    Directory.CreateDirectory(uploadsFolder);

                    var uniqueName = $"announce_{Guid.NewGuid():N}_{Path.GetFileName(imageFile.FileName)}";
                    var filePath = Path.Combine(uploadsFolder, uniqueName);
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await imageFile.CopyToAsync(stream);
                    }
                    announcement.ImagePath = $"/uploads/announcements/{uniqueName}";
                }
                else
                {
                    announcement.ImagePath = existing.ImagePath;
                }

                announcement.CreatedAt = existing.CreatedAt;
                announcement.PostedByUserId = existing.PostedByUserId;

                _context.Update(announcement);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "Announcement updated successfully.";
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, "The announcement was modified by another user. Please refresh and try again.");
                ViewBag.Audiences = new SelectList(Audiences, "Value", "Text", announcement.TargetAudience);
                return View(announcement);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> Delete(int id)
        {
            var announcement = await _context.Announcements
                .Include(a => a.PostedBy)
                .FirstOrDefaultAsync(a => a.AnnouncementId == id);

            if (announcement == null)
            {
                return NotFound();
            }

            return View(announcement);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> Delete(int id, IFormCollection collection)
        {
            var announcement = await _context.Announcements.FindAsync(id);
            if (announcement == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrEmpty(announcement.ImagePath))
            {
                var filePath = Path.Combine(_env.WebRootPath, announcement.ImagePath.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }

            _context.Announcements.Remove(announcement);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = "Announcement deleted successfully.";
            return RedirectToAction(nameof(Index));
        }

        private async Task CreateNotificationForAnnouncementAsync(Announcement announcement)
        {
            var notificationService = HttpContext.RequestServices.GetRequiredService<INotificationService>();

            var targetRoles = announcement.TargetAudience == "All"
                ? new[] { "Admin", "LGU", "Barangay", "Maintenance", "Resident" }
                : announcement.TargetAudience == "LGU_Barangay"
                    ? new[] { "LGU", "Barangay" }
                    : announcement.TargetAudience == "Maintenance_Resident"
                        ? new[] { "Maintenance", "Resident" }
                        : new[] { announcement.TargetAudience };

            foreach (var role in targetRoles)
            {
                await notificationService.CreateNotificationForRoleAsync(
                    role,
                    "New Announcement",
                    announcement.Title,
                    "Announcement",
                    null,
                    null);
            }
        }
    }
}
