using System.Diagnostics;
using COMS_MVC.Data;
using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Retention policy for transient flood-risk assessment history, bound from
    /// "FloodRiskRetention". The operational default is 5 hours. Only
    /// assessment-history rows older than the cutoff are eligible; the latest
    /// assessment for every canal is always preserved because dashboards, the
    /// Flood Risk page, and alert/risk calculations read that latest row.
    /// Sensor readings, obstruction alerts, notifications, and reports are
    /// never touched by this service.
    /// </summary>
    public class FloodRiskRetentionOptions
    {
        public const string SectionName = "FloodRiskRetention";

        /// <summary>Hours of assessment history to retain (clamped to at least 1).</summary>
        public int RetentionHours { get; set; } = 5;

        /// <summary>How often the cleanup sweep runs, in minutes (clamped to at least 1).</summary>
        public int CleanupIntervalMinutes { get; set; } = 30;

        /// <summary>Rows shown per page on the Flood Risk details history table.</summary>
        public int HistoryPageSize { get; set; } = 20;

        /// <summary>Maximum rows deleted per database round-trip.</summary>
        public int CleanupBatchSize { get; set; } = 2000;

        public TimeSpan Retention => TimeSpan.FromHours(Math.Max(1, RetentionHours));

        public int PageSize => Math.Clamp(HistoryPageSize, 5, 100);

        public int BatchSize => Math.Clamp(CleanupBatchSize, 100, 20000);

        /// <summary>UTC cutoff instant: assessments recorded before this moment are expired.</summary>
        public DateTime CutoffUtc => DateTime.UtcNow - Retention;

        public static FloodRiskRetentionOptions Default { get; } = new();
    }

    public sealed record FloodRiskCleanupResult(int Deleted, int PreservedLatest, TimeSpan Duration);

    /// <summary>
    /// Background hosted service that periodically deletes transient
    /// flood-risk assessment rows older than the configured cutoff. A fresh DI
    /// scope is used per sweep; the sweep is idempotent and safe when multiple
    /// application instances run concurrently (set-based DELETE, no in-memory
    /// full-table materialization).
    /// </summary>
    public class FloodRiskRetentionService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FloodRiskRetentionService> _logger;
        private readonly FloodRiskRetentionOptions _options;

        public FloodRiskRetentionService(
            IServiceScopeFactory scopeFactory,
            ILogger<FloodRiskRetentionService> logger,
            IOptions<FloodRiskRetentionOptions> options)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
            _logger.LogInformation(
                "FloodRiskRetentionService starting. Retaining assessment history younger than {Hours}h; sweeping every {Interval}.",
                _options.Retention.TotalHours, interval);

            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOldAssessmentsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Flood-risk assessment retention cleanup failed; will retry next cycle.");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("FloodRiskRetentionService stopped.");
        }

        /// <summary>
        /// Deletes expired assessment-history rows in bounded batches while
        /// preserving the newest assessment for every canal. Internal so the
        /// retention tests can invoke one sweep deterministically.
        /// </summary>
        internal async Task<FloodRiskCleanupResult> CleanupOldAssessmentsAsync(
            CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var cutoff = _options.CutoffUtc;
            var batchSize = _options.BatchSize;

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // One id per canal: the newest assessment is protected. This is a
            // small set (one row per canal), not the full history table.
            var latestIds = await context.FloodRiskAssessments
                .GroupBy(a => a.CanalId)
                .Select(g => g.OrderByDescending(a => a.AssessmentDate)
                    .Select(a => a.FloodRiskAssessmentId)
                    .First())
                .ToListAsync(cancellationToken);

            var protectedIds = latestIds.ToHashSet();
            var deleted = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var batchIds = await context.FloodRiskAssessments
                    .Where(a => a.AssessmentDate < cutoff
                                && !protectedIds.Contains(a.FloodRiskAssessmentId))
                    .OrderBy(a => a.AssessmentDate)
                    .Select(a => a.FloodRiskAssessmentId)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (batchIds.Count == 0)
                {
                    break;
                }

                var removed = await context.FloodRiskAssessments
                    .Where(a => batchIds.Contains(a.FloodRiskAssessmentId))
                    .ExecuteDeleteAsync(cancellationToken);
                deleted += removed;

                if (removed == 0)
                {
                    // Another instance already removed this batch; move on.
                    break;
                }
            }

            started.Stop();
            _logger.LogInformation(
                "Flood-risk retention: removed {Deleted} expired assessment(s) before {Cutoff:u} (retention {Hours}h, {Elapsed} ms). Latest assessments preserved: {Protected}.",
                deleted, cutoff, _options.Retention.TotalHours,
                started.ElapsedMilliseconds, protectedIds.Count);

            return new FloodRiskCleanupResult(deleted, protectedIds.Count, started.Elapsed);
        }
    }
}
