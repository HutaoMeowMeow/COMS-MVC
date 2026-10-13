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
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
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
                if (User.IsInRole("Barangay"))
                {
                    var scopeUser = await _userManager.GetUserAsync(User);
                    if (!string.IsNullOrEmpty(scopeUser?.Barangay)
                        && canal.Barangay != scopeUser.Barangay)
                    {
                        return Forbid();
                    }
                }
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

            IQueryable<Canal> canalQuery = _context.Canals.Include(c => c.Sensors);
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                if (!string.IsNullOrEmpty(scopeUser?.Barangay))
                {
                    canalQuery = canalQuery.Where(c => c.Barangay == scopeUser.Barangay);
                }
            }
            var canals = await canalQuery.ToListAsync();

            // Single batched query for one latest reading per visible canal
            // (was one query per canal).
            var visibleCanalIds = canals.Select(c => c.CanalId).ToList();
            var latestReadings = (await _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Include(sr => sr.Canal)
                .Where(sr => visibleCanalIds.Contains(sr.CanalId))
                .GroupBy(sr => sr.CanalId)
                .Select(g => g.OrderByDescending(sr => sr.RecordedAt).FirstOrDefault())
                .ToListAsync())
                .Where(sr => sr != null)
                .Select(sr => sr!)
                .ToList();

            var model = new SensorReadingsViewModel
            {
                Readings = readings,
                LatestReadings = latestReadings,
                Canals = canals
            };

            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
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

            // Same barangay restriction as Index: a Barangay user may only
            // open readings from their own barangay (IDOR guard).
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                if (reading.Canal == null || reading.Canal.Barangay != scopeUser?.Barangay)
                {
                    return Forbid();
                }
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

            // Barangay users may only generate readings for their own barangay.
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                if (canal.Barangay != scopeUser?.Barangay)
                {
                    return Forbid();
                }
            }

            try
            {
                var reading = await _sensorService.GenerateSimulatedReadingAsync(canalId);
                var alerts = await _alertService.CheckThresholdsAsync(reading);

                var hub = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.MonitoringHub>>();
                // Staff-only broadcast (see StaffGroups).
                await hub.Clients.Groups(Hubs.MonitoringHub.StaffGroups).SendAsync("SensorReadingAdded", new
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
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> ChartData(int canalId, int hours = 24)
        {
            if (canalId <= 0)
            {
                return BadRequest("Invalid canal.");
            }
            if (hours < 1 || hours > 720)
            {
                return BadRequest("Hours must be between 1 and 720.");
            }
            // Barangay users may only chart their own barangay's canals.
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                var scopeCanal = await _context.Canals.FindAsync(canalId);
                if (scopeCanal == null)
                {
                    return NotFound();
                }
                if (scopeCanal.Barangay != scopeUser?.Barangay)
                {
                    return Forbid();
                }
            }
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
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> LatestReadingsJson()
        {
            IQueryable<SensorReading> latestQuery = _context.SensorReadings
                .Include(sr => sr.Sensor)
                .Include(sr => sr.Canal);
            // Barangay users only receive their own barangay's latest readings.
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                if (!string.IsNullOrEmpty(scopeUser?.Barangay))
                {
                    var barangay = scopeUser.Barangay;
                    latestQuery = latestQuery.Where(sr => sr.Canal.Barangay == barangay);
                }
            }
            var readings = await latestQuery
                .GroupBy(sr => sr.CanalId)
                .Select(g => g.OrderByDescending(sr => sr.RecordedAt).FirstOrDefault())
                .ToListAsync();

            return Json(readings.Where(sr => sr != null).Select(sr => new
            {
                sr!.SensorReadingId,
                sr.SensorId,
                sr.CanalId,
                CanalName = sr.Canal != null ? sr.Canal.CanalName : "Unknown",
                SensorType = sr.Sensor != null ? sr.Sensor.SensorType : "Unknown",
                SensorCode = sr.Sensor != null ? sr.Sensor.SensorCode : "Unknown",
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
