using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private static List<CurrentControlDto> CurrentControls(Home home) => JsonSerializer.Deserialize<List<CurrentControlDto>>(home.CurrentControlsJson) ?? [];
    private static CurrentControlDto? EvControl(Home home, Device device) => EvChargingPolicy.Control(home, device);

    private static void ValidateEvSettings(Home home, HomeSettingsRequest request)
    {
        if (request.EvCharging is not { } settings) return;
        foreach (var (id, config) in settings)
        {
            var device = home.Devices.SingleOrDefault(d => d.Present && d.EntityId == id);
            if (device is null || !id.StartsWith("switch.") || SmartPanelCircuit.IsCircuit(id))
                throw new ArgumentException("Choose an EV charger switch for charging control.");
            if (config is not null && ((!CurrentControls(home).Any(c => c.EntityId == config.CurrentEntityId) && device.EvCurrentEntityId != config.CurrentEntityId) ||
                !double.IsFinite(config.WattsPerAmp) || config.WattsPerAmp < 100 || config.WattsPerAmp > 1000))
                throw new ArgumentException("Choose a discovered current control and a charger rating from 100 to 1000 watts per amp.");
        }
        var mapped = home.Devices.Select(d => settings.TryGetValue(d.EntityId, out var config) ? config?.CurrentEntityId : d.EvCurrentEntityId).Where(id => id is not null).ToList();
        if (mapped.Distinct().Count() != mapped.Count)
            throw new ArgumentException("Map each charging current control to only one EV.");
    }

    private static void ApplyEvSettings(Home home, Device device, HomeSettingsRequest request)
    {
        if (request.EvCharging?.TryGetValue(device.EntityId, out var config) != true) return;
        if (device.EvCurrentEntityId != config?.CurrentEntityId || device.EvWattsPerAmp != (config?.WattsPerAmp ?? 240))
        {
            foreach (var c in home.Commands.Where(c => c.EntityId == device.EntityId && c.Action == "SetCurrent" && !CommandPolicy.Terminal(c)))
            {
                c.Status = "Cancelled";
                c.Message = "EV charging configuration changed.";
            }
            AutoRestorePolicy.Clear(device);
        }
        device.EvCurrentEntityId = config?.CurrentEntityId;
        device.EvWattsPerAmp = config?.WattsPerAmp ?? 240;
    }

    private static double? EvRatedWatts(Home home, Device device)
    {
        var control = EvControl(home, device);
        return control?.Amps is { } amps && EvChargingPolicy.Valid(control, amps)
            ? amps * device.EvWattsPerAmp : null;
    }

    private static void UpdateEvRestoreEstimate(Home home, Device device)
    {
        if (device.EvCurrentEntityId is null) return;
        var rated = EvRatedWatts(home, device);
        // A tapered or paused car may draw much less than its configured limit.
        // Restoration must allow for it returning to that limit, including
        // current changes made directly in Home Assistant while it was off.
        device.LastOnWatts = rated is { } watts ? Math.Max(device.LastOnWatts ?? 0, watts) : null;
        if (device.RestoreEntry is { } entry)
            entry.EstimatedWatts = rated is { } restore ? Math.Max(entry.EstimatedWatts ?? 0, restore) : null;
    }

    private bool QueueEvReduction(Home home)
    {
        var eventPrefix = $"ev:{home.SmartPowerOffEventId}:";
        foreach (var device in home.Devices.OrderByDescending(d => d.PowerWatts).ThenBy(d => d.EntityId))
        {
            // A failed or unconfirmed control must not repeatedly block shutoff.
            if (home.Commands.Any(c => c.EntityId == device.EntityId && c.IdempotencyKey.StartsWith(eventPrefix) &&
                c.Status is "Failed" or "Expired" or "Cancelled")) continue;
            var amps = EvChargingPolicy.Reduction(home, device, EvControl(home, device), LimitWatts);
            if (amps is null) continue;
            home.Commands.Add(new DeviceCommand
            {
                HomeId = home.Id, EntityId = device.EntityId, RequestedBy = home.OwnerId,
                Action = "SetCurrent", CurrentControlEntityId = device.EvCurrentEntityId, CurrentAmps = amps,
                Automatic = true, IdempotencyKey = eventPrefix + Guid.NewGuid().ToString("N"),
                CreatedUtc = Now, NextAttemptUtc = Now, ExpiresUtc = Now.AddSeconds(30),
                Message = $"Reducing EV charging to {amps:0.##} A to keep household usage below 11 kW."
            });
            return true; // Replan from the next measured household load, one EV at a time.
        }
        return false;
    }

    public Task<CommandDto> EvCurrentAsync(string owner, Guid id, EvCurrentRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100 || !double.IsFinite(request.Amps))
            throw new ArgumentException("Enter a valid charging current and request key.");
        var prior = home.Commands.Where(c => c.IdempotencyKey == request.IdempotencyKey).ToList();
        if (prior.Count > 0)
        {
            if (prior.Count != 1 || prior[0].Automatic || prior[0].Action != "SetCurrent" || prior[0].EntityId != request.EntityId || prior[0].CurrentAmps != request.Amps)
                throw new ArgumentException("Idempotency key was used for a different command.");
            return Dto(prior[0]);
        }
        var device = home.Devices.SingleOrDefault(d => d.EntityId == request.EntityId && d.Present && d.Allowed && d.ShutoffLevel != ShutoffLevels.Never);
        var control = device is null ? null : EvControl(home, device);
        if (!Fresh(home, Now) || home.ProtectedTokens is null || device is null || device.State is not ("on" or "off") ||
            control?.Amps is null || !EvChargingPolicy.Valid(control, request.Amps))
            throw new ArgumentException("Choose an available, permitted EV and a current within its supported range and step.");
        foreach (var c in home.Commands) CommandPolicy.Advance(c, Now);
        if (home.Commands.Any(c => !CommandPolicy.Terminal(c)))
            throw new ArgumentException("Wait for pending device commands before changing charging current.");
        AutoRestorePolicy.ClearCurrentRestore(device);
        var command = new DeviceCommand
        {
            HomeId = id, EntityId = device.EntityId, RequestedBy = owner, Action = "SetCurrent",
            CurrentControlEntityId = control.EntityId, CurrentAmps = request.Amps,
            IdempotencyKey = request.IdempotencyKey, CreatedUtc = Now, NextAttemptUtc = Now,
            ExpiresUtc = Now.AddSeconds(30), Message = $"Setting EV charging current to {request.Amps:0.##} A."
        };
        home.Commands.Add(command);
        await repository.SaveAsync();
        return Dto(command);
    }));

    private async Task ProcessEvCommand(Home home, Device device, DeviceCommand command, ProviderTokens tokens)
    {
        void Pause(string message)
        {
            command.Status = "Cancelled";
            command.Message = message;
            if (command.IsRestoration && device.RestoreEntry is { } entry)
            {
                entry.Status = "waiting";
                entry.EligibleSinceUtc = null;
            }
        }
        var control = EvControl(home, device);
        if (control?.Amps is not { } current || control.EntityId != command.CurrentControlEntityId ||
            command.CurrentAmps is not { } target || !EvChargingPolicy.Valid(control, target) || device.State is not ("on" or "off"))
        {
            Pause("Charging current control is unavailable or its configuration changed.");
            return;
        }
        if (Math.Abs(current - target) < 0.000001)
        {
            command.Status = "Confirmed";
            command.Message = $"Home Assistant reports the EV current limit at {current:0.##} A.";
            if (command.Automatic && device.RestoreEntry is { TargetCurrentAmps: not null } entry)
            {
                entry.LastManagedCurrentAmps = current;
                entry.Status = "waiting";
                entry.EligibleSinceUtc = null;
                if (command.IsRestoration)
                {
                    home.LastRestoreUtc = Now;
                    // A partial increase goes to the front, but only if fresh capacity allows another step.
                    entry.AtFront = true;
                    if (current >= entry.TargetCurrentAmps) AutoRestorePolicy.ClearCurrentRestore(device);
                }
            }
            return;
        }
        if (command.Automatic && command.Attempts > 0 && command.PreviousCurrentAmps is { } previous &&
            Math.Abs(current - previous) > 0.000001)
        {
            Pause("Current changed outside this command; automatic current restoration stopped.");
            AutoRestorePolicy.ClearCurrentRestore(device);
            return;
        }
        if (command.Automatic)
        {
            var safeTarget = command.IsRestoration
                ? EvChargingPolicy.Increase(home, device, LimitWatts)
                : EvChargingPolicy.Reduction(home, device, control, LimitWatts);
            if (safeTarget is null || (command.IsRestoration && device.RestoreEntry is null))
            {
                Pause("Current adjustment paused: capacity, device state, or restoration target changed.");
                return;
            }
            command.CurrentAmps = target = Math.Min(target, safeTarget.Value);
        }
        CommandPolicy.Advance(command, Now);
        if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now) return;
        if (command.Attempts == 0) command.PreviousCurrentAmps = current;
        if (command.Automatic)
        {
            var entry = AutoRestorePolicy.Enqueue(device, Now);
            if (!command.IsRestoration)
            {
                // Persist the first pre-reduction limit before sending, including ambiguous network failures.
                // Further reductions must never replace the original compensation target.
                entry.TargetCurrentAmps ??= current;
                entry.Status = "waiting";
            }
            entry.LastManagedCurrentAmps = target;
            entry.EligibleSinceUtc = null;
            if (command.IsRestoration) home.LastRestoreUtc = Now;
        }
        command.Attempts++;
        command.Status = "AwaitingConfirmation";
        command.LeaseUntilUtc = Now.AddSeconds(10);
        await repository.SaveAsync();
        try
        {
            await provider.SetCurrentAsync(home.BaseUrl, tokens, control.EntityId, target);
        }
        catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Home Assistant rejected or could not receive the charging current command."); }
        catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Charging current command timed out."); }
    }
}
