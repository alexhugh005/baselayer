using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;
using BaseLayer.Application.Interfaces;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    // Recognize persisted warnings from versions that imposed an hourly action cooldown.
    private const string LegacyAnomalyCooldownMessage = "A device action is already pending or was attempted within the last hour. Review before acting again.";
    private const string AnomalyPendingMessage = "A device action is already pending. Waiting for it to finish before checking excess usage again.";

    private static bool AnomalyActionBlocked(Home home, string entityId) => home.Commands.Any(c =>
        c.EntityId == entityId && !CommandPolicy.Terminal(c));

    private static AnomalyDto AnomalyDto(UsageAnomaly a, DeviceCommand? command = null) => new(a.Id, a.EventId, a.EntityId, a.UsualWatts,
        a.ObservedWatts, a.ObservedWatts - a.UsualWatts, a.RecommendedAction, DateTime.SpecifyKind(a.DetectedUtc, DateTimeKind.Utc),
        command?.Status ?? a.Status, command?.Message ?? a.Message, a.CommandId, AnomalySavingsPolicy.EstimateKwh(a));

    public Task<HomeDto> AnomalySavingsAsync(string owner, Guid id, AnomalySavingsRequest request) =>
        operations.RunAsync(id, () => repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            if (home.Revoked) throw new ArgumentException("Connection revoked.");
            home.AnomalySavingsEnabled = request.Enabled;
            if (!request.Enabled)
                foreach (var command in home.Commands.Where(c => c.AnomalyId != null && c.Automatic && !CommandPolicy.Terminal(c)))
                {
                    command.Status = "Cancelled";
                    command.Message = "Anomaly savings disabled. Requests already sent cannot be undone.";
                    var anomaly = home.Anomalies.Single(a => a.Id == command.AnomalyId);
                    anomaly.Status = command.Status;
                    anomaly.Message = command.Message;
                }
            await repository.SaveAsync();
            return ToDto(home);
        }));

    public Task<AnomalyDto> EnqueueAnomalyAsync(string owner, Guid id, EnqueueAnomalyRequest request) =>
        operations.RunAsync(id, () => repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            if (home.Revoked) throw new ArgumentException("Connection revoked.");
            if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 100 ||
                string.IsNullOrWhiteSpace(request.EntityId) ||
                !double.IsFinite(request.UsualWatts) || request.UsualWatts is < 0 or > 1_000_000 ||
                !double.IsFinite(request.ObservedWatts) || request.ObservedWatts is < 0 or > 1_000_000 ||
                request.ObservedWatts == request.UsualWatts || request.RecommendedAction is not ("Off" or "Inspect") ||
                request.DetectedUtc.Kind != DateTimeKind.Utc || request.DetectedUtc > Now.AddSeconds(30))
                throw new ArgumentException("Supply an event ID, device, different nonnegative usual/observed watts, UTC detection time, and Off or Inspect recommendation.");
            var prior = home.Anomalies.SingleOrDefault(a => a.EventId == request.EventId);
            if (prior is not null)
            {
                if (prior.EntityId != request.EntityId || prior.UsualWatts != request.UsualWatts ||
                    prior.ObservedWatts != request.ObservedWatts || prior.RecommendedAction != request.RecommendedAction || prior.DetectedUtc != request.DetectedUtc)
                    throw new ArgumentException("Event ID was already used for a different anomaly.");
                return AnomalyDto(prior);
            }
            var device = home.Devices.SingleOrDefault(d => d.EntityId == request.EntityId && d.Present)
                ?? throw new ArgumentException("Choose a discovered device in this home.");
            if (home.Anomalies.Count(a => a.Status == "Queued") >= 1000)
                throw new ArgumentException("Anomaly queue is full; retry after pending events are processed.");
            var anomaly = new UsageAnomaly
            {
                HomeId = id, EventId = request.EventId, EntityId = request.EntityId,
                UsualWatts = request.UsualWatts, ObservedWatts = request.ObservedWatts,
                RecommendedAction = request.RecommendedAction, DetectedUtc = request.DetectedUtc,
                ReceivedUtc = Now, PowerSensorRevision = device.PowerSensorRevision
            };
            home.Anomalies.Add(anomaly);
            await repository.SaveAsync();
            return AnomalyDto(anomaly);
        }));

    private void DetectStandardPowerAnomalies(Home home)
    {
        foreach (var device in home.Devices.Where(d => d.Present))
        {
            if (device.State is "unknown" or "unavailable" ||
                DevicePowerStandards.StandardWatts(device) is not { } standard ||
                device.PowerWatts is not { } power || !double.IsFinite(power) || power < 0) continue;
            if (!DevicePowerStandards.Exceeds(power, standard))
            {
                device.StandardPowerAnomalyActive = false;
                continue;
            }
            // One event per continuous excess-usage episode. Normal readings rearm
            // detection immediately; missing telemetry and restarts do not replay it.
            var latest = home.Anomalies.Where(a => a.EntityId == device.EntityId).OrderByDescending(a => a.ReceivedUtc).FirstOrDefault();
            // Recheck a blocked standard event when its action guard clears, using a new
            // fresh observation. This also recovers warnings blocked by the former
            // cooldown on successful shutoffs; it never replays a stale queued event.
            var resumed = latest is { Status: "NotificationOnly", Message: LegacyAnomalyCooldownMessage or AnomalyPendingMessage } &&
                latest.EventId.StartsWith("standard:", StringComparison.Ordinal) &&
                latest.PowerSensorRevision == device.PowerSensorRevision && device.State == "on" &&
                AnomalyDeviceAllowed(home, device) && !AnomalyActionBlocked(home, device.EntityId);
            if (device.StandardPowerAnomalyActive && latest != null && !resumed) continue;
            device.StandardPowerAnomalyActive = true;
            if (home.Anomalies.Any(a => a.EntityId == device.EntityId && a.Status == "Queued")) continue;
            home.Anomalies.Add(new UsageAnomaly
            {
                HomeId = home.Id, EntityId = device.EntityId, EventId = $"standard:{Guid.NewGuid():N}",
                UsualWatts = standard, ObservedWatts = power, RecommendedAction = "Off",
                DetectedUtc = Now, ReceivedUtc = Now, PowerSensorRevision = device.PowerSensorRevision
            });
        }
    }

    private void AttachApprovedAnomaly(Home home, DeviceCommand command)
    {
        var device = home.Devices.Single(d => d.EntityId == command.EntityId);
        var standard = DevicePowerStandards.StandardWatts(device)!.Value;
        var anomaly = new UsageAnomaly
        {
            HomeId = home.Id, EntityId = device.EntityId, EventId = $"approved:{command.Id:N}",
            UsualWatts = standard, ObservedWatts = device.PowerWatts!.Value,
            AvoidedWatts = device.PowerWatts.Value - standard, RecommendedAction = "Off",
            DetectedUtc = Now, ReceivedUtc = Now, PowerSensorRevision = device.PowerSensorRevision,
            Status = "Pending", CommandId = command.Id,
            Message = "Anomaly detection: user approved shutoff; waiting for confirmation."
        };
        device.StandardPowerAnomalyActive = true;
        command.AnomalyId = anomaly.Id;
        command.Message = anomaly.Message;
        home.Anomalies.Add(anomaly);
        AutoRestorePolicy.Clear(device);
        device.SmartUsageHeld = true;
    }

    private static bool AnomalyDeviceAllowed(Home home, Device d) => home.AnomalySavingsEnabled && AnomalyControlAllowed(d);

    private static bool AnomalyControlAllowed(Device d) => d.Present && d.Allowed && d.ShutoffLevel is ShutoffLevels.Sometimes or ShutoffLevels.Anytime &&
        !d.EntityId.StartsWith("climate.", StringComparison.Ordinal) && !SmartPanelCircuit.IsCircuit(d.EntityId);

    // Called only after a fresh provider snapshot, under the same per-home operation gate as settings.
    private async Task ProcessAnomalies(Home home, ProviderTokens tokens)
    {
        foreach (var a in home.Anomalies.OrderBy(a => a.ReceivedUtc).ThenBy(a => a.Id))
        {
            var device = home.Devices.SingleOrDefault(d => d.EntityId == a.EntityId && d.Present);
            a.EstimatedSavedKwh = AnomalySavingsPolicy.EstimateKwh(a);
            if (a.Status == "Queued")
            {
                a.Status = "NotificationOnly";
                a.Message = "Recommendation only. Anomaly savings is off or this device does not allow automatic shutoff.";
                if (a.DetectedUtc < Now.AddMinutes(-5))
                    a.Message = "This anomaly is older than five minutes. Review current usage before acting.";
                else if (a.RecommendedAction != "Off" || !DevicePowerStandards.Exceeds(a.ObservedWatts, a.UsualWatts))
                    a.Message = "Review this unusual usage. Automatic shutoff requires at least 50% above standard usage and an Off recommendation.";
                else if (device is not null && AnomalyDeviceAllowed(home, device))
                {
                    if (device.State != "on" || device.PowerSensorRevision != a.PowerSensorRevision ||
                        device.PowerWatts is not { } power || !DevicePowerStandards.Exceeds(power, a.UsualWatts))
                        a.Message = "Current readings no longer confirm excess usage. Review the recommendation.";
                    else if (AnomalyActionBlocked(home, a.EntityId))
                        a.Message = AnomalyPendingMessage;
                    else
                    {
                        var command = new DeviceCommand
                        {
                            HomeId = home.Id, EntityId = a.EntityId, AnomalyId = a.Id, Automatic = true,
                            RequestedBy = home.OwnerId, IdempotencyKey = $"anomaly:{a.Id:N}",
                            CreatedUtc = Now, ExpiresUtc = Now.AddMinutes(2), NextAttemptUtc = Now,
                            Message = "Anomaly savings: excess usage detected; requesting shutoff."
                        };
                        a.CommandId = command.Id;
                        // Credit only excess, bounded by the freshly measured excess at dispatch.
                        a.AvoidedWatts = Math.Min(a.ObservedWatts, power) - a.UsualWatts;
                        home.Commands.Add(command);
                    }
                }
            }
            if (a.CommandId is not { } commandId) continue;
            var action = home.Commands.Single(c => c.Id == commandId);
            if (action.ExpiresUtc <= Now) CommandPolicy.Advance(action, Now);
            if (!CommandPolicy.Terminal(action))
            {
                if (device is null || !AnomalyControlAllowed(device) || (action.Automatic && !home.AnomalySavingsEnabled) || device.PowerSensorRevision != a.PowerSensorRevision)
                {
                    action.Status = "Cancelled";
                    action.Message = "Anomaly action stopped: device access, setting, or power mapping changed.";
                }
                else if (action.Attempts > 0 && device.State == "off")
                {
                    action.Status = "Confirmed";
                    action.Message = "Anomaly detection turned this device off. Review it before switching it back on.";
                    a.ConfirmedUtc = Now;
                    a.EstimatedSavedKwh = AnomalySavingsPolicy.EstimateKwh(a);
                }
                else if (device.State != "on" || device.PowerWatts is not { } current || !DevicePowerStandards.Exceeds(current, a.UsualWatts))
                {
                    action.Status = "Cancelled";
                    action.Message = "Anomaly action stopped: fresh readings no longer confirm excess usage.";
                }
                else
                {
                    CommandPolicy.Advance(action, Now);
                    if (CommandPolicy.Terminal(action) || action.Status == "AwaitingConfirmation" || action.NextAttemptUtc > Now)
                    {
                        a.Status = action.Status;
                        a.Message = action.Message ?? "Waiting for device confirmation.";
                        continue;
                    }
                    a.AvoidedWatts = Math.Min(a.AvoidedWatts, current - a.UsualWatts);
                    // An abnormal device must not enter the grid-risk automatic restoration queue.
                    AutoRestorePolicy.Clear(device);
                    device.SmartUsageHeld = true;
                    OutageRecoveryPolicy.Hold(home, device.EntityId);
                    action.Attempts++;
                    action.Status = "AwaitingConfirmation";
                    action.LeaseUntilUtc = Now.AddSeconds(10);
                    await repository.SaveAsync(); // Persist the attempt before network I/O.
                    try { await provider.TurnOffAsync(home.BaseUrl, tokens, device.EntityId); }
                    catch (HttpRequestException) { CommandPolicy.Retry(action, Now, "Anomaly shutoff could not be delivered. Review the recommendation."); }
                    catch (TaskCanceledException) { CommandPolicy.Retry(action, Now, "Anomaly shutoff timed out. Review the recommendation."); }
                }
            }
            a.Status = action.Status;
            a.Message = action.Message ?? "Review the anomaly recommendation.";
        }
        await repository.SaveAsync();
    }

}
