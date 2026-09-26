using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public sealed class SmartPowerUsageService(IPlatformRepository repository, IBatteryService battery,
    HomeOperationGate operations, TimeProvider clock) : ISmartPowerUsageService
{
    private async Task<Home> Owned(string owner, Guid homeId)
    {
        var home = await repository.HomeAsync(homeId);
        return home is not null && home.OwnerId == owner ? home : throw new KeyNotFoundException("Home not found.");
    }
    public Task<SmartUsagePlan> PreviewAsync(string owner, Guid homeId, double? targetWatts = null, CancellationToken cancellationToken = default) =>
        operations.RunAsync(homeId, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var home = await Owned(owner, homeId);
            var reading = await battery.GetCurrentAsync(owner, homeId, cancellationToken);
            return SmartUsagePolicy.Plan(home, reading, clock.GetUtcNow(), targetWatts);
        });

    public Task<List<CommandDto>> ApplyAsync(string owner, Guid homeId, ApplySmartUsageRequest request, CancellationToken cancellationToken = default) =>
        operations.RunAsync(homeId, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var home = await Owned(owner, homeId);
            if (!double.IsFinite(request.TargetWatts) || request.TargetWatts < 0 || string.IsNullOrWhiteSpace(request.Revision) ||
                string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 64)
                throw new ArgumentException("Invalid Smart Usage confirmation.");
            var key = $"usage:{request.IdempotencyKey}";
            var prior = home.Commands.Where(c => c.IdempotencyKey == key).ToList();
            if (prior.Count > 0)
            {
                if (prior.Any(c => c.UsageRevision != request.Revision || c.UsageBudgetWatts != request.TargetWatts))
                    throw new ArgumentException("This confirmation key was used for another plan.");
                return prior.Select(Dto).ToList();
            }
            var reading = await battery.GetCurrentAsync(owner, homeId, cancellationToken);
            var plan = SmartUsagePolicy.Plan(home, reading, clock.GetUtcNow(), request.TargetWatts);
            if (plan.Revision != request.Revision || plan.TargetWatts != request.TargetWatts)
                throw new ArgumentException("Usage or device settings changed. Review a fresh plan before confirming.");
            if (!plan.CanApply) throw new ArgumentException(plan.BlockedReason);
            return await repository.TransactionAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var turningOn = plan.Changes.Where(c => c.Action == "On").Select(c => c.EntityId).ToHashSet();
                var turningOff = plan.Changes.Where(c => c.Action == "Off").Select(c => c.EntityId).ToHashSet();
                foreach (var device in home.Devices.Where(SmartUsagePolicy.Controllable))
                {
                    device.SmartUsageHeld = turningOff.Contains(device.EntityId) || device.State == "off" && !turningOn.Contains(device.EntityId);
                    if (turningOn.Contains(device.EntityId)) AutoRestorePolicy.Clear(device);
                }
                var now = clock.GetUtcNow().UtcDateTime;
                var commands = plan.Changes.Select(change => new DeviceCommand
                {
                    HomeId = homeId, EntityId = change.EntityId, Action = change.Action, RequestedBy = owner,
                    IdempotencyKey = key, EstimatedWatts = change.EstimatedWatts,
                    UsageBudgetWatts = plan.TargetWatts, UsageRevision = plan.Revision,
                    CreatedUtc = now, NextAttemptUtc = now, ExpiresUtc = now.AddMinutes(2),
                    Message = "Confirmed Smart Usage plan; waiting for Home Assistant."
                }).ToList();
                home.Commands.AddRange(commands);
                await repository.SaveAsync();
                return commands.Select(Dto).ToList();
            });
        });
    private static CommandDto Dto(DeviceCommand c) => new(c.Id, c.EntityId, c.Status, c.Attempts, c.CreatedUtc, c.Message, c.Automatic, c.Action);
}
