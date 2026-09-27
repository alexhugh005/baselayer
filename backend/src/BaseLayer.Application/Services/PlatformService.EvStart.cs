using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    // Called only after the requested charge limit and current are observed.
    private async Task<bool> EnsureEvStarted(Home home, Device device, DeviceCommand command, ProviderTokens tokens)
    {
        if (!command.StartCharge || device.State == "on") return true;
        if (command.ChargeStartSentUtc is null)
        {
            command.Status = "Pending";
            command.LeaseUntilUtc = null;
            command.NextAttemptUtc = Now;
        }
        CommandPolicy.Advance(command, Now);
        if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now) return false;
        command.Attempts++;
        command.ChargeStartSentUtc = Now;
        command.Status = "AwaitingConfirmation";
        command.LeaseUntilUtc = Now.AddSeconds(10);
        command.Message = "Starting the charger; waiting for Home Assistant confirmation.";
        await repository.SaveAsync();
        try { await provider.TurnOnAsync(home.BaseUrl, tokens, device.EntityId); }
        catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Could not start the charger."); }
        catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Starting the charger timed out."); }
        return false;
    }
}
