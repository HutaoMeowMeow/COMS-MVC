using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class SensorsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SensorsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var sensors = await _context.Sensors
                .Include(s => s.Canal)
                .Include(s => s.SensorReadings.OrderByDescending(sr => sr.RecordedAt).Take(1))
                .ToListAsync();

            return View(sensors);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var sensor = await _context.Sensors
                .Include(s => s.Canal)
                .Include(s => s.SensorReadings.OrderByDescending(sr => sr.RecordedAt).Take(20))
                .FirstOrDefaultAsync(s => s.SensorId == id);

            if (sensor == null)
            {
                return NotFound();
            }

            return View(sensor);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create()
        {
            await PopulateDropDownsAsync();
            return View(new Sensor());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create(Sensor sensor)
        {
            if (ModelState.IsValid)
            {
                sensor.LastReading = DateTime.UtcNow;
                sensor.LastCommunication = DateTime.UtcNow;
                _context.Sensors.Add(sensor);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' created successfully.";
                return RedirectToAction(nameof(Index));
            }

            await PopulateDropDownsAsync();
            return View(sensor);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                return NotFound();
            }

            await PopulateDropDownsAsync();
            return View(sensor);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int id, Sensor sensor)
        {
            if (id != sensor.SensorId)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(sensor);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' updated successfully.";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await SensorExists(sensor.SensorId))
                    {
                        return NotFound();
                    }
                    ModelState.AddModelError(string.Empty, "The sensor was modified by another user. Please refresh and try again.");
                }
                return RedirectToAction(nameof(Index));
            }

            await PopulateDropDownsAsync();
            return View(sensor);
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var sensor = await _context.Sensors
                .Include(s => s.Canal)
                .FirstOrDefaultAsync(s => s.SensorId == id);

            if (sensor == null)
            {
                return NotFound();
            }

            return View(sensor);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id, IFormCollection collection)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                return NotFound();
            }

            _context.Sensors.Remove(sensor);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' deleted successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin,LGU,Barangay,Maintenance")]
        public async Task<IActionResult> GenerateTestReading(int id)
        {
            var sensor = await _context.Sensors
                .Include(s => s.Canal)
                .FirstOrDefaultAsync(s => s.SensorId == id);

            if (sensor == null)
            {
                return NotFound();
            }

            var rng = new Random();
            var canal = sensor.Canal;
            var waterLevel = (float)(canal.NormalWaterLevel + rng.NextDouble() * (canal.CriticalWaterLevel - canal.NormalWaterLevel) * 0.5);
            var flowRate = rng.NextDouble() * 5 + 1;
            var debrisLevel = rng.NextDouble() * 50;
            var turbidity = rng.NextDouble() * 20;
            var temperature = rng.NextDouble() * 10 + 20;

            var reading = new SensorReading
            {
                SensorId = sensor.SensorId,
                CanalId = canal.CanalId,
                WaterLevel = Math.Round(waterLevel, 2),
                FlowRate = Math.Round(flowRate, 2),
                DebrisLevel = Math.Round(debrisLevel, 1),
                Turbidity = Math.Round(turbidity, 1),
                Temperature = Math.Round(temperature, 1),
                RecordedAt = DateTime.UtcNow,
                IsSimulated = true
            };

            _context.SensorReadings.Add(reading);
            await _context.SaveChangesAsync();

            sensor.LastReading = reading.RecordedAt;
            sensor.LastCommunication = reading.RecordedAt;
            await _context.SaveChangesAsync();

            var alertService = HttpContext.RequestServices.GetRequiredService<IAlertService>();
            var hub = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.MonitoringHub>>();

            var alerts = await alertService.CheckThresholdsAsync(reading);

            if (alerts.Any())
            {
                TempData["AlertMessage"] = $"Test reading generated. {alerts.Count} alert(s) created.";
            }
            else
            {
                TempData["SuccessMessage"] = "Test reading generated successfully. No alerts triggered.";
            }

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

            return RedirectToAction(nameof(Details), new { id = sensor.SensorId });
        }

        private async Task PopulateDropDownsAsync()
        {
            var canals = await _context.Canals
                .Select(c => new { c.CanalId, c.CanalName })
                .ToListAsync();

            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName");

            var sensorTypes = new List<string> { "Water Level", "Flow Rate", "Debris", "Turbidity", "Temperature" };
            ViewBag.SensorTypeList = new SelectList(sensorTypes);

            var statuses = new List<string> { "Online", "Offline", "Maintenance" };
            ViewBag.StatusList = new SelectList(statuses);
        }

        private async Task<bool> SensorExists(int id)
        {
            return await _context.Sensors.AnyAsync(s => s.SensorId == id);
        }
    }
}
