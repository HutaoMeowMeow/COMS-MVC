using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class CanalsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public CanalsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
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
            return View(new Canal());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create(Canal canal)
        {
            NormalizeCanalInput(canal);
            ValidateCebuLocation(canal);
            ValidateWaterLevels(canal);

            if (ModelState.IsValid)
            {
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
                    ModelState.AddModelError(string.Empty, "Could not save the canal. Please check your input and try again.");
                }
                catch (Exception)
                {
                    ModelState.AddModelError(string.Empty, "An unexpected error occurred while creating the canal. Please try again.");
                }
            }

            await PopulateStatusDropDown();
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
            ValidateCebuLocation(canal);
            ValidateWaterLevels(canal);

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
            // System coverage is Cebu province only.
            if (canal.Latitude < 9.30 || canal.Latitude > 11.45)
            {
                ModelState.AddModelError(nameof(canal.Latitude), "Latitude must be inside Cebu (9.30 to 11.45).");
            }
            if (canal.Longitude < 123.20 || canal.Longitude > 124.60)
            {
                ModelState.AddModelError(nameof(canal.Longitude), "Longitude must be inside Cebu (123.20 to 124.60).");
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
            var statuses = new List<SelectListItem>
            {
                new SelectListItem("Normal", "Normal"),
                new SelectListItem("Warning", "Warning"),
                new SelectListItem("Critical", "Critical"),
                new SelectListItem("Inactive", "Inactive")
            };
            ViewBag.StatusList = new SelectList(statuses, "Value", "Text");
        }

        private async Task<bool> CanalExists(int id)
        {
            return await _context.Canals.AnyAsync(c => c.CanalId == id);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Map()
        {
            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .Include(c => c.Alerts.Where(a => a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress"))
                .ToListAsync();

            return View(canals);
        }
    }
}
