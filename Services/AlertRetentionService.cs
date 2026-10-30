using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using COMS_MVC.Data;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Retention policy for obstruction alerts, bound from the
    /// "AlertRetention" configuration section. Defaults keep 5 hours of live
    /// alert history. Every read path (Admin/Resident dashboards, Recent
    /// Alerts, flood-risk scoring, Alerts Management) filters alerts with the
    /// same window so counts and lists stay consistent with what the
    /// background cleanup actually deletes.
    /// </summary>
    public class AlertRetentionOptions
    {
        public const string SectionName = "AlertRetention";

        /// <summary>Hours of alert history to retain (clamped to at least 1).</summary>
        public int RetentionHours { get; set; } = 5;

        /// <summary>How often the cleanup sweep runs, in minutes (clamped to at least 1).</summary>
        public int CleanupIntervalMinutes { get; set; } = 5;

        public TimeSpan Retention => TimeSpan.FromHours(Math.Max(1, RetentionHours));

        /// <summary>
        /// UTC cutoff instant: alerts detected strictly before this moment are
        /// expired. Compared against <see cref="Models.ObstructionAlert.DetectedAt"/>
        /// (stored in UTC), never against page-open time.
        /// </summary>
        public DateTime CutoffUtc => DateTime.UtcNow - Retention;

        /// <summary>Fallback used when options were not injected (e.g. direct construction in tests).</summary>
        public static AlertRetentionOptions Default { get; } = new();
    }

    /// <summary>
    /// Background hosted service that periodically deletes obstruction alert
    /// records (and the in-app notifications that reference them) older than
    /// the retention window. Exactly one instance is registered as a hosted
    /// service in Program.cs, so cleanup never runs per HTTP request and a
    /// single sequential loop guarantees no overlapping sweeps. A fresh DI
    /// scope is created per sweep, so no scoped DbContext is ever captured by
    /// this singleton. Database failures are logged and retried on the next
    /// cycle — they never crash the application.
    /// </summary>
    public class AlertRetentionService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AlertRetentionService> _logger;
        private readonly AlertRetentionOptions _options;

        public AlertRetentionService(
            IServiceScopeFactory scopeFactory,
            ILogger<AlertRetentionService> logger,
            IOptions<AlertRetentionOptions> options)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
            _logger.LogInformation(
                "AlertRetentionService starting. Retaining alerts younger than {Hours}h; sweeping every {Interval}.",
                _options.Retention.TotalHours, interval);

            // First sweep shortly after startup so rows that expired while the
            // app was down are removed without waiting a full interval.
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOldAlertsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and keep the loop alive: next cycle retries.
                    _logger.LogError(ex, "Alert retention cleanup failed; will retry next cycle.");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("AlertRetentionService stopped.");
        }

        /// <summary>
        /// Deletes alerts whose <c>DetectedAt</c> (UTC) is older than the
        /// retention cutoff, plus the notifications that referenced them.
        /// Uses the indexed <c>DetectedAt</c> column and two set-based SQL
        /// statements (<c>ExecuteDeleteAsync</c>) so no rows are materialized
        /// in memory, no matter how many alerts have accumulated.
        /// Internal for isolated testing via InternalsVisibleTo.
        /// </summary>
        internal async Task CleanupOldAlertsAsync(CancellationToken cancellationToken)
        {
            var cutoff = _options.CutoffUtc;

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var expiredIds = await context.ObstructionAlerts
                .Where(a => a.DetectedAt < cutoff)
                .Select(a => a.ObstructionAlertId)
                .ToListAsync(cancellationToken);

            if (expiredIds.Count == 0)
            {
                return;
            }

            // Capture the linked notifications BEFORE deleting the alerts:
            // the FK from Notifications to ObstructionAlerts is ON DELETE SET
            // NULL, so the links would already be gone after the next step.
            var linkedNotificationIds = await context.Notifications
                .Where(n => n.RelatedAlertId.HasValue && expiredIds.Contains(n.RelatedAlertId.Value))
                .Select(n => n.NotificationId)
                .ToListAsync(cancellationToken);

            var deletedAlerts = await context.ObstructionAlerts
                .Where(a => expiredIds.Contains(a.ObstructionAlertId))
                .ExecuteDeleteAsync(cancellationToken);

            // Remove the stale notification entries so the bell history
            // matches the live alert window. Best-effort: failure here never
            // undoes the alert deletion above.
            var deletedNotifications = 0;
            if (linkedNotificationIds.Count > 0)
            {
                try
                {
                    deletedNotifications = await context.Notifications
                        .Where(n => linkedNotificationIds.Contains(n.NotificationId))
                        .ExecuteDeleteAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove notifications linked to expired alerts.");
                }
            }

            _logger.LogInformation(
                "Alert retention: removed {Alerts} alert(s) and {Notifications} related notification(s) detected before {Cutoff:u} (retention {Hours}h).",
                deletedAlerts, deletedNotifications, cutoff, _options.Retention.TotalHours);
        }
    }
}
