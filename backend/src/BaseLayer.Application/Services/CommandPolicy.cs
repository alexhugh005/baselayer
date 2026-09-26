using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public static class CommandPolicy
{
    public const int MaxAttempts = 4;
    public static bool Terminal(DeviceCommand c) => c.Status is "Confirmed" or "Failed" or "Expired" or "Cancelled";
    public static void Advance(DeviceCommand c, DateTime now)
    {
        if (Terminal(c))
            return;
        if (now >= c.ExpiresUtc)
        {
            c.Status = "Expired";
            c.Message = "Approval expired before confirmed shutoff.";
            return;
        }
        if (c.Status == "AwaitingConfirmation" && c.LeaseUntilUtc <= now)
            Retry(c, now, "No confirmation received.");
    }
    public static void Retry(DeviceCommand c, DateTime now, string message)
    {
        c.Message = message;
        c.AttemptToken = null;
        c.Status = c.Attempts >= MaxAttempts ? "Failed" : "Retrying";
        c.NextAttemptUtc = now.AddSeconds(5 * Math.Pow(2, Math.Max(0, c.Attempts - 1)));
    }
}
