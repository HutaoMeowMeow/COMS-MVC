using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using COMS_MVC.Hubs;

namespace COMS_MVC.Services
{
    public interface IAlertService
    {
        Task<List<ObstructionAlert>> CheckThresholdsAsync(SensorReading reading);
        Task<bool> AcknowledgeAsync(int alertId, int userId);
        Task<bool> UpdateStatusAsync(int alertId, string status, int userId, string? notes = null);
        Task<List<ObstructionAlert>> GetActiveAlertsAsync(int? canalId = null);
        Task<ObstructionAlert?> GetByIdAsync(int id);
    }

    public class AlertService : IAlertService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AlertService> _logger;
        private readonly IHubContext<MonitoringHub> _hubContext;
        private readonly INotificationService _notificationService;
        private readonly UserManager<ApplicationUser> _userManager;

        public AlertService(ApplicationDbContext context,
            ILogger<AlertService> logger,
            IHubContext<MonitoringHub> hubContext,
            INotificationService notificationService,
            UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _logger = logger;
            _hubContext = hubContext;
            _notificationService = notificationService;
            _userManager = userManager;
        }

        public async Task<List<ObstructionAlert>> CheckThresholdsAsync(SensorReading reading)
        {
            var alerts = new List<ObstructionAlert>();

            var canal = await _context.Canals.FindAsync(reading.CanalId);
            if (canal == null)
            {
                _logger.LogWarning("Canal not found for reading {ReadingId}", reading.SensorReadingId);
                return alerts;
            }

            var latestReadingTime = reading.RecordedAt.AddMinutes(-30);
            var existingActive = await _context.ObstructionAlerts
                .Where(a => a.CanalId == canal.CanalId
                            && (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress")
                            && a.DetectedAt >= latestReadingTime)
                .ToListAsync();

            if (reading.WaterLevel >= canal.CriticalWaterLevel)
            {
                if (!existingActive.Any(a => a.AlertType == "WaterLevelCritical"))
                {
                    var alert = new ObstructionAlert
                    {
                        CanalId = canal.CanalId,
                        AlertType = "WaterLevelCritical",
                        Severity = "Critical",
                        Title = "Critical Water Level",
                        Description = $"Water level ({reading.WaterLevel:F2}m) has exceeded the critical threshold ({canal.CriticalWaterLevel:F2}m). Immediate inspection recommended.",
                        ObstructionType = "Possible Blockage",
                        WaterLevel = reading.WaterLevel,
                        SensorReadingId = reading.SensorReadingId,
                        DetectedAt = DateTime.UtcNow,
                        Status = "Active"
                    };

                    await SaveAndNotifyAsync(alert);
                    alerts.Add(alert);
                }
            }
            else if (reading.WaterLevel >= canal.WarningWaterLevel)
            {
                if (!existingActive.Any(a => a.AlertType == "WaterLevelWarning"))
                {
                    var severity = DetermineWarningSeverity(reading, canal, existingActive);
                    var alert = new ObstructionAlert
                    {
                        CanalId = canal.CanalId,
                        AlertType = "WaterLevelWarning",
                        Severity = severity,
                        Title = "Elevated Water Level",
                        Description = $"Water level ({reading.WaterLevel:F2}m) has exceeded the warning threshold ({canal.WarningWaterLevel:F2}m). Monitor conditions closely.",
                        ObstructionType = "Possible Blockage",
                        WaterLevel = reading.WaterLevel,
                        SensorReadingId = reading.SensorReadingId,
                        DetectedAt = DateTime.UtcNow,
                        Status = "Active"
                    };

                    await SaveAndNotifyAsync(alert);
                    alerts.Add(alert);
                }
            }

            if (reading.DebrisLevel >= 80 && reading.WaterLevel >= canal.WarningWaterLevel)
            {
                if (!existingActive.Any(a => a.AlertType == "ObstructionDetected"))
                {
                    var alert = new ObstructionAlert
                    {
                        CanalId = canal.CanalId,
                        AlertType = "ObstructionDetected",
                        Severity = "High",
                        Title = "Possible Canal Obstruction",
                        Description = $"High debris level ({reading.DebrisLevel:F1}%) combined with elevated water level indicates a possible obstruction.",
                        ObstructionType = "Debris / Garbage Blockage",
                        WaterLevel = reading.WaterLevel,
                        SensorReadingId = reading.SensorReadingId,
                        DetectedAt = DateTime.UtcNow,
                        Status = "Active"
                    };

                    await SaveAndNotifyAsync(alert);
                    alerts.Add(alert);
                }
            }

            if (reading.DebrisLevel >= 90 && !existingActive.Any(a => a.AlertType == "SevereObstruction"))
            {
                var alert = new ObstructionAlert
                {
                    CanalId = canal.CanalId,
                    AlertType = "SevereObstruction",
                    Severity = "Critical",
                    Title = "Severe Obstruction Detected",
                    Description = $"Severe debris accumulation ({reading.DebrisLevel:F1}%) detected. Canal may be blocked.",
                    ObstructionType = "Debris / Garbage Blockage",
                    WaterLevel = reading.WaterLevel,
                    SensorReadingId = reading.SensorReadingId,
                    DetectedAt = DateTime.UtcNow,
                    Status = "Active"
                };

                await SaveAndNotifyAsync(alert);
                alerts.Add(alert);
            }

            return alerts;
        }

        private static string DetermineWarningSeverity(SensorReading reading, Canal canal, List<ObstructionAlert> existing)
        {
            if (reading.DebrisLevel >= 75)
                return "High";
            if (existing.Any(a => a.Severity == "High" || a.Severity == "Critical"))
                return "High";
            var ratio = reading.WaterLevel / canal.WarningWaterLevel;
            if (ratio >= 1.3)
                return "High";
            return "Medium";
        }

        private async Task SaveAndNotifyAsync(ObstructionAlert alert)
        {
            _context.ObstructionAlerts.Add(alert);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Alert created: {Title} for canal {CanalId}", alert.Title, alert.CanalId);

            await _hubContext.Clients.All.SendAsync("AlertCreated", new
            {
                alert.ObstructionAlertId,
                alert.CanalId,
                alert.Title,
                alert.Description,
                alert.Severity,
                alert.Status,
                alert.DetectedAt,
                WaterLevel = alert.WaterLevel
            });

            var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
            foreach (var user in adminUsers)
            {
                await _notificationService.CreateNotificationAsync(
                    user.Id,
                    $"[{alert.Severity}] {alert.Title}",
                    alert.Description,
                    "Alert",
                    alert.ObstructionAlertId,
                    null);
            }

            var maintenanceUsers = await _userManager.GetUsersInRoleAsync("Maintenance");
            foreach (var user in maintenanceUsers)
            {
                await _notificationService.CreateNotificationAsync(
                    user.Id,
                    $"Alert Assigned: {alert.Title}",
                    $"A {alert.Severity.ToLower()} obstruction alert requires your attention.",
                    "Maintenance",
                    alert.ObstructionAlertId,
                    null);
            }

            await _hubContext.Clients.All.SendAsync("NotificationUpdated");
        }

        public async Task<bool> AcknowledgeAsync(int alertId, int userId)
        {
            var alert = await _context.ObstructionAlerts.FindAsync(alertId);
            if (alert == null)
                return false;

            if (alert.Status != "Active" && alert.Status != "Acknowledged")
                return false;

            alert.Status = "Acknowledged";
            await _context.SaveChangesAsync();

            await _hubContext.Clients.All.SendAsync("AlertUpdated", new
            {
                alert.ObstructionAlertId,
                alert.Status
            });

            return true;
        }

        public async Task<bool> UpdateStatusAsync(int alertId, string status, int userId, string? notes = null)
        {
            var allowed = new[] { "Active", "Acknowledged", "In Progress", "Resolved", "Dismissed" };
            if (alertId <= 0 || string.IsNullOrWhiteSpace(status) || !allowed.Contains(status))
                return false;

            var alert = await _context.ObstructionAlerts.FindAsync(alertId);
            if (alert == null)
                return false;

            alert.Status = status;
            if (!string.IsNullOrEmpty(notes))
                alert.ResolutionNotes = notes;

            if (status == "Resolved" || status == "Dismissed")
            {
                alert.ResolvedAt = DateTime.UtcNow;
                alert.AssignedToUserId = userId;
            }
            else if (status == "In Progress")
            {
                alert.AssignedToUserId = userId;
            }

            await _context.SaveChangesAsync();

            await _hubContext.Clients.All.SendAsync("AlertUpdated", new
            {
                alert.ObstructionAlertId,
                alert.Status,
                alert.ResolvedAt
            });

            if (alert.AssignedToUserId.HasValue && alert.AssignedToUserId.Value != userId)
            {
                await _notificationService.CreateNotificationAsync(
                    alert.AssignedToUserId.Value,
                    "Alert Status Updated",
                    $"Alert \"{alert.Title}\" status updated to {status}.",
                    "Alert",
                    alert.ObstructionAlertId,
                    null);
            }

            return true;
        }

        public async Task<List<ObstructionAlert>> GetActiveAlertsAsync(int? canalId = null)
        {
            var query = _context.ObstructionAlerts
                .Include(a => a.Canal)
                .Include(a => a.SensorReading)
                .Where(a => a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress");

            if (canalId.HasValue)
                query = query.Where(a => a.CanalId == canalId.Value);

            return await query.OrderByDescending(a => a.DetectedAt).ToListAsync();
        }

        public async Task<ObstructionAlert?> GetByIdAsync(int id)
        {
            return await _context.ObstructionAlerts
                .Include(a => a.Canal)
                .Include(a => a.SensorReading)
                .Include(a => a.AssignedToUser)
                .FirstOrDefaultAsync(a => a.ObstructionAlertId == id);
        }
    }
}
