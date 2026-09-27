namespace BaseLayer.Domain.Entities;

// Durable inbox, notification, and savings ledger. EventId is supplied by the detector.
public sealed class UsageAnomaly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HomeId { get; set; }
    public string EventId { get; set; } = "";
    public string EntityId { get; set; } = "";
    public double UsualWatts { get; set; }
    public double ObservedWatts { get; set; }
    public string RecommendedAction { get; set; } = "";
    public DateTime DetectedUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public int PowerSensorRevision { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Waiting to review anomaly.";
    public Guid? CommandId { get; set; }
    public DateTime? ConfirmedUtc { get; set; }
    public DateTime? LastSavingsObservationUtc { get; set; }
    public bool SavingsClosed { get; set; }
    public double AvoidedWatts { get; set; }
    public double EstimatedSavedKwh { get; set; }
}
