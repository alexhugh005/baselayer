import { isDeviceRunning } from "../../lib/deviceState";
import { Activity, PlugZap } from "lucide-react";
import type { Home } from "../../lib/types";
import { formatPower } from "./power";
import { LoadingStatus } from "../../components/ui/LoadingStatus";
export function UsageOverview({ home }: { home: Home }) {
  const waitingForUsage =
    home.connected &&
    !home.revoked &&
    home.householdWatts === null &&
    (home.powerSource === "deviceSum"
      ? home.devices.some((device) => device.powerSensorId)
      : !!home.householdPowerSensorId);
  const usage = home.householdWatts,
    percent =
      usage === null ? 0 : Math.min(100, (usage / home.limitWatts) * 100);
  return (
    <section className="overview-grid">
      <div className="card usage-card">
        <div className="eyebrow">
          <Activity size={16} />{" "}
          {home.powerSource === "deviceSum"
            ? "MONITORED DEVICE USAGE"
            : "HOUSEHOLD USAGE"}
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
          className="meter"
          role={usage === null ? "status" : "meter"}
          aria-label="Household power"
          aria-valuemin={0}
          aria-valuemax={home.limitWatts}
          aria-valuenow={
            usage === null ? undefined : Math.min(usage, home.limitWatts)
          }
          aria-valuetext={usage === null ? "Unknown power usage" : undefined}
        >
          <div
            className={usage !== null && usage >= home.limitWatts ? "over" : ""}
            style={{ width: `${percent}%` }}
          />
        </div>
        <div className="meter-label">
          <span>
            {usage === null
              ? "Waiting for a measurement"
              : home.powerSource === "deviceSum"
                ? "Device readings"
                : "Home Assistant"}
          </span>
          <strong>{formatPower(home.limitWatts)} limit</strong>
        </div>
        {home.powerSource === "deviceSum" && (
          <p className="muted">
            {home.devices.filter((d) => d.powerSensorId).length} of{" "}
            {home.devices.length} devices metered · Other usage excluded.
          </p>
        )}
      </div>
      <div className="card metric-card">
        <span className="metric-icon">
          <PlugZap size={23} />
        </span>
        <span className="eyebrow">DEVICES RUNNING</span>
        <strong>
          {home.devices.filter((d) => isDeviceRunning(d.state)).length}
          <small> / {home.devices.length}</small>
        </strong>
        <p>{home.devices.filter((d) => d.allowed).length} controllable</p>
      </div>
    </section>
  );
}
