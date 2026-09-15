using Microsoft.Extensions.Logging;

namespace COMS_MVC.Services
{
    public interface IFloodRiskService
    {
        Task<FloodRiskAssessment> CalculateRiskAsync(int canalId);
        Task<FloodRiskAssessment> CalculateRiskAsync(Canal canal);
        Task<List<FloodRiskAssessment>> GetLatestAssessmentsAsync();
        Task<FloodRiskAssessment?> GetLatestForCanalAsync(int canalId);
    }

    public class FloodRiskService : IFloodRiskService
    {
        private const string ModelVersion = "Rule-Based v1.0";

        private readonly ApplicationDbContext _context;
        private readonly ILogger<FloodRiskService> _logger;

        public FloodRiskService(ApplicationDbContext context, ILogger<FloodRiskService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<FloodRiskAssessment> CalculateRiskAsync(int canalId)
        {
            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                throw new ArgumentException($"Canal with ID {canalId} not found.");
            }

            return await CalculateRiskAsync(canal);
        }

        public async Task<FloodRiskAssessment> CalculateRiskAsync(Canal canal)
        {
            var latestReading = await _context.SensorReadings
                .Where(sr => sr.CanalId == canal.CanalId)
                .OrderByDescending(sr => sr.RecordedAt)
                .FirstOrDefaultAsync();

            var activeAlertsCount = await _context.ObstructionAlerts
                .Where(a => a.CanalId == canal.CanalId
                            && (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress"))
                .CountAsync();

            var pendingReports = await _context.CommunityReports
                .Where(r => r.CanalId == canal.CanalId
                            && (r.Status == "Pending" || r.Status == "Under Review" || r.Status == "Verified"))
                .CountAsync();

            var riskScore = ComputeRiskScore(canal, latestReading, activeAlertsCount, pendingReports);
            var riskLevel = GetRiskLevel(riskScore);
            var details = BuildAssessmentDetails(canal, latestReading, activeAlertsCount, pendingReports, riskScore, riskLevel);

            var assessment = new FloodRiskAssessment
            {
                CanalId = canal.CanalId,
                RiskScore = riskScore,
                RiskLevel = riskLevel,
                AssessmentDetails = details,
                ModelVersion = ModelVersion,
                AssessmentDate = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddHours(6)
            };

            _context.FloodRiskAssessments.Add(assessment);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Risk assessment for canal {CanalId}: Score={Score}, Level={Level}",
                canal.CanalId, riskScore, riskLevel);

            return assessment;
        }

        private static int ComputeRiskScore(Canal canal, SensorReading? reading, int activeAlerts, int pendingReports)
        {
            var score = 0;

            if (reading != null)
            {
                var waterRatio = canal.WarningWaterLevel > 0
                    ? reading.WaterLevel / canal.WarningWaterLevel
                    : 0;
                if (waterRatio > 1.0)
                {
                    score += (int)(Math.Min(waterRatio - 1.0, 1.0) * 30);
                }

                var criticalRatio = canal.CriticalWaterLevel > 0
                    ? reading.WaterLevel / canal.CriticalWaterLevel
                    : 0;
                if (criticalRatio > 1.0)
                {
                    score += (int)(Math.Min(criticalRatio - 1.0, 1.0) * 30);
                }

                if (reading.FlowRate < 1.0)
                {
                    score += 15;
                }

                score += (int)(reading.DebrisLevel / 100.0 * 20);
            }
            else
            {
                score += 10;
            }

            score += Math.Min(activeAlerts * 10, 20);

            score += Math.Min(pendingReports * 5, 10);

            return Math.Min(score, 100);
        }

        private static string GetRiskLevel(int score)
        {
            return score switch
            {
                <= 25 => "Low",
                <= 50 => "Moderate",
                <= 75 => "High",
                _ => "Critical"
            };
        }

        private static string BuildAssessmentDetails(Canal canal, SensorReading? reading, int activeAlerts, int pendingReports, int score, string level)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Flood Risk Assessment for {canal.CanalName}");
            sb.AppendLine($"Model: {ModelVersion}");
            sb.AppendLine();

            if (reading != null)
            {
                sb.AppendLine($"Latest Reading (at {reading.RecordedAt:yyyy-MM-dd HH:mm}):");
                sb.AppendLine($"  Water Level: {reading.WaterLevel:F2}m (Warning: {canal.WarningWaterLevel:F2}m, Critical: {canal.CriticalWaterLevel:F2}m)");
                sb.AppendLine($"  Flow Rate: {reading.FlowRate:F2} m³/s");
                sb.AppendLine($"  Debris Level: {reading.DebrisLevel:F1}%");
                sb.AppendLine($"  Turbidity: {reading.Turbidity:F1} NTU");
                sb.AppendLine($"  Temperature: {reading.Temperature:F1}°C");
                if (reading.IsSimulated)
                {
                    sb.AppendLine($"  Source: Simulated sensor data (not from physical IoT hardware)");
                }
            }
            else
            {
                sb.AppendLine("No recent sensor readings available. Risk elevated due to lack of monitoring data.");
            }

            sb.AppendLine();
            sb.AppendLine($"Active Alerts: {activeAlerts}");
            sb.AppendLine($"Pending Community Reports: {pendingReports}");
            sb.AppendLine();
            sb.AppendLine($"Risk Score: {score}/100");
            sb.AppendLine($"Risk Level: {level}");

            return sb.ToString();
        }

        public async Task<List<FloodRiskAssessment>> GetLatestAssessmentsAsync()
        {
            var assessments = new List<FloodRiskAssessment>();
            var canals = await _context.Canals.ToListAsync();

            foreach (var canal in canals)
            {
                var latest = await _context.FloodRiskAssessments
                    .Where(a => a.CanalId == canal.CanalId)
                    .OrderByDescending(a => a.AssessmentDate)
                    .FirstOrDefaultAsync();

                if (latest != null)
                {
                    assessments.Add(latest);
                }
            }

            return assessments;
        }

        public async Task<FloodRiskAssessment?> GetLatestForCanalAsync(int canalId)
        {
            return await _context.FloodRiskAssessments
                .Include(a => a.Canal)
                .Where(a => a.CanalId == canalId)
                .OrderByDescending(a => a.AssessmentDate)
                .FirstOrDefaultAsync();
        }

        public async Task<FloodRiskAssessment> RecalculateRiskForCanalAsync(int canalId)
        {
            var canal = await _context.Canals.FindAsync(canalId);
            if (canal == null)
            {
                throw new ArgumentException($"Canal with ID {canalId} not found.");
            }

            var existingValid = await _context.FloodRiskAssessments
                .Where(a => a.CanalId == canalId && a.ValidUntil > DateTime.UtcNow)
                .OrderByDescending(a => a.AssessmentDate)
                .FirstOrDefaultAsync();

            var assessment = existingValid ?? await CalculateRiskAsync(canal);

            if (existingValid != null)
            {
                assessment = await CalculateRiskAsync(canal);
            }

            return assessment;
        }
    }
}
