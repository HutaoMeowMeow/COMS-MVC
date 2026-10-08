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
            sensor.SensorCode = (sensor.SensorCode ?? string.Empty).Trim();
            sensor.SensorType = (sensor.SensorType ?? string.Empty).Trim();
            sensor.Status = string.IsNullOrWhiteSpace(sensor.Status) ? "Online" : sensor.Status.Trim();
            await ValidateSensorAsync(sensor, isNew: true);

            if (ModelState.IsValid)
            {
                try
                {
                    sensor.LastReading = DateTime.UtcNow;
                    sensor.LastCommunication = DateTime.UtcNow;
                    _context.Sensors.Add(sensor);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' created successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateException)
                {
                    ModelState.AddModelError(string.Empty, "Could not save the sensor. The sensor code may already exist.");
                }
                catch (Exception)
                {
                    ModelState.AddModelError(string.Empty, "An unexpected error occurred while creating the sensor. Please try again.");
                }
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

            sensor.SensorCode = (sensor.SensorCode ?? string.Empty).Trim();
            sensor.SensorType = (sensor.SensorType ?? string.Empty).Trim();
            sensor.Status = string.IsNullOrWhiteSpace(sensor.Status) ? "Online" : sensor.Status.Trim();
            await ValidateSensorAsync(sensor, isNew: false);

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(sensor);
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' updated successfully.";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!await SensorExists(sensor.SensorId))
                    {
                        return NotFound();
                    }
                    ModelState.AddModelError(string.Empty, "The sensor was modified by another user. Please refresh and try again.");
                }
                catch (DbUpdateException)
                {
                    ModelState.AddModelError(string.Empty, "Could not save the sensor. The sensor code may already exist.");
                }
                catch (Exception)
                {
                    ModelState.AddModelError(string.Empty, "An unexpected error occurred while saving the sensor. Please try again.");
                }
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

            try
            {
                _context.Sensors.Remove(sensor);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Sensor '{sensor.SensorCode}' deleted successfully.";
            }
            catch (DbUpdateException)
            {
                TempData["ErrorMessage"] = $"Cannot delete '{sensor.SensorCode}' because it still has readings linked to it.";
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the sensor. Please try again.";
            }
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

            if (sensor.Canal == null)
            {
                TempData["ErrorMessage"] = "Cannot generate a reading because this sensor is not linked to a canal.";
                return RedirectToAction(nameof(Details), new { id = sensor.SensorId });
            }

            try
            {
                var rng = Random.Shared;
                var canal = sensor.Canal;
                var span = canal.CriticalWaterLevel - canal.NormalWaterLevel;
                if (span <= 0) span = 1;
            var waterLevel = (float)(canal.NormalWaterLevel + rng.NextDouble() * span * 0.5);
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

                try
                {
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
                }
                catch
                {
                    // Real-time push is best-effort; the reading is already saved.
                }
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Failed to generate a test reading. Please try again.";
            }

            return RedirectToAction(nameof(Details), new { id = sensor.SensorId });
        }

        private async Task ValidateSensorAsync(Sensor sensor, bool isNew)
        {
            if (sensor.Latitude < 9.30 || sensor.Latitude > 11.45)
            {
                ModelState.AddModelError(nameof(sensor.Latitude), "Latitude must be inside Cebu (9.30 to 11.45).");
            }
            if (sensor.Longitude < 123.20 || sensor.Longitude > 124.60)
            {
                ModelState.AddModelError(nameof(sensor.Longitude), "Longitude must be inside Cebu (123.20 to 124.60).");
            }
            if (sensor.CanalId <= 0)
            {
                ModelState.AddModelError(nameof(sensor.CanalId), "Please select a canal.");
            }
            else if (!await _context.Canals.AnyAsync(c => c.CanalId == sensor.CanalId))
            {
                ModelState.AddModelError(nameof(sensor.CanalId), "Selected canal is invalid.");
            }

            var code = (sensor.SensorCode ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(code))
            {
                var duplicate = await _context.Sensors.AnyAsync(s => s.SensorCode == code && (isNew || s.SensorId != sensor.SensorId));
                if (duplicate)
                {
                    ModelState.AddModelError(nameof(sensor.SensorCode), "This sensor code is already in use.");
                }
            }
        }

        private async Task PopulateDropDownsAsync()
        {
            var canals = await _context.Canals
                .Select(c => new { c.CanalId, c.CanalName, c.Latitude, c.Longitude })
                .ToListAsync();

            ViewBag.CanalList = new SelectList(canals, "CanalId", "CanalName");
            ViewBag.CanalCoords = canals.ToDictionary(c => c.CanalId, c => new { lat = c.Latitude, lng = c.Longitude });

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
