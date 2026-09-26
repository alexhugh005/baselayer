using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
namespace BaseLayer.Api.Workers;

public sealed class HomePollingWorker(IServiceScopeFactory scopes, ILogger<HomePollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = scopes.CreateScope())
            {
                var ids = await scope.ServiceProvider.GetRequiredService<IPlatformRepository>().ActiveHomeIdsAsync();
                foreach (var id in ids)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;
                    var polled = false;
                    using var pollScope = scopes.CreateScope();
                    try
                    {
                        await pollScope.ServiceProvider.GetRequiredService<IPlatformService>().PollAsync(id);
                        polled = true;
                    }
                    catch (Exception exception) { logger.LogWarning("Home {HomeId} polling failed ({FailureType}).", id, exception.GetType().Name); }
                    // A separate context keeps recording failures out of the command/control flow.
                    try
                    {
                        using var historyScope = scopes.CreateScope();
                        var history = historyScope.ServiceProvider.GetRequiredService<UsageHistoryService>();
                        if (polled) await history.RecordAsync(id);
                        else await history.BreakObservationAsync(id);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Usage history recording failed for home {HomeId}.", id);
                        try
                        {
                            using var recoveryScope = scopes.CreateScope();
                            await recoveryScope.ServiceProvider.GetRequiredService<UsageHistoryService>().BreakObservationAsync(id);
                        }
                        catch (Exception recoveryException)
                        {
                            logger.LogError(recoveryException, "Could not mark usage history gap for home {HomeId}.", id);
                        }
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
