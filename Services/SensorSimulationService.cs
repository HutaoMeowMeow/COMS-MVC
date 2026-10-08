using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using COMS_MVC.Hubs;

namespace COMS_MVC.Services
{
    public class SimulationOptions
    {
        public bool EnableSimulation { get; set; } = false;
        public int SimulationIntervalSeconds { get; set; } = 20;
    }

    public class SensorSimulationService : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<MonitoringHub> _hubContext;
        private readonly ILogger<SensorSimulationService> _logger;
        private readonly SimulationOptions _options;
        private Timer? _timer;

        public SensorSimulationService(
            IServiceProvider serviceProvider,
            IHubContext<MonitoringHub> hubContext,
            ILogger<SensorSimulationService> logger,
            IOptions<SimulationOptions> options)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _logger = logger;
            _options = options.Value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.EnableSimulation)
            {
                _logger.LogInformation("Sensor simulation service started. Interval: {Interval}s", _options.SimulationIntervalSeconds);
                _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(_options.SimulationIntervalSeconds));
            }
            else
            {
                _logger.LogInformation("Sensor simulation service is disabled.");
            }
            return Task.CompletedTask;
        }

        private async void DoWork(object? state)
        {
            using var scope = _serviceProvider.CreateScope();
            try
            {
                var sensorService = scope.ServiceProvider.GetRequiredService<ISensorService>();
                var alertService = scope.ServiceProvider.GetRequiredService<IAlertService>();
                var floodRiskService = scope.ServiceProvider.GetRequiredService<IFloodRiskService>();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var readings = await sensorService.GenerateSimulatedReadingsForAllAsync();

                foreach (var reading in readings)
                {
                    var alerts = await alertService.CheckThresholdsAsync(reading);

                    await _hubContext.Clients.All.SendAsync("SensorReadingAdded", new
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
                        CanalName = reading.Canal?.CanalName ?? string.Empty,
                        AlertCount = alerts.Count
                    });
                }

                var canals = await context.Canals.ToListAsync();
                foreach (var canal in canals)
                {
                    var assessment = await floodRiskService.CalculateRiskAsync(canal);

                    await _hubContext.Clients.All.SendAsync("RiskAssessmentUpdated", new
                    {
                        assessment.FloodRiskAssessmentId,
                        assessment.CanalId,
                        assessment.RiskScore,
                        assessment.RiskLevel,
                        assessment.AssessmentDate
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during sensor simulation cycle");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Sensor simulation service is stopping.");
            _timer?.Dispose();
            _timer = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
