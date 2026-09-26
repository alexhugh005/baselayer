namespace BaseLayer.Domain.Entities;

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
    public string Status { get; set; } = "waiting";
}
