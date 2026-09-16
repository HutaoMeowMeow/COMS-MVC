namespace COMS_MVC.Controllers
{
    [Authorize]
    public class FloodRiskController : Controller
    {
        private readonly IFloodRiskService _floodRiskService;
        private readonly ApplicationDbContext _context;

        public FloodRiskController(IFloodRiskService floodRiskService, ApplicationDbContext context)
        {
            _floodRiskService = floodRiskService;
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .ToListAsync();

            var assessments = new List<FloodRiskAssessment>();
            foreach (var canal in canals)
            {
                var latest = await _floodRiskService.GetLatestForCanalAsync(canal.CanalId);
                if (latest != null)
                {
                    assessments.Add(latest);
                }
            }

            ViewBag.Canals = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(canals, "CanalId", "CanalName");
            return View(assessments);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int canalId = 0)
        {
            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                return NotFound();
            }

            var assessments = await _context.FloodRiskAssessments
                .Where(a => a.CanalId == canalId)
                .OrderByDescending(a => a.AssessmentDate)
                .ToListAsync();

            var latest = assessments.FirstOrDefault();

            var model = new FloodRiskDetailsViewModel
            {
                Canal = canal,
                LatestAssessment = latest,
                History = assessments
            };

            return View(model);
        }

        [HttpGet]
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

        [HttpGet]
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
            var data = new List<object>();

            foreach (var canal in canals)
            {
                var latest = await _context.FloodRiskAssessments
                    .Where(a => a.CanalId == canal.CanalId)
                    .OrderByDescending(a => a.AssessmentDate)
                    .FirstOrDefaultAsync();

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
