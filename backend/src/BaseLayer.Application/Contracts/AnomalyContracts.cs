namespace BaseLayer.Application.Contracts;

public sealed record AnomalySavingsRequest(bool Enabled);
public sealed record EnqueueAnomalyRequest(string EventId, string EntityId, double UsualWatts, double ObservedWatts, string RecommendedAction, DateTime DetectedUtc);
public sealed record AnomalyDto(Guid Id, string EventId, string EntityId, double UsualWatts, double ObservedWatts, double DifferenceWatts, string RecommendedAction, DateTime DetectedUtc, string Status, string Message, Guid? CommandId, double EstimatedSavedKwh);
public sealed record AnomalySavingsDto(double EstimatedSavedKwh, int ConfirmedActions, double EstimatedRatePerKwh = 0.16, int AssumedUndetectedMinutes = Services.AnomalySavingsPolicy.AssumedUndetectedMinutes);

public sealed record DeviceCategoryDto(string Id, string Name, double? StandardWatts);
public sealed record DevicePowerSettings(string Category, double? StandardWatts = null);
