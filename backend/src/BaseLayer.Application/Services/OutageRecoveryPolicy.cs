using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed class OutageCircuit
{
    public List<OutageDevice> Devices { get; set; } = [];
    public string EntityId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool WasOn { get; set; }
    public double? EstimatedWatts { get; set; }
    public string Status { get; set; } = "waiting";
    public Guid? CommandId { get; set; }
}

public sealed class OutageDevice
{
    public string EntityId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TargetState { get; set; } = "on";
    public string Status { get; set; } = "waiting";
    public Guid? CommandId { get; set; }
}

public sealed class OutageRecoveryState
{
    public DateTime? LastGridUtc { get; set; }
    public List<OutageCircuit> Baseline { get; set; } = [];
    public string? EventId { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? LastStepUtc { get; set; }
    public string Status { get; set; } = "monitoring";
    public double? MeasuredWatts { get; set; }
    public double? ReservedWatts { get; set; }
    public List<OutageCircuit> Circuits { get; set; } = [];
}

public static class OutageRecoveryPolicy
{
    public static bool IsRestorableDevice(string id) => !SmartPanelCircuit.IsCircuit(id) &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^(switch|light|fan|climate)\.[a-z0-9_]+$");
    public static bool IsRunningState(string id, string value) => id.StartsWith("climate.", StringComparison.Ordinal)
        ? value is "heat" or "cool" or "heat_cool" or "auto" or "dry" or "fan_only" : value == "on";
    private static bool Mapped(ProviderSnapshot snapshot, string circuit, string device) =>
        snapshot.CircuitDevices?.TryGetValue(circuit, out var members) == true && members.Contains(device) &&
        snapshot.CircuitDevices.Count(x => x.Value.Contains(device)) == 1;
    // Demo timing only: real installations need longer, device-specific startup
    // and measurement-settling delays. Five seconds keeps this demonstration short.
    public static readonly TimeSpan StepDelay = TimeSpan.FromSeconds(5);
    public const double BudgetWatts = 11_000; // Admit up to the full 11 kW inverter limit.

    public static OutageRecoveryState Read(Home home) =>
        JsonSerializer.Deserialize<OutageRecoveryState>(home.OutageRecoveryJson) ?? new();
    public static void Save(Home home, OutageRecoveryState state) => home.OutageRecoveryJson = JsonSerializer.Serialize(state);
    private static bool Valid(double? watts) => watts is { } w && double.IsFinite(w) && w >= 0;

    public static void Pause(Home home, DateTime now)
    {
        var state = Read(home);
        state.Status = "paused";
        state.LastStepUtc = now;
        Save(home, state);
    }

    public static OutageRecoveryStatusDto Status(Home home, DateTime now)
    {
        var state = Read(home);
        var fresh = !home.Revoked && home.LastSeenUtc >= now.AddSeconds(-20);
        return new(fresh ? state.Status : "paused", state.EventId, state.StartedUtc,
            state.LastStepUtc + StepDelay, BudgetWatts, fresh ? state.MeasuredWatts : null,
            fresh ? state.ReservedWatts : null,
            state.Circuits.Select(c => new OutageCircuitDto(c.EntityId, c.Name, c.EstimatedWatts, c.Status,
                c.Devices.Select(d => new OutageDeviceDto(d.EntityId, d.Name, d.TargetState, d.Status)).ToList())).ToList());
    }

    public static void Hold(Home home, string entityId)
    {
        var state = Read(home);
        var circuit = state.Circuits.Find(c => c.EntityId == entityId || c.Devices.Any(d => d.EntityId == entityId));
        if (circuit is null) return;
        var affected = circuit.EntityId == entityId ? circuit.Devices : circuit.Devices.Where(d => d.EntityId == entityId).ToList();
        if (circuit.EntityId == entityId) circuit.Status = "held";
        foreach (var device in affected) device.Status = "held";
        foreach (var command in home.Commands.Where(c => (c.EntityId == entityId || affected.Any(d => d.EntityId == c.EntityId)) && c.OutageEventId == state.EventId && c.OutageEventId != null && !CommandPolicy.Terminal(c)))
        {
            command.Status = "Cancelled";
            command.Message = "Outage restoration overridden by the user.";
        }
        Save(home, state);
    }

    // Called only after a successful HA snapshot. No power writes occur here.
    public static DeviceCommand? Update(Home home, ProviderSnapshot snapshot, DateTime now)
    {
        var state = Read(home);
        var mode = PowerSupplyDetection.Current(home, now).State;
        var circuits = home.Devices.Where(d => d.Present && SmartPanelCircuit.IsCircuit(d.EntityId)).ToList();
        if (mode == "grid")
        {
            foreach (var command in home.Commands.Where(c => c.OutageEventId != null && !CommandPolicy.Terminal(c)))
            {
                command.Status = "Cancelled";
                command.Message = "Grid supply restored; battery rollout ended.";
            }
            var old = state.Baseline.ToDictionary(c => c.EntityId);
            state = new OutageRecoveryState { LastGridUtc = now, Baseline = circuits.Select(d =>
            {
                var previous = old.GetValueOrDefault(d.EntityId)?.EstimatedWatts;
                double? configured = snapshot.CircuitRestoreEstimates?.TryGetValue(d.EntityId, out var watts) == true ? watts : null;
                var observed = d.State == "on" && Valid(d.PowerWatts) && d.PowerWatts > 0 ? d.PowerWatts : null;
                var estimates = new[] { previous, configured, observed }.Where(Valid).Select(w => w!.Value).ToList();
                return new OutageCircuit { EntityId = d.EntityId, Name = d.Name, WasOn = d.State == "on",
                    EstimatedWatts = estimates.Count > 0 ? estimates.Max() : null,
                    Devices = home.Devices.Where(a => a.Present && IsRestorableDevice(a.EntityId) &&
                        IsRunningState(a.EntityId, a.State) && Mapped(snapshot, d.EntityId, a.EntityId))
                        .Select(a => new OutageDevice { EntityId = a.EntityId, Name = a.Name, TargetState = a.State }).ToList() };
            }).ToList() };
            Save(home, state);
            return null;
        }
        if (mode != "battery")
        {
            state.Status = "paused";
            state.LastStepUtc = now;
            Save(home, state);
            return null;
        }
        if (state.EventId is null)
        {
            // Do not invent pre-outage intent when first connecting during an outage.
            if (state.LastGridUtc is null || state.LastGridUtc < now.AddSeconds(-20) || state.Baseline.Count == 0)
            {
                state.Status = "waitingForBaseline";
                Save(home, state);
                return null;
            }
            state.EventId = Guid.NewGuid().ToString("N");
            state.StartedUtc = state.LastStepUtc = now;
            foreach (var command in home.Commands.Where(c => c.OutageEventId == null && c.Automatic &&
                (c.Action == "On" || c.IsRestoration) && !CommandPolicy.Terminal(c)))
            {
                command.Status = "Cancelled";
                command.Message = "Circuit outage recovery now owns the battery restoration budget.";
            }
            state.Circuits = state.Baseline.Where(c => c.WasOn).Select(c => new OutageCircuit
            { EntityId = c.EntityId, Name = c.Name, WasOn = true, EstimatedWatts = c.EstimatedWatts,
                Devices = c.Devices.Select(d => new OutageDevice { EntityId = d.EntityId, Name = d.Name, TargetState = d.TargetState }).ToList() }).ToList();
        }
        state.Status = "restoring";
        state.MeasuredWatts = null;
        state.ReservedWatts = null;
        var waitingForConfirmation = false;
        foreach (var entry in state.Circuits)
        {
            if (entry.Status == "held") continue;
            var device = circuits.Find(d => d.EntityId == entry.EntityId);
            var command = home.Commands.Find(c => c.Id == entry.CommandId);
            if (command is not null && !CommandPolicy.Terminal(command))
            {
                if (command.Attempts > 0 && device?.State == "on")
                {
                    command.Status = "Confirmed";
                    command.Message = "Home Assistant confirmed circuit restoration during battery backup.";
                    entry.Status = "restored";
                    state.LastStepUtc = now;
                }
                else if (command.LeaseUntilUtc <= now || command.ExpiresUtc <= now)
                {
                    command.Status = "Failed";
                    command.Message = "Circuit restoration was not confirmed. Rollout stopped for review.";
                    entry.Status = "failed";
                }
                else waitingForConfirmation = true;
            }
            else if (command is not null && command.Status != "Confirmed") entry.Status = "failed";
            // A circuit switched off again after our confirmed restore stays off.
            // This avoids fighting a manual switch or the panel's own load shedding.
            if (entry.Status == "restored" && device?.State == "off") entry.Status = "held";
            if (entry.Status is "waiting" or "on") entry.Status = device?.State == "on" ? "on" : "waiting";
            foreach (var appliance in entry.Devices)
            {
                var applianceCommand = home.Commands.Find(c => c.Id == appliance.CommandId);
                if (entry.Status == "held")
                {
                    appliance.Status = "held";
                    if (applianceCommand is not null && !CommandPolicy.Terminal(applianceCommand))
                    {
                        applianceCommand.Status = "Cancelled";
                        applianceCommand.Message = "Parent circuit is off; device restoration stopped.";
                    }
                }
                if (appliance.Status == "held") continue;
                var current = home.Devices.Find(d => d.EntityId == appliance.EntityId && d.Present);
                if (applianceCommand is not null && !CommandPolicy.Terminal(applianceCommand))
                {
                    if (applianceCommand.Attempts > 0 && current?.State == appliance.TargetState)
                    {
                        applianceCommand.Status = "Confirmed";
                        applianceCommand.Message = "Home Assistant confirmed the device's pre-outage operating state.";
                        appliance.Status = "restored";
                        state.LastStepUtc = now;
                    }
                    else if (applianceCommand.LeaseUntilUtc <= now || applianceCommand.ExpiresUtc <= now)
                    {
                        applianceCommand.Status = "Failed";
                        applianceCommand.Message = "Device restart was not confirmed; rollout stopped for review.";
                        appliance.Status = "failed";
                    }
                    else waitingForConfirmation = true;
                }
                else if (applianceCommand is not null && applianceCommand.Status != "Confirmed") appliance.Status = "failed";
                if (appliance.Status == "restored" && current?.State == "off") appliance.Status = "held";
                if (appliance.Status == "on" && current?.State == "off" && device?.State == "on" &&
                    snapshot.BackupReady != false &&
                    (snapshot.CircuitSupplyStates?.TryGetValue(entry.EntityId, out var stillPowered) != true || stillPowered == true))
                    appliance.Status = "held"; // It stayed on through the outage, then was stopped.
                if (appliance.Status is "waiting" or "on")
                    appliance.Status = current?.State == appliance.TargetState ? "on" : "waiting";
            }
        }
        if (state.Circuits.Any(c => c.Status == "failed" || c.Devices.Any(d => d.Status == "failed"))) state.Status = "blocked";
        else if (waitingForConfirmation) state.Status = "confirming";
        // The virtual lab advertises readiness after its all-off sequence completes.
        // Real panels without that lab helper use confirmed battery mode and settling.
        else if (snapshot.BackupReady == false) { state.Status = "waitingForBackup"; state.LastStepUtc = now; }
        else if (circuits.Count == 0 || circuits.Any(d => d.State is not ("on" or "off") || !Valid(d.PowerWatts)) ||
            state.Baseline.Any(c => !circuits.Any(d => d.EntityId == c.EntityId)))
        {
            state.Status = "waitingForTelemetry";
            state.LastStepUtc = now;
        }
        else
        {
            // Include all measured circuits (even previously-off/manual additions),
            // plus unmetered household demand. Once a start is confirmed and its
            // cooldown has elapsed, measured demand replaces the startup estimate.
            state.MeasuredWatts = Math.Max(Valid(home.HouseholdWatts) ? home.HouseholdWatts!.Value : 0,
                circuits.Sum(d => d.PowerWatts!.Value));
            state.ReservedWatts = state.MeasuredWatts;
            // Cancellation cannot undo a dispatched request. Keep its capacity
            // reserved for this event while off in case a delayed write arrives.
            state.ReservedWatts += state.Circuits.Where(c => c.Status == "held" &&
                circuits.Any(d => d.EntityId == c.EntityId && d.State == "off") &&
                home.Commands.Any(command => command.Id == c.CommandId && command.Attempts > 0 && command.Status == "Cancelled"))
                .Sum(c => c.EstimatedWatts ?? 0);
            if (!Valid(state.MeasuredWatts) || !Valid(state.ReservedWatts))
            {
                state.Status = "waitingForTelemetry";
                state.MeasuredWatts = state.ReservedWatts = null;
                state.LastStepUtc = now;
                Save(home, state);
                return null;
            }
            // Restore frozen appliance states before advancing to another circuit.
            // Each start must still fit the parent's estimated remaining demand;
            // other settled circuits contribute only their actual measured usage.
            var deviceCircuit = state.Circuits.Where(c => c.Status is "on" or "restored")
                .OrderBy(c => c.EstimatedWatts).FirstOrDefault(c => c.Devices.Any(d => d.Status == "waiting"));
            if (deviceCircuit is not null)
            {
                var appliance = deviceCircuit.Devices.First(d => d.Status == "waiting");
                var current = home.Devices.Find(d => d.EntityId == appliance.EntityId && d.Present);
                if (!Mapped(snapshot, deviceCircuit.EntityId, appliance.EntityId)) state.Status = "waitingForDeviceMapping";
                else if (snapshot.CircuitSupplyStates?.TryGetValue(deviceCircuit.EntityId, out var powered) == true && powered != true)
                    state.Status = "waitingForCircuitPower";
                else if (current is null || current.State is "unknown" or "unavailable") state.Status = "waitingForDevices";
                else if (current.State != "off") appliance.Status = "held"; // Changed manually: retain the user's mode.
                else if (!Valid(deviceCircuit.EstimatedWatts)) state.Status = "waitingForEstimate";
                else if (state.ReservedWatts + Math.Max(0, deviceCircuit.EstimatedWatts!.Value -
                    circuits.Single(c => c.EntityId == deviceCircuit.EntityId).PowerWatts!.Value) > BudgetWatts)
                    state.Status = "capacityLimited";
                else if (state.LastStepUtc + StepDelay <= now && !home.Commands.Any(c => !CommandPolicy.Terminal(c)))
                {
                    var command = new DeviceCommand { HomeId = home.Id, EntityId = appliance.EntityId,
                        RequestedBy = home.OwnerId, Action = "On", Automatic = true, IsRestoration = true,
                        OutageEventId = state.EventId, IdempotencyKey = $"outage-device:{Guid.NewGuid():N}",
                        CreatedUtc = now, ExpiresUtc = now.AddSeconds(30), NextAttemptUtc = now,
                        Message = "Restoring the device's pre-outage state after its circuit power was confirmed." };
                    appliance.CommandId = command.Id;
                    appliance.Status = "restoring";
                    state.LastStepUtc = now;
                    state.Status = "restoringDevices";
                    home.Commands.Add(command);
                    Save(home, state);
                    return command;
                }
                Save(home, state);
                return null;
            }
            var pending = state.Circuits.Where(c => c.Status == "waiting").ToList();
            var next = pending.Where(c => Valid(c.EstimatedWatts))
                .OrderBy(c => c.EstimatedWatts).ThenBy(c => c.EntityId, StringComparer.Ordinal).FirstOrDefault();
            if (pending.Count == 0) state.Status = "complete";
            else if (next is null) state.Status = "waitingForEstimate";
            else if (state.ReservedWatts + next.EstimatedWatts > BudgetWatts) state.Status = "capacityLimited";
            else if (state.LastStepUtc + StepDelay <= now && !home.Commands.Any(c => !CommandPolicy.Terminal(c)))
            {
                var command = new DeviceCommand { HomeId = home.Id, EntityId = next.EntityId,
                    RequestedBy = home.OwnerId, Action = "On", Automatic = true, IsRestoration = true,
                    OutageEventId = state.EventId, EstimatedWatts = next.EstimatedWatts,
                    IdempotencyKey = $"outage:{state.EventId}:{Guid.NewGuid():N}", CreatedUtc = now,
                    ExpiresUtc = now.AddSeconds(30), NextAttemptUtc = now,
                    Message = "Restoring the lowest estimated circuit load within the battery budget." };
                next.CommandId = command.Id;
                next.Status = "restoring";
                state.LastStepUtc = now;
                home.Commands.Add(command);
                Save(home, state);
                return command;
            }
        }
        Save(home, state);
        return null;
    }
}
