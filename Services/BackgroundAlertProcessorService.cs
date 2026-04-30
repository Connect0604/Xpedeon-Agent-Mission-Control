namespace XpedeonAgentMissionControl.Services;

public class BackgroundAlertProcessorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundAlertProcessorService> _logger;

    public BackgroundAlertProcessorService(IServiceProvider serviceProvider, ILogger<BackgroundAlertProcessorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundAlertProcessorService started");

        // Wait for app startup
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var alertService = scope.ServiceProvider.GetRequiredService<AlertNotificationService>();
                    var processed = await alertService.ProcessPendingAlertsAsync();

                    if (processed)
                        _logger.LogInformation("Processed pending cost alerts");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing alerts in background service");
            }

            // Check every 30 seconds
            await Task.Delay(30000, stoppingToken);
        }

        _logger.LogInformation("BackgroundAlertProcessorService stopped");
    }
}
