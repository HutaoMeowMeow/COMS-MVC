using COMS_MVC.Helpers;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

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

        private const int DefaultChartHours = 5;
        private const int DefaultChartPoints = 300;
        private const int DefaultPageSize = 20;

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> Index(int? canalId = null, string? fromDate = null, string? fromTime = null, string? toDate = null, string? toTime = null, int page = 1, int pageSize = DefaultPageSize)
        {
            IQueryable<SensorReading> query = _context.SensorReadings
                .AsNoTracking()
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
                ViewBag.CurrentCanalId = canalId.Value;
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

            DateTime? fromDt = null;
            DateTime? toDt = null;
            string? rangeError = null;

            if (!string.IsNullOrWhiteSpace(fromDate))
            {
                if (PhTime.TryParsePhDateTime(fromDate, fromTime, endOfDay: false, out var parsedFrom))
                    fromDt = parsedFrom;
                else
                    rangeError = "Invalid start date/time.";
            }
            if (!string.IsNullOrWhiteSpace(toDate))
            {
                if (PhTime.TryParsePhDateTime(toDate, toTime, endOfDay: true, out var parsedTo))
                    toDt = parsedTo;
                else
                    rangeError = "Invalid end date/time.";
            }
            if (fromDt.HasValue && toDt.HasValue && fromDt.Value > toDt.Value)
            {
                rangeError = "Invalid date range. Start date/time must be before or equal to end date/time.";
            }
            if (rangeError != null)
            {
                TempData["ErrorMessage"] = rangeError;
                fromDt = null;
                toDt = null;
                fromDate = null;
                fromTime = null;
                toDate = null;
                toTime = null;
            }

            ViewData["FromDate"] = fromDate;
            ViewData["FromTime"] = fromTime;
            ViewData["ToDate"] = toDate;
            ViewData["ToTime"] = toTime;

            if (fromDt.HasValue)
            {
                query = query.Where(sr => sr.RecordedAt >= fromDt.Value);
            }
            if (toDt.HasValue)
            {
                query = query.Where(sr => sr.RecordedAt <= toDt.Value);
            }
            if (!fromDt.HasValue && !toDt.HasValue)
            {
                var fiveHoursAgo = DateTime.UtcNow.AddHours(-5);
                query = query.Where(sr => sr.RecordedAt >= fiveHoursAgo);
            }

            pageSize = Math.Clamp(pageSize, 10, 100);
            var totalReadings = await query.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalReadings / (double)pageSize));
            var currentPage = Math.Clamp(page, 1, totalPages);

            var readings = await query
                .OrderByDescending(sr => sr.RecordedAt)
                .Skip((currentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Page = currentPage;
            ViewBag.TotalPages = totalPages;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalReadings = totalReadings;

            IQueryable<Canal> canalQuery = _context.Canals.AsNoTracking().Include(c => c.Sensors);
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
                .AsNoTracking()
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
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> ChartData(int canalId, int hours = DefaultChartHours, int maxPoints = DefaultChartPoints, string? fromDate = null, string? fromTime = null, string? toDate = null, string? toTime = null)
        {
            if (canalId <= 0)
            {
                return BadRequest("Invalid canal.");
            }
            if (hours < 1 || hours > 720)
            {
                return BadRequest("Hours must be between 1 and 720.");
            }
            maxPoints = Math.Clamp(maxPoints, 20, 2000);

            DateTime? fromDt = null;
            DateTime? toDt = null;
            if (!string.IsNullOrWhiteSpace(fromDate))
            {
                if (!PhTime.TryParsePhDateTime(fromDate, fromTime, endOfDay: false, out var parsedFrom))
                    return BadRequest("Invalid start date/time.");
                fromDt = parsedFrom;
            }
            if (!string.IsNullOrWhiteSpace(toDate))
            {
                if (!PhTime.TryParsePhDateTime(toDate, toTime, endOfDay: true, out var parsedTo))
                    return BadRequest("Invalid end date/time.");
                toDt = parsedTo;
            }
            if (fromDt.HasValue && toDt.HasValue && fromDt.Value > toDt.Value)
            {
                return BadRequest("Start date/time must be before or equal to end date/time.");
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

            // Default window is the most recent 5 hours (not every historical
            // reading). A custom from/to range still uses server-side filters.
            var rangeEnd = toDt ?? DateTime.UtcNow;
            var rangeStart = fromDt ?? rangeEnd.AddHours(-hours);
            if (rangeStart > rangeEnd)
            {
                return BadRequest("Start date/time must be before or equal to end date/time.");
            }

            var readingsQuery = _context.SensorReadings
                .AsNoTracking()
                .Where(sr => sr.CanalId == canalId
                             && sr.RecordedAt >= rangeStart
                             && sr.RecordedAt <= rangeEnd);

            // Query only the selected canal/range and only chart fields.
            var rawReadings = await readingsQuery
                .OrderBy(sr => sr.RecordedAt)
                .Select(sr => new ChartReadingPoint
                {
                    RecordedAt = sr.RecordedAt,
                    WaterLevel = sr.WaterLevel,
                    FlowRate = sr.FlowRate,
                    DebrisLevel = sr.DebrisLevel,
                    Turbidity = sr.Turbidity,
                    Temperature = sr.Temperature,
                    IsSimulated = sr.IsSimulated
                })
                .ToListAsync();

            var readings = ChartDataDownsampler.Downsample(
                rawReadings, maxPoints, rangeStart, rangeEnd);

            var canal = await _context.Canals
                .AsNoTracking()
                .Where(c => c.CanalId == canalId)
                .Select(c => new
                {
                    c.CanalName,
                    c.WarningWaterLevel,
                    c.CriticalWaterLevel,
                    c.NormalWaterLevel
                })
                .FirstOrDefaultAsync();

            return Json(new
            {
                canalName = canal?.CanalName ?? "Unknown",
                warningLevel = canal?.WarningWaterLevel ?? 0,
                criticalLevel = canal?.CriticalWaterLevel ?? 0,
                normalLevel = canal?.NormalWaterLevel ?? 0,
                rangeStartUtc = rangeStart,
                rangeEndUtc = rangeEnd,
                timeZone = "Asia/Manila (UTC+8)",
                totalReadings = rawReadings.Count,
                returnedPoints = readings.Count,
                sampled = rawReadings.Count > readings.Count,
                maxPoints,
                readings = readings.Select(r => new
                {
                    recordedAt = r.RecordedAt,
                    waterLevel = r.WaterLevel,
                    flowRate = r.FlowRate,
                    debrisLevel = r.DebrisLevel,
                    turbidity = r.Turbidity,
                    temperature = r.Temperature,
                    isSimulated = r.IsSimulated,
                    isAggregate = r.IsAggregate,
                    aggregation = r.Aggregation,
                    bucketStartUtc = r.BucketStartUtc,
                    bucketEndUtc = r.BucketEndUtc
                })
            });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> WaterLevelTrendJson(int hours = DefaultChartHours, int maxPoints = 300)
        {
            if (hours < 1 || hours > 720)
            {
                return BadRequest("Hours must be between 1 and 720.");
            }
            maxPoints = Math.Clamp(maxPoints, 20, 1000);

            // Same visibility rules as the rest of the dashboard: Barangay
            // users only see canals in their own barangay; staff roles see all.
            IQueryable<Canal> canalQuery = _context.Canals.AsNoTracking();
            if (User.IsInRole("Barangay"))
            {
                var scopeUser = await _userManager.GetUserAsync(User);
                if (string.IsNullOrWhiteSpace(scopeUser?.Barangay))
                {
                    canalQuery = canalQuery.Where(c => false);
                }
                else
                {
                    var barangay = scopeUser.Barangay;
                    canalQuery = canalQuery.Where(c => c.Barangay == barangay);
                }
            }

            var canals = await canalQuery
                .Select(c => new
                {
                    c.CanalId,
                    c.CanalName,
                    c.NormalWaterLevel,
                    c.WarningWaterLevel,
                    c.CriticalWaterLevel
                })
                .ToListAsync();

            var rangeEnd = DateTime.UtcNow;
            var rangeStart = rangeEnd.AddHours(-hours);

            if (canals.Count == 0)
            {
                return Json(new DashboardTrendResponse
                {
                    RangeStartUtc = rangeStart,
                    RangeEndUtc = rangeEnd,
                    MaxPoints = maxPoints
                });
            }

            var canalIds = canals.Select(c => c.CanalId).ToList();
            var rawReadings = await _context.SensorReadings
                .AsNoTracking()
                .Where(sr => canalIds.Contains(sr.CanalId)
                             && sr.RecordedAt >= rangeStart
                             && sr.RecordedAt <= rangeEnd)
                .OrderBy(sr => sr.RecordedAt)
                .Select(sr => new
                {
                    sr.CanalId,
                    sr.RecordedAt,
                    sr.WaterLevel
                })
                .ToListAsync();

            // double.NaN/Infinity cannot be plotted; drop them rather than
            // letting Chart.js render a broken axis. No values are invented.
            var validReadings = rawReadings
                .Where(r => double.IsFinite(r.WaterLevel))
                .ToList();

            var readingsByCanal = validReadings
                .GroupBy(r => r.CanalId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var response = new DashboardTrendResponse
            {
                RangeStartUtc = rangeStart,
                RangeEndUtc = rangeEnd,
                TotalReadings = validReadings.Count,
                MaxPoints = maxPoints
            };

            foreach (var canal in canals)
            {
                if (!readingsByCanal.TryGetValue(canal.CanalId, out var canalReadings)
                    || canalReadings.Count == 0)
                {
                    continue;
                }

                var points = ChartDataDownsampler.Downsample(
                    canalReadings
                        .Select(r => new ChartReadingPoint
                        {
                            RecordedAt = r.RecordedAt,
                            WaterLevel = r.WaterLevel
                        })
                        .ToList(),
                    maxPoints,
                    rangeStart,
                    rangeEnd);

                if (points.Count == 0)
                {
                    continue;
                }

                response.Canals.Add(new DashboardTrendSeries
                {
                    CanalId = canal.CanalId,
                    CanalName = canal.CanalName,
                    NormalLevel = canal.NormalWaterLevel,
                    WarningLevel = canal.WarningWaterLevel,
                    CriticalLevel = canal.CriticalWaterLevel,
                    Readings = points
                        .Select(p => new DashboardTrendPoint
                        {
                            RecordedAt = p.RecordedAt,
                            WaterLevel = p.WaterLevel
                        })
                        .ToList()
                });
                response.ReturnedPoints += points.Count;
            }

            return Json(response);
        }

        [HttpGet]
        // Residents need the same read-only latest-reading feed for the Canal
        // Water Levels panel on their dashboard. The endpoint remains
        // authenticated (no anonymous access) and is still role-restricted.
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance,Resident")]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> LatestReadingsJson()
        {
            IQueryable<SensorReading> latestQuery = _context.SensorReadings
                .AsNoTracking()
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
                 sensorReadingId = sr!.SensorReadingId,
                 sensorId = sr.SensorId,
                 canalId = sr.CanalId,
                 canalName = sr.Canal != null ? sr.Canal.CanalName : "Unknown",
                 sensorType = sr.Sensor != null ? sr.Sensor.SensorType : "Unknown",
                 sensorCode = sr.Sensor != null ? sr.Sensor.SensorCode : "Unknown",
                 waterLevel = sr.WaterLevel,
                 flowRate = sr.FlowRate,
                 debrisLevel = sr.DebrisLevel,
                 turbidity = sr.Turbidity,
                 temperature = sr.Temperature,
                 recordedAt = sr.RecordedAt,
                 isSimulated = sr.IsSimulated,
                 normalLevel = sr.Canal != null ? sr.Canal.NormalWaterLevel : 0,
                 warningLevel = sr.Canal != null ? sr.Canal.WarningWaterLevel : 0,
                 criticalLevel = sr.Canal != null ? sr.Canal.CriticalWaterLevel : 0
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

    /// <summary>Water Level Trend payload used by the role dashboards.</summary>
    public sealed class DashboardTrendResponse
    {
        [JsonPropertyName("rangeStartUtc")]
        public DateTime RangeStartUtc { get; set; }

        [JsonPropertyName("rangeEndUtc")]
        public DateTime RangeEndUtc { get; set; }

        [JsonPropertyName("timeZone")]
        public string TimeZone { get; set; } = "Asia/Manila (UTC+8)";

        [JsonPropertyName("totalReadings")]
        public int TotalReadings { get; set; }

        [JsonPropertyName("returnedPoints")]
        public int ReturnedPoints { get; set; }

        [JsonPropertyName("maxPoints")]
        public int MaxPoints { get; set; }

        /// <summary>
        /// One real time series per visible canal. Canals without readings in
        /// the window are omitted; nothing is padded with fabricated points.
        /// </summary>
        [JsonPropertyName("canals")]
        public List<DashboardTrendSeries> Canals { get; set; } = new List<DashboardTrendSeries>();
    }

    public sealed class DashboardTrendSeries
    {
        [JsonPropertyName("canalId")]
        public int CanalId { get; set; }

        [JsonPropertyName("canalName")]
        public string CanalName { get; set; } = string.Empty;

        [JsonPropertyName("normalLevel")]
        public double NormalLevel { get; set; }

        [JsonPropertyName("warningLevel")]
        public double WarningLevel { get; set; }

        [JsonPropertyName("criticalLevel")]
        public double CriticalLevel { get; set; }

        [JsonPropertyName("readings")]
        public List<DashboardTrendPoint> Readings { get; set; } = new List<DashboardTrendPoint>();
    }

    public sealed class DashboardTrendPoint
    {
        [JsonPropertyName("recordedAt")]
        public DateTime RecordedAt { get; set; }

        [JsonPropertyName("waterLevel")]
        public double WaterLevel { get; set; }
    }
}
