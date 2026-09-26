using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private static CircuitPriorityDto? CircuitPriority(Home home, Device device, bool online = true)
    {
        if (!SmartPanelCircuit.IsCircuit(device.EntityId)) return null;
        var controls = JsonSerializer.Deserialize<List<CircuitPriorityDto>>(home.CircuitPrioritiesJson) ?? [];
        var control = controls.SingleOrDefault(c => c.EntityId == SmartPanelCircuit.PriorityEntityId(device.EntityId));
        return online || control is null ? control : control with { Priority = null };
    }

    public Task<CommandDto> CircuitPriorityAsync(string owner, Guid id, CircuitPriorityRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (string.IsNullOrWhiteSpace(request.EntityId) || !SmartPanelCircuit.IsCircuit(request.EntityId) ||
            !SmartPanelCircuit.ValidPriority(request.Priority) || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new ArgumentException("Choose a panel circuit and an available outage setting.");
        var prior = home.Commands.Where(c => c.IdempotencyKey == request.IdempotencyKey).ToList();
        if (prior.Count > 0)
        {
            if (prior.Count != 1 || !prior[0].ManualCircuit || prior[0].Action != "SetCircuitPriority" ||
                prior[0].EntityId != request.EntityId || prior[0].CircuitPriority != request.Priority)
                throw new ArgumentException("Idempotency key was used for a different command.");
            return Dto(prior[0]);
        }
        if (!Fresh(home, Now) || home.ProtectedTokens is null)
            throw new ArgumentException("Home is offline. Reconnect before changing outage settings.");
        var device = home.Devices.SingleOrDefault(d => d.EntityId == request.EntityId && d.Present);
        var control = device is null ? null : CircuitPriority(home, device);
        if (control?.Priority is null || !control.Options.Contains(request.Priority))
            throw new ArgumentException("This circuit's outage setting is unavailable in Home Assistant.");
        foreach (var existing in home.Commands) CommandPolicy.Advance(existing, Now);
        if (home.Commands.Any(c => c.EntityId == request.EntityId && !CommandPolicy.Terminal(c)))
            throw new ArgumentException("A command is already pending for this circuit.");
        var command = new DeviceCommand
        {
            HomeId = id, EntityId = request.EntityId, Action = "SetCircuitPriority", ManualCircuit = true,
            PriorityControlEntityId = control.EntityId, CircuitPriority = request.Priority,
            RequestedBy = owner, IdempotencyKey = request.IdempotencyKey, CreatedUtc = Now,
            ExpiresUtc = Now.AddMinutes(2), NextAttemptUtc = Now,
            Message = "Updating the circuit's grid outage setting."
        };
        home.Commands.Add(command);
        await repository.SaveAsync();
        return Dto(command);
    }));

    private async Task ProcessCircuitPriority(Home home, Device device, DeviceCommand command, ProviderTokens tokens)
    {
        var control = CircuitPriority(home, device);
        if (control is null || control.EntityId != command.PriorityControlEntityId ||
            !SmartPanelCircuit.ValidPriority(command.CircuitPriority) || !control.Options.Contains(command.CircuitPriority!))
        {
            command.Status = "Cancelled";
            command.Message = "The circuit's outage setting is no longer available.";
            return;
        }
        if (control.Priority == command.CircuitPriority)
        {
            command.Status = "Confirmed";
            command.Message = "Home Assistant confirmed the circuit's grid outage setting.";
            return;
        }
        CommandPolicy.Advance(command, Now);
        if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now) return;
        if (control.Priority is null)
        {
            CommandPolicy.Retry(command, Now, "The circuit's outage setting is unavailable.");
            return;
        }
        command.Attempts++;
        command.Status = "AwaitingConfirmation";
        command.LeaseUntilUtc = Now.AddSeconds(10);
        await repository.SaveAsync();
        try
        {
            await provider.SetCircuitPriorityAsync(home.BaseUrl, tokens, control.EntityId, command.CircuitPriority!);
        }
        catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Home Assistant rejected or could not receive the outage setting."); }
        catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Home Assistant outage setting timed out."); }
    }
}
