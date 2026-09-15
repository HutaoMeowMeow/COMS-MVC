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
            if (ModelState.IsValid)
            {
                canal.CreatedAt = DateTime.UtcNow;
                _context.Canals.Add(canal);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' created successfully.";
                return RedirectToAction(nameof(Index));
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

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(canal);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' updated successfully.";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await CanalExists(canal.CanalId))
                    {
                        return NotFound();
                    }
                    ModelState.AddModelError(string.Empty, "The canal was modified by another user. Please refresh and try again.");
                }
                return RedirectToAction(nameof(Index));
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

            _context.Canals.Remove(canal);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Canal '{canal.CanalName}' deleted successfully.";
            return RedirectToAction(nameof(Index));
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
