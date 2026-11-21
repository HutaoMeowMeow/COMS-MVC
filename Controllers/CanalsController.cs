using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class CanalsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILookupService _lookups;
        private readonly IMemoryCache _cache;
        private readonly ICebuBoundaryService _cebuBoundary;

        private static readonly object SubmissionLock = new();
        private static readonly Regex SubmissionIdPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

        public CanalsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, ILookupService lookups,
            IMemoryCache cache, ICebuBoundaryService cebuBoundary)
        {
            _context = context;
            _userManager = userManager;
            _lookups = lookups;
            _cache = cache;
            _cebuBoundary = cebuBoundary;
        }

        /// <summary>
        /// Atomically claims a one-time submission key for 10 minutes. Returns
        /// false when the key was already claimed (duplicate POST).
        /// </summary>
        internal static bool TryClaimSubmission(IMemoryCache cache, string key)
        {
            lock (SubmissionLock)
            {
                if (cache.TryGetValue(key, out _))
                {
                    return false;
                }
                cache.Set(key, true, TimeSpan.FromMinutes(10));
                return true;
            }
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Index()
        {
            var query = _context.Canals
                .Include(c => c.Sensors)
                .Include(c => c.Alerts.Where(a => a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress"))
                .AsQueryable();

            if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                var barangay = user?.Barangay;
                if (!string.IsNullOrEmpty(barangay))
                {
                    query = query.Where(c => c.Barangay == barangay);
                }
            }

            var canals = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
            return View(canals);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Details(int id)
        {
            var canal = await _context.Canals
                .Include(c => c.Sensors)
                .Include(c => c.Alerts)
                    .ThenInclude(a => a.AssignedToUser)
                .FirstOrDefaultAsync(c => c.CanalId == id);

            if (canal == null)
            {
                return NotFound();
            }

            if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                if (canal.Barangay != user?.Barangay)
                {
                    return Forbid();
                }
            }

            return View(canal);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create()
        {
            await PopulateStatusDropDown();
            ViewBag.SubmissionId = Guid.NewGuid().ToString("N");
            return View(new Canal());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create(Canal canal, [FromForm] string? submissionId)
        {
            if (string.IsNullOrEmpty(submissionId) || !SubmissionIdPattern.IsMatch(submissionId))
            {
                ModelState.AddModelError(string.Empty, "Form expired, please submit again.");
            }
            NormalizeCanalInput(canal);
            ValidateCebuLocation(canal);
            ValidateWaterLevels(canal);
            if (!await _lookups.IsValidAsync(LookupService.Categories.CanalStatus, canal.Status))
            {
                ModelState.AddModelError(nameof(canal.Status), "Please select a valid status.");
            }

            if (ModelState.IsValid)
            {
                var submissionKey = $"canal-submit:{_userManager.GetUserId(User)}:{submissionId!.ToLowerInvariant()}";
                if (!TryClaimSubmission(_cache, submissionKey))
                {
                    TempData["SuccessMessage"] = "This canal was already submitted.";
                    return RedirectToAction(nameof(Index));
                }
                try
                {
                    canal.CreatedAt = DateTime.UtcNow;
                    _context.Canals.Add(canal);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' created successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateException)
                {
                    _cache.Remove(submissionKey);
                    ModelState.AddModelError(string.Empty, "Could not save the canal. Please check your input and try again.");
                }
                catch (Exception)
                {
                    _cache.Remove(submissionKey);
                    ModelState.AddModelError(string.Empty, "An unexpected error occurred while creating the canal. Please try again.");
                }
            }

            await PopulateStatusDropDown();
            ViewBag.SubmissionId = Guid.NewGuid().ToString("N");
            return View(canal);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id)
        {
            var canal = await _context.Canals.FindAsync(id);
            if (canal == null)
            {
                return NotFound();
            }

            await PopulateStatusDropDown();
            return View(canal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id, Canal canal)
        {
            if (id != canal.CanalId)
            {
                return NotFound();
            }

            NormalizeCanalInput(canal);
            // Records saved before the polygon rule stay editable while their point is unchanged.
            var pointUnchanged = await _context.Canals.AnyAsync(c => c.CanalId == id
                && Math.Abs(c.Latitude - canal.Latitude) < 1e-7 && Math.Abs(c.Longitude - canal.Longitude) < 1e-7);
            if (!pointUnchanged)
            {
                ValidateCebuLocation(canal);
            }
            ValidateWaterLevels(canal);
            if (!await _lookups.IsValidAsync(LookupService.Categories.CanalStatus, canal.Status))
            {
                ModelState.AddModelError(nameof(canal.Status), "Please select a valid status.");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existing = await _context.Canals.AsNoTracking().FirstOrDefaultAsync(c => c.CanalId == id);
                    if (existing == null)
                    {
                        return NotFound();
                    }
                    canal.CreatedAt = existing.CreatedAt;
                    _context.Update(canal);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' updated successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await CanalExists(canal.CanalId))
                    {
                        return NotFound();
                    }
                    ModelState.AddModelError(string.Empty, "The canal was modified by another user. Please refresh and try again.");
                }
                catch (DbUpdateException)
                {
                    ModelState.AddModelError(string.Empty, "Could not save the canal. Please check your input and try again.");
                }
                catch (Exception)
                {
                    ModelState.AddModelError(string.Empty, "An unexpected error occurred while saving the canal. Please try again.");
                }
            }

            await PopulateStatusDropDown();
            return View(canal);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var canal = await _context.Canals
                .Include(c => c.Sensors)
                .FirstOrDefaultAsync(c => c.CanalId == id);

            if (canal == null)
            {
                return NotFound();
            }

            return View(canal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id, IFormCollection collection)
        {
            var canal = await _context.Canals.FindAsync(id);
            if (canal == null)
            {
                return NotFound();
            }

            try
            {
                _context.Canals.Remove(canal);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' deleted successfully.";
            }
            catch (DbUpdateException)
            {
                TempData["ErrorMessage"] = $"Cannot delete '{canal.CanalName}' because it still has sensors, readings, alerts, or reports. Remove or reassign them first.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the canal. Please try again.";
            }
            return RedirectToAction(nameof(Index));
        }

        private void NormalizeCanalInput(Canal canal)
        {
            canal.CanalName = (canal.CanalName ?? string.Empty).Trim();
            canal.Location = (canal.Location ?? string.Empty).Trim();
            canal.Barangay = (canal.Barangay ?? string.Empty).Trim();
            canal.City = (canal.City ?? string.Empty).Trim();
            canal.Status = string.IsNullOrWhiteSpace(canal.Status) ? "Normal" : canal.Status.Trim();
        }

        private void ValidateCebuLocation(Canal canal)
        {
            // Same Cebu report-area polygon the map mask uses.
            if (!_cebuBoundary.Contains(canal.Latitude, canal.Longitude))
            {
                ModelState.AddModelError(nameof(canal.Latitude), "Location must be inside Cebu. Please select a point within the highlighted area.");
            }
        }

        private void ValidateWaterLevels(Canal canal)
        {
            if (canal.WarningWaterLevel > 0 && canal.NormalWaterLevel > 0
                && canal.WarningWaterLevel <= canal.NormalWaterLevel)
            {
                ModelState.AddModelError(nameof(canal.WarningWaterLevel), "Warning level must be greater than normal level.");
            }
            if (canal.CriticalWaterLevel > 0 && canal.WarningWaterLevel > 0
                && canal.CriticalWaterLevel <= canal.WarningWaterLevel)
            {
                ModelState.AddModelError(nameof(canal.CriticalWaterLevel), "Critical level must be greater than warning level.");
            }
        }

        private async Task PopulateStatusDropDown()
        {
            ViewBag.StatusList = new SelectList(await _lookups.GetOptionsAsync(LookupService.Categories.CanalStatus), "Value", "DisplayName");
        }

        private async Task<bool> CanalExists(int id)
        {
            return await _context.Canals.AnyAsync(c => c.CanalId == id);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Map()
        {
            IQueryable<Canal> query = _context.Canals.AsNoTracking();
            if (User.IsInRole("Barangay"))
            {
                var user = await _userManager.GetUserAsync(User);
                if (!string.IsNullOrEmpty(user?.Barangay))
                {
                    query = query.Where(c => c.Barangay == user.Barangay);
                }
            }
            var canals = await query.ToListAsync();

            return View(canals);
        }
    }
}
