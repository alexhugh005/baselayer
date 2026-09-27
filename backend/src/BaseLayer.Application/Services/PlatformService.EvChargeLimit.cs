using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private static ChargeLimitControlDto? EvChargeLimit(Home home, Device device, string? vehicleId = null)
    {
        var sensors = EvBatterySensors(home);
        var battery = vehicleId is null
            ? EvBattery(device) ?? EvBatteryDiscovery.Resolve(home.Devices, sensors, EvBattery).GetValueOrDefault(device.EntityId)
            : EvVehicles(home).SingleOrDefault(v => v.Id == vehicleId && v.ChargerEntityId == device.EntityId)?.Battery;
        return sensors.SingleOrDefault(s => s.EntityId == battery?.SensorEntityId)?.ChargeLimitControl;
    }

    private static bool ValidChargeLimit(ChargeLimitControlDto control, double percent) =>
        percent >= 1 && percent <= 100 && EvChargingPolicy.Valid(new(control.EntityId, control.Name, control.Percent, control.Min, control.Max, control.Step), percent);

    // Set and observe the vehicle's stopping point before applying the current.
    private async Task<bool> EnsureEvChargeLimit(Home home, Device device, DeviceCommand command, ProviderTokens tokens)
    {
        if (command.ChargeLimitPercent is not { } target) return true;
        var control = EvChargeLimit(home, device, command.EvVehicleId);
        if (control?.Percent is not { } current || control.EntityId != command.ChargeLimitControlEntityId || !ValidChargeLimit(control, target))
        {
            command.Status = "Cancelled";
            command.Message = "Charge limit control is unavailable or its battery pairing changed.";
            return false;
        }
        if (Math.Abs(current - target) < 0.000001)
        {
            if (command.PreviousCurrentAmps is null && command.ChargeStartSentUtc is null && command.Status is "AwaitingConfirmation" or "Retrying")
            {
                command.Status = "Pending";
                command.NextAttemptUtc = Now;
                command.LeaseUntilUtc = null;
            }
            return true;
        }
        CommandPolicy.Advance(command, Now);
        if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now) return false;
        command.Attempts++;
        command.Status = "AwaitingConfirmation";
        command.LeaseUntilUtc = Now.AddSeconds(10);
        command.Message = $"Setting vehicle charge limit to {target:0.##}%; waiting for confirmation before setting current.";
        await repository.SaveAsync();
        try { await provider.SetChargeLimitAsync(home.BaseUrl, tokens, control.EntityId, target); }
        catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Home Assistant rejected or could not receive the charge limit command."); }
        catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Charge limit command timed out."); }
        catch (NotSupportedException) { command.Status = "Failed"; command.Message = "This provider does not support setting a charge limit."; }
        return false;
    }
}
