using Microsoft.Extensions.Logging;

namespace COMS_MVC.Services
{
    public interface ISensorService
    {
        Task<SensorReading> GenerateSimulatedReadingAsync(int canalId, Random? rng = null);
        Task<List<SensorReading>> GenerateSimulatedReadingsForAllAsync();
    }

    public class SensorService : ISensorService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<SensorService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public SensorService(ApplicationDbContext context, ILogger<SensorService> logger, IServiceProvider serviceProvider)
        {
            _context = context;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task<SensorReading> GenerateSimulatedReadingAsync(int canalId, Random? rng = null)
        {
            rng ??= new Random();

            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                throw new ArgumentException($"Canal with ID {canalId} not found.");
            }

            var sensors = await _context.Sensors
                .Where(s => s.CanalId == canalId && s.Status == "Online")
                .ToListAsync();

            if (sensors.Count == 0)
            {
                sensors = await _context.Sensors.Where(s => s.CanalId == canalId).ToListAsync();
                if (sensors.Count == 0)
                {
                    throw new InvalidOperationException($"No sensors found for canal {canal?.CanalName}.");
                }
            }

            var sensor = sensors[rng.Next(sensors.Count)];

            var normalLevel = canal.NormalWaterLevel;
            var warningLevel = canal.WarningWaterLevel;
            var criticalLevel = canal.CriticalWaterLevel;

            var waterLevel = GenerateWaterLevel(normalLevel, warningLevel, criticalLevel, rng);
            var flowRate = GenerateFlowRate(waterLevel, normalLevel, warningLevel, rng);
            var debrisLevel = GenerateDebrisLevel(rng);
            var turbidity = GenerateTurbidity(debrisLevel, rng);
            var temperature = GenerateTemperature(rng);

            var reading = new SensorReading
            {
                SensorId = sensor.SensorId,
                CanalId = canalId,
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

            _logger.LogInformation("Generated simulated reading for canal {CanalName}: WaterLevel={Level}m", canal.CanalName, reading.WaterLevel);

            return reading;
        }

        public async Task<List<SensorReading>> GenerateSimulatedReadingsForAllAsync()
        {
            var canals = await _context.Canals.ToListAsync();
            var readings = new List<SensorReading>();
            var rng = new Random();

            foreach (var canal in canals)
            {
                try
                {
                    var reading = await GenerateSimulatedReadingAsync(canal.CanalId, rng);
                    readings.Add(reading);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate reading for canal {CanalId}", canal.CanalId);
                }
            }

            return readings;
        }

        private static double GenerateWaterLevel(double normal, double warning, double critical, Random rng)
        {
            var baseRange = (critical - normal) * 0.4;
            var chance = rng.NextDouble();

            if (chance < 0.75)
            {
                return normal + rng.NextDouble() * baseRange;
            }
            else if (chance < 0.90)
            {
                return warning + rng.NextDouble() * (critical - warning) * 0.6;
            }
            else
            {
                return critical + rng.NextDouble() * (critical * 0.3);
            }
        }

        private static double GenerateFlowRate(double waterLevel, double normal, double warning, Random rng)
        {
            if (waterLevel >= warning)
            {
                return rng.NextDouble() * 2.0;
            }
            return rng.NextDouble() * 5.0 + 1.0;
        }

        private static double GenerateDebrisLevel(Random rng)
        {
            var chance = rng.NextDouble();
            if (chance < 0.60)
            {
                return rng.NextDouble() * 40;
            }
            else if (chance < 0.85)
            {
                return 40 + rng.NextDouble() * 30;
            }
            else
            {
                return 70 + rng.NextDouble() * 30;
            }
        }

        private static double GenerateTurbidity(double debrisLevel, Random rng)
        {
            return debrisLevel * 0.6 + rng.NextDouble() * 5;
        }

        private static double GenerateTemperature(Random rng)
        {
            var month = DateTime.Now.Month;
            var seasonalBase = (6.5 * Math.Sin((month - 3) * Math.PI / 6)) + 22;
            return seasonalBase + rng.NextDouble() * 4 - 2;
        }
    }
}
