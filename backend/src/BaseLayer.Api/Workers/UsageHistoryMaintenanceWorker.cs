using BaseLayer.Application.Services;
namespace BaseLayer.Api.Workers;

public sealed class UsageHistoryMaintenanceWorker(IServiceScopeFactory scopes, ILogger<UsageHistoryMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<UsageHistoryMaintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Usage history aggregation or retention failed; unaggregated minute data is preserved."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
