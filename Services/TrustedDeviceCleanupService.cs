using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    public class TrustedDeviceCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TrustedDeviceCleanupService> _logger;
        private readonly TwoFactorOptions _options;

        public TrustedDeviceCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<TrustedDeviceCleanupService> logger,
            IOptions<TwoFactorOptions> options)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromMinutes(_options.CleanupIntervalMinutes);
            _logger.LogInformation(
                "TrustedDeviceCleanupService starting; sweeping every {Interval}.", interval);

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
                    await service.PurgeExpiredAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trusted-device cleanup failed; will retry next cycle.");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("TrustedDeviceCleanupService stopped.");
        }
    }
}
