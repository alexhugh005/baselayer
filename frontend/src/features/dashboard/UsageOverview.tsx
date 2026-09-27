import { AnomalySavingsWidget } from "./AnomalySavings";
import { isDeviceRunning } from "../../lib/deviceState";
import type { Home } from "../../lib/types";
import { TriangleAlert } from "lucide-react";
import { hasUsageReading } from "./UsageAlerts";
import { formatPower } from "./power";
import { LoadingStatus } from "../../components/ui/LoadingStatus";
export function UsageOverview({ home }: { home: Home }) {
  const devices = home.devices.filter((device) => !device.isCircuit);
  const waitingForUsage =
    home.connected &&
    !home.revoked &&
    home.householdWatts === null &&
    (home.powerSource === "deviceSum"
      ? home.devices.some((device) => device.powerSensorId)
      : !!home.householdPowerSensorId);
  const usage = home.householdWatts;
  // The visual warning reflects usage, independently of grid-risk enforcement.
  const overLimit = hasUsageReading(home) && usage! > home.limitWatts;
  const usagePercent =
    usage === null ||
    !Number.isFinite(usage) ||
    !Number.isFinite(home.limitWatts) ||
    home.limitWatts <= 0
      ? 0
      : Math.max(0, (usage / home.limitWatts) * 100);
  // Reserve room for the bar to pass a stable limit endpoint. Very large
  // overages rescale the track so the full reading still fits inside the card.
  const trackPercent = overLimit ? Math.min(80, 10000 / usagePercent) : 80;
  const percent = Math.min(100, usagePercent);
  return (
    <section className="overview-grid">
      <div className={`card usage-card${overLimit ? " usage-card-over" : ""}`}>
        <div className="usage-card-heading">
          <div className="eyebrow">
            {home.powerSource === "deviceSum"
              ? "MONITORED USAGE"
              : "HOUSEHOLD USAGE"}
          </div>
          {overLimit && (
            <span className="usage-over-badge">
              <TriangleAlert size={14} aria-hidden="true" /> Over limit
            </span>
          )}
        </div>
        <div className="usage-value">
          {waitingForUsage ? (
            <LoadingStatus>Loading usage…</LoadingStatus>
          ) : usage === null ? (
            <span>Usage unavailable</span>
          ) : (
            <>
              {(usage / 1000).toFixed(2)} <span>kW</span>
            </>
          )}
        </div>
        <div
          className="meter usage-meter"
          style={{ width: `${trackPercent}%` }}
          role={usage === null ? "status" : "meter"}
          aria-label="Household power"
          aria-valuemin={0}
          aria-valuemax={home.limitWatts}
          aria-valuenow={
            usage === null ? undefined : Math.min(usage, home.limitWatts)
          }
          aria-valuetext={
            usage === null
              ? "Unknown power usage"
              : overLimit
                ? `${formatPower(usage)}, ${formatPower(usage - home.limitWatts)} over the ${formatPower(home.limitWatts)} limit`
                : undefined
          }
        >
          <div style={{ width: `${percent}%` }} />
          <span
            className="usage-meter-overflow"
            aria-hidden="true"
            style={{ width: `${overLimit ? usagePercent : percent}%` }}
          />
        </div>
        <div className="meter-label">
          <span className={overLimit ? "usage-over-amount" : undefined}>
            {usage === null
              ? "Waiting for a measurement"
              : overLimit
                ? `${formatPower(usage - home.limitWatts)} over`
                : home.powerSource === "deviceSum"
                  ? "Device readings"
                  : "Home Assistant"}
          </span>
          <strong>
            {formatPower(home.limitWatts)}{" "}
            {!overLimit && home.gridOutageRisk === "low"
              ? "limit inactive"
              : "limit"}
          </strong>
        </div>
      </div>
      <div className="card metric-card">
        <span className="eyebrow">DEVICES RUNNING</span>
        <strong>
          {devices.filter((d) => isDeviceRunning(d.state)).length}
          <small> / {devices.length}</small>
        </strong>
        <p>{devices.filter((d) => d.allowed).length} controllable</p>
      </div>
      <AnomalySavingsWidget home={home} />
    </section>
  );
}
