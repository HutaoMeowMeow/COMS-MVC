using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
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
        private readonly ILookupService _lookups;
        private readonly AlertRetentionOptions _retention;

        public AlertService(ApplicationDbContext context,
            ILogger<AlertService> logger,
            IHubContext<MonitoringHub> hubContext,
            INotificationService notificationService,
            UserManager<ApplicationUser> userManager,
            ILookupService lookups,
            Microsoft.Extensions.Options.IOptions<AlertRetentionOptions>? retentionOptions = null)
        {
            _context = context;
            _logger = logger;
            _hubContext = hubContext;
            _notificationService = notificationService;
            _userManager = userManager;
            _lookups = lookups;
            _retention = retentionOptions?.Value ?? AlertRetentionOptions.Default;
        }

        /// <summary>
        /// Alert policy (intended behavior — change only with product approval):
        /// a single reading CAN produce multiple alerts because each AlertType
        /// is an independent condition (e.g. WaterLevelCritical + ObstructionDetected
        /// + SevereObstruction may all fire together). Within one type, repeats are
        /// suppressed: no new alert of the same type is created while an Active /
        /// Acknowledged / In Progress alert of that type detected in the last
        /// 30 minutes exists. Instead, the existing alert is refreshed with the
        /// latest reading/water level so its timestamps stay current without
        /// duplicating history. Sustained flooding therefore re-alerts at most
        /// once per type per 30 minutes (after the previous alert resolves or
        /// ages out), and a resolved condition that recurs creates a new alert.
        ///
        /// Concurrency: the canal row is locked (FOR UPDATE) inside a
        /// Serializable transaction so two concurrent readings for the same
        /// canal cannot both pass the "no existing alert" check and insert
        /// duplicate alerts of the same type.
        /// </summary>
        public async Task<List<ObstructionAlert>> CheckThresholdsAsync(SensorReading reading)
        {
            var alerts = new List<ObstructionAlert>();

            var canal = await _context.Canals.FindAsync(reading.CanalId);
            if (canal == null)
            {
                _logger.LogWarning("Canal not found for reading {ReadingId}", reading.SensorReadingId);
                return alerts;
            }

            using var tx = _context.Database.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                // Serialize per-canal alert generation so concurrent readings
                // for the same canal cannot both pass the "no existing alert"
                // check and insert duplicate alerts of the same type. PostgreSQL
                // supports row-level FOR UPDATE; other providers (e.g. Sqlite in
                // tests) run single-threaded here and skip the lock.
                await LockCanalRowAsync(reading.CanalId);

                var latestReadingTime = reading.RecordedAt.AddMinutes(-30);
                var existingActive = await _context.ObstructionAlerts
                    .Where(a => a.CanalId == canal.CanalId
                                && (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress")
                                && a.DetectedAt >= latestReadingTime)
                    .ToListAsync();

                if (reading.WaterLevel >= canal.CriticalWaterLevel)
                {
                    var existing = existingActive.FirstOrDefault(a => a.AlertType == "WaterLevelCritical");
                    if (existing is null)
                    {
                        alerts.Add(CreateAlert(canal, "WaterLevelCritical", "Critical",
                            "Critical Water Level",
                            $"Water level ({reading.WaterLevel:F2}m) has exceeded the critical threshold ({canal.CriticalWaterLevel:F2}m). Immediate inspection recommended.",
                            "Possible Blockage", reading.WaterLevel, reading));
                    }
                    else
                    {
                        RefreshAlert(existing, reading);
                        alerts.Add(existing);
                    }
                }
                else if (reading.WaterLevel >= canal.WarningWaterLevel)
                {
                    var existing = existingActive.FirstOrDefault(a => a.AlertType == "WaterLevelWarning");
                    if (existing is null)
                    {
                        alerts.Add(CreateAlert(canal, "WaterLevelWarning", DetermineWarningSeverity(reading, canal, existingActive),
                            "Elevated Water Level",
                            $"Water level ({reading.WaterLevel:F2}m) has exceeded the warning threshold ({canal.WarningWaterLevel:F2}m). Monitor conditions closely.",
                            "Possible Blockage", reading.WaterLevel, reading));
                    }
                    else
                    {
                        RefreshAlert(existing, reading);
                        alerts.Add(existing);
                    }
                }

                if (reading.DebrisLevel >= 80 && reading.WaterLevel >= canal.WarningWaterLevel)
                {
                    var existing = existingActive.FirstOrDefault(a => a.AlertType == "ObstructionDetected");
                    if (existing is null)
                    {
                        alerts.Add(CreateAlert(canal, "ObstructionDetected", "High",
                            "Possible Canal Obstruction",
                            $"High debris level ({reading.DebrisLevel:F1}%) combined with elevated water level indicates a possible obstruction.",
                            "Debris / Garbage Blockage", reading.WaterLevel, reading));
                    }
                    else
                    {
                        RefreshAlert(existing, reading);
                        alerts.Add(existing);
                    }
                }

                if (reading.DebrisLevel >= 90)
                {
                    var existing = existingActive.FirstOrDefault(a => a.AlertType == "SevereObstruction");
                    if (existing is null)
                    {
                        alerts.Add(CreateAlert(canal, "SevereObstruction", "Critical",
                            "Severe Obstruction Detected",
                            $"Severe debris accumulation ({reading.DebrisLevel:F1}%) detected. Canal may be blocked.",
                            "Debris / Garbage Blockage", reading.WaterLevel, reading));
                    }
                    else
                    {
                        RefreshAlert(existing, reading);
                        alerts.Add(existing);
                    }
                }

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            // Notify staff after the transaction commits so subscribers observe
            // committed data. Best-effort: a notification failure never rolls
            // back the already-committed alert.
            foreach (var alert in alerts)
            {
                try { await NotifyAlertAsync(alert); }
                catch (Exception ex) { _logger.LogWarning(ex, "Alert notification failed for alert {AlertId}", alert.ObstructionAlertId); }
            }

            return alerts;
        }

        private ObstructionAlert CreateAlert(Canal canal, string alertType, string severity, string title,
            string description, string obstructionType, double waterLevel, SensorReading reading)
        {
            var alert = new ObstructionAlert
            {
                CanalId = canal.CanalId,
                AlertType = alertType,
                Severity = severity,
                Title = title,
                Description = description,
                ObstructionType = obstructionType,
                WaterLevel = waterLevel,
                SensorReadingId = reading.SensorReadingId,
                DetectedAt = DateTime.UtcNow,
                Status = "Active"
            };
            _context.ObstructionAlerts.Add(alert);
            return alert;
        }

        /// <summary>
        /// Refreshes an existing active alert with the latest reading without
        /// creating a duplicate. DetectedAt/status history is preserved; only
        /// the water level and source reading are brought current.
        /// </summary>
        private static void RefreshAlert(ObstructionAlert existing, SensorReading reading)
        {
            existing.WaterLevel = reading.WaterLevel;
            existing.SensorReadingId = reading.SensorReadingId;
            existing.Description = existing.Description.Replace(
                $"Water level ({existing.WaterLevel:F2}m)", $"Water level ({reading.WaterLevel:F2}m)");
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

        /// <summary>
        /// Locks the canal row within the current transaction so concurrent
        /// alert generation for the same canal is serialized. Only PostgreSQL
        // supports FOR UPDATE; other providers are single-threaded in tests and
        /// skip the lock without failing.
        /// </summary>
        private async Task LockCanalRowAsync(int canalId)
        {
            var connection = _context.Database.GetDbConnection();
            if (connection is NpgsqlConnection)
            {
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Canals\" WHERE \"CanalId\" = {0} FOR UPDATE", canalId);
            }
        }

        private async Task NotifyAlertAsync(ObstructionAlert alert)
        {
            _logger.LogInformation("Alert created: {Title} for canal {CanalId}", alert.Title, alert.CanalId);

            // Staff-only broadcast: alert details mirror the staff-only
            // Alerts pages, so Residents must not receive them.
            await _hubContext.Clients.Groups(Hubs.MonitoringHub.StaffGroups).SendAsync("AlertCreated", new
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

            // Dataless refresh ping only (per-user endpoints enforce
            // ownership); safe to broadcast to all connected clients.
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

            await _hubContext.Clients.Groups(Hubs.MonitoringHub.StaffGroups).SendAsync("AlertUpdated", new
            {
                alert.ObstructionAlertId,
                alert.Status
            });

            return true;
        }

        public async Task<bool> UpdateStatusAsync(int alertId, string status, int userId, string? notes = null)
        {
            if (alertId <= 0 || string.IsNullOrWhiteSpace(status)
                || !await _lookups.IsValidAsync(LookupService.Categories.AlertStatus, status))
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

            await _hubContext.Clients.Groups(Hubs.MonitoringHub.StaffGroups).SendAsync("AlertUpdated", new
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

        /// <summary>
        /// Retention cutoff (UTC) shared with the AlertRetentionService sweep:
        /// alerts detected before this instant are expired everywhere.
        /// </summary>
        private DateTime GetRetentionCutoff() => _retention.CutoffUtc;

        public async Task<List<ObstructionAlert>> GetActiveAlertsAsync(int? canalId = null)
        {
            var cutoff = GetRetentionCutoff();
            var query = _context.ObstructionAlerts
                .Include(a => a.Canal)
                .Include(a => a.SensorReading)
                .Where(a => (a.Status == "Active" || a.Status == "Acknowledged" || a.Status == "In Progress")
                            && a.DetectedAt >= cutoff);

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
