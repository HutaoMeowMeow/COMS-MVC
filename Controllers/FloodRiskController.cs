namespace COMS_MVC.Controllers
{
    [Authorize]
    public class FloodRiskController : Controller
    {
        private readonly IFloodRiskService _floodRiskService;
        private readonly ApplicationDbContext _context;
        private readonly FloodRiskRetentionOptions _retention;

        public FloodRiskController(
            IFloodRiskService floodRiskService,
            ApplicationDbContext context,
            Microsoft.Extensions.Options.IOptions<FloodRiskRetentionOptions>? retentionOptions = null)
        {
            _floodRiskService = floodRiskService;
            _context = context;
            _retention = retentionOptions?.Value ?? FloodRiskRetentionOptions.Default;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .ToListAsync();

            // Single batched lookup (was one query per canal).
            var assessments = await _floodRiskService.GetLatestForCanalsAsync(
                canals.Select(c => c.CanalId));

            // The news panel loads asynchronously in the browser (see Index view):
            // Flood Risk never awaits the external news provider, so a slow or
            // dead provider cannot delay or break this core page.
            ViewBag.Canals = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(canals, "CanalId", "CanalName");
            return View(assessments);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int canalId = 0, int id = 0, int page = 1)
        {
            var resolvedId = canalId != 0 ? canalId : id;
            var canal = await _context.Canals.FindAsync(resolvedId);
            if (canal == null)
            {
                return NotFound();
            }

            // Load the latest assessment first (the row used by dashboards and
            // risk calculations), then only one bounded page of history. The
            // history table is never materialized in full.
            var latest = await _context.FloodRiskAssessments
                .AsNoTracking()
                .Where(a => a.CanalId == resolvedId)
                .OrderByDescending(a => a.AssessmentDate)
                .FirstOrDefaultAsync();

            var pageSize = _retention.PageSize;
            var totalHistory = await _context.FloodRiskAssessments
                .AsNoTracking()
                .CountAsync(a => a.CanalId == resolvedId);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalHistory / (double)pageSize));
            var currentPage = Math.Clamp(page, 1, totalPages);

            var history = await _context.FloodRiskAssessments
                .AsNoTracking()
                .Where(a => a.CanalId == resolvedId)
                .OrderByDescending(a => a.AssessmentDate)
                .Skip((currentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Page = currentPage;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalHistory = totalHistory;
            ViewBag.PageSize = pageSize;

            var model = new FloodRiskDetailsViewModel
            {
                Canal = canal,
                LatestAssessment = latest,
                History = history
            };

            return View(model);
        }

        // Risk calculation mutates the database, so it is POST-only with
        // anti-forgery (a state-changing GET could be triggered by crawlers,
        // prefetch, or a crafted <img> tag). The legacy GET overloads below
        // only redirect and never write.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Assess(int canalId)
        {
            if (canalId <= 0)
            {
                return NotFound();
            }
            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                return NotFound();
            }

            try
            {
                var assessment = await _floodRiskService.CalculateRiskAsync(canal);

                TempData["SuccessMessage"] = $"Risk assessment generated for '{canal.CanalName}'. Score: {assessment.RiskScore}/100 ({assessment.RiskLevel})";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not generate the risk assessment. Please try again.";
            }
            return RedirectToAction(nameof(Details), new { canalId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU")]
        public async Task<IActionResult> AssessAll()
        {
            try
            {
                var canals = await _context.Canals.ToListAsync();
                foreach (var canal in canals)
                {
                    try
                    {
                        await _floodRiskService.CalculateRiskAsync(canal);
                    }
                    catch
                    {
                        // Continue with remaining canals; one failure should not block the batch.
                    }
                }

                TempData["SuccessMessage"] = $"Risk assessments generated for {canals.Count} canal(s).";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Could not generate risk assessments. Please try again.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> RiskChartData()
        {
            var canals = await _context.Canals.ToListAsync();
            // Single batched lookup (was one query per canal).
            var latestByCanal = (await _floodRiskService.GetLatestForCanalsAsync(
                canals.Select(c => c.CanalId)))
                .ToDictionary(a => a.CanalId);
            var data = new List<object>();

            foreach (var canal in canals)
            {
                latestByCanal.TryGetValue(canal.CanalId, out var latest);
                data.Add(new
                {
                    canal.CanalName,
                    Score = latest?.RiskScore ?? 0,
                    Level = latest?.RiskLevel ?? "Unknown",
                    CanalId = canal.CanalId
                });
            }

            return Json(data);
        }
    }

    public class FloodRiskDetailsViewModel
    {
        public Canal Canal { get; set; }

        public FloodRiskAssessment? LatestAssessment { get; set; }

        public List<FloodRiskAssessment> History { get; set; } = new List<FloodRiskAssessment>();
    }
}
