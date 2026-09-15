using Microsoft.AspNetCore.Mvc;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class SensorReadingsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ISensorService _sensorService;
        private readonly IAlertService _alertService;

        public SensorReadingsController(ApplicationDbContext context,
            ISensorService sensorService,
            IAlertService alertService)
        {
            _context = context;
            _sensorService = sensorService;
            _alertService = alertService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? canalId = null)
        {
            IQueryable<SensorReading> query = _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Include(sr => sr.Canal);

            if (canalId.HasValue)
            {
                query = query.Where(sr => sr.CanalId == canalId.Value);
                var canal = await _context.Canals.FindAsync(canalId.Value);
                if (canal == null)
                    return NotFound();
                ViewData["CurrentCanal"] = canal;
            }
            else
            {
                if (User.IsInRole("Barangay"))
                {
                    var user = await _userManager.GetUserAsync(User);
                    if (!string.IsNullOrEmpty(user?.Barangay))
                    {
                        query = query.Where(sr => sr.Canal.Barangay == user.Barangay);
                    }
                }
            }

            var readings = await query.OrderByDescending(sr => sr.RecordedAt).Take(50).ToListAsync();

            var latestReadings = new List<SensorReading>();
            var canals = await _context.Canals
                .Include(c => c.Sensors)
                .ToListAsync();

            foreach (var canal in canals)
            {
                var reading = await _context.SensorReadings
                    .Include(sr => sr.Sensor)
                    .Include(sr => sr.Canal)
                    .Where(sr => sr.CanalId == canal.CanalId)
                    .OrderByDescending(sr => sr.RecordedAt)
                    .FirstOrDefaultAsync();

                if (reading != null)
                {
                    latestReadings.Add(reading);
                }
            }

            var model = new SensorReadingsViewModel
            {
                Readings = readings,
                LatestReadings = latestReadings,
                Canals = canals
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var reading = await _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Include(sr => sr.Canal)
                .Include(sr => sr.Alerts)
                .FirstOrDefaultAsync(sr => sr.SensorReadingId == id);

            if (reading == null)
            {
                return NotFound();
            }

            return View(reading);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> GenerateTestReading(int canalId)
        {
            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                return NotFound();
            }

            try
            {
                var reading = await _sensorService.GenerateSimulatedReadingAsync(canalId);
                var alerts = await _alertService.CheckThresholdsAsync(reading);

                var hub = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.MonitoringHub>>();
                await hub.Clients.All.SendAsync("SensorReadingAdded", new
                {
                    reading.SensorReadingId,
                    reading.SensorId,
                    reading.CanalId,
                    reading.WaterLevel,
                    reading.FlowRate,
                    reading.DebrisLevel,
                    reading.Turbidity,
                    reading.Temperature,
                    reading.RecordedAt,
                    reading.IsSimulated,
                    CanalName = canal.CanalName,
                    AlertCount = alerts.Count
                });

                if (alerts.Any())
                {
                    TempData["AlertMessage"] = $"Test reading generated for '{canal.CanalName}'. {alerts.Count} alert(s) triggered.";
                }
                else
                {
                    TempData["SuccessMessage"] = $"Test reading generated for '{canal.CanalName}'. No alerts triggered.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Failed to generate reading: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> ChartData(int canalId, int hours = 24)
        {
            var since = DateTime.UtcNow.AddHours(-hours);

            var readings = await _context.SensorReadings
                .Where(sr => sr.CanalId == canalId && sr.RecordedAt >= since)
                .OrderBy(sr => sr.RecordedAt)
                .Select(sr => new
                {
                    sr.RecordedAt,
                    sr.WaterLevel,
                    sr.FlowRate,
                    sr.DebrisLevel,
                    sr.Turbidity,
                    sr.Temperature,
                    sr.IsSimulated
                })
                .ToListAsync();

            var canal = await _context.Canals.FindAsync(canalId);

            return Json(new
            {
                canalName = canal?.CanalName ?? "Unknown",
                warningLevel = canal?.WarningWaterLevel ?? 0,
                criticalLevel = canal?.CriticalWaterLevel ?? 0,
                normalLevel = canal?.NormalWaterLevel ?? 0,
                readings
            });
        }

        [HttpGet]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> LatestReadingsJson()
        {
            var readings = await _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Include(sr => sr.Canal)
                .GroupBy(sr => sr.CanalId)
                .Select(g => g.OrderByDescending(sr => sr.RecordedAt).FirstOrDefault())
                .ToListAsync();

            return Json(readings.Select(sr => new
            {
                sr.SensorReadingId,
                sr.SensorId,
                sr.CanalId,
                sr.Canal.CanalName,
                sr.Sensor.SensorType,
                sr.Sensor.SensorCode,
                sr.WaterLevel,
                sr.FlowRate,
                sr.DebrisLevel,
                sr.Turbidity,
                sr.Temperature,
                sr.RecordedAt,
                sr.IsSimulated
            }));
        }

        private UserManager<ApplicationUser> _userManager => HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
    }

    public class SensorReadingsViewModel
    {
        public List<SensorReading> Readings { get; set; } = new List<SensorReading>();

        public List<SensorReading> LatestReadings { get; set; } = new List<SensorReading>();

        public List<Canal> Canals { get; set; } = new List<Canal>();
    }
}
