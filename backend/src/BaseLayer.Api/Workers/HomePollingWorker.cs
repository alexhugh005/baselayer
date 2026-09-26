using BaseLayer.Application.Interfaces;
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
                    using var pollScope = scopes.CreateScope();
                    try
                    {
                        await pollScope.ServiceProvider.GetRequiredService<IPlatformService>().PollAsync(id);
                    }
                    catch (Exception exception) { logger.LogWarning("Home {HomeId} polling failed ({FailureType}).", id, exception.GetType().Name); }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
