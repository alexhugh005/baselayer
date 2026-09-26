namespace BaseLayer.Domain.Entities;

public enum RestoreStatus
{
    Waiting,
    Restoring,
    Failed
}

// One active restoration per device. Device metadata remains on the shared Device row.
public sealed class RestoreQueueEntry
{
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public DateTime QueuedUtc { get; set; }
    public double? EstimatedWatts { get; set; }
    public double? TargetCurrentAmps { get; set; }
    public double? LastManagedCurrentAmps { get; set; }
    public bool AtFront { get; set; }
    public bool PowerOn { get; set; }
    public DateTime? EligibleSinceUtc { get; set; }
    public RestoreStatus Status { get; private set; } = RestoreStatus.Waiting;

    public void ResumeWaiting()
    {
        Status = RestoreStatus.Waiting;
        ResetEligibility();
    }

    public void BeginRestoration()
    {
        if (Status != RestoreStatus.Waiting)
            throw new InvalidOperationException("Only a waiting queue entry can start restoring.");
        Status = RestoreStatus.Restoring;
        ResetEligibility();
    }

    public void FailRestoration()
    {
        if (Status != RestoreStatus.Restoring) return;
        Status = RestoreStatus.Failed;
        ResetEligibility();
    }

    public void ResetEligibility() => EligibleSinceUtc = null;

    public void ObserveCapacity(DateTime now, bool available)
    {
        if (Status != RestoreStatus.Waiting || !available) ResetEligibility();
        else EligibleSinceUtc ??= now;
    }
}
