using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private async Task ProcessOutageRecovery(Home home, ProviderSnapshot snapshot, ProviderTokens tokens)
    {
        var command = OutageRecoveryPolicy.Update(home, snapshot, Now);
        if (command is null) return;
        // Persist intent before dispatch: a restart or lost HTTP response must not
        // create another turn-on or spend the same reserved capacity twice.
        command.Attempts = 1;
        command.Status = "AwaitingConfirmation";
        command.LeaseUntilUtc = Now.AddSeconds(15);
        await repository.SaveAsync();
        try
        {
            var targetState = OutageRecoveryPolicy.Read(home).Circuits.SelectMany(c => c.Devices)
                .SingleOrDefault(d => d.CommandId == command.Id)?.TargetState;
            if (targetState is null)
                await provider.TurnOnAsync(home.BaseUrl, tokens, command.EntityId);
            else
                await provider.RestoreDeviceStateAsync(home.BaseUrl, tokens, command.EntityId, targetState);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Delivery may have succeeded. Resolve using the next observed relay
            // state; never issue the next circuit while this result is uncertain.
            command.Message = "Waiting for observed circuit state after an uncertain Home Assistant response.";
        }
    }
}
