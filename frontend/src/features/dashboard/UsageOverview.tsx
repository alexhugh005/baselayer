import { isDeviceRunning } from "../../lib/deviceState";
import { Activity, ArrowDownRight, PlugZap } from "lucide-react";
import type { Home } from "../../lib/types";
import { formatPower } from "./power";
export function UsageOverview({ home }: { home: Home }) {
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
            : "LIVE HOUSEHOLD LOAD"}
        </div>
        <div className="usage-value">
          {usage === null ? "—" : (usage / 1000).toFixed(2)} <span>kW</span>
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
                ? "Sum of mapped device readings"
                : "Measured by Home Assistant"}
          </span>
          <strong>11 kW limit</strong>
        </div>
        {home.powerSource === "deviceSum" && (
          <p className="muted">
            {home.devices.filter((d) => d.powerSensorId).length} of{" "}
            {home.devices.length} devices mapped · Unmonitored loads are not
            included.
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
        <p>
          {home.devices.filter((d) => d.allowed).length} devices available to
          control
        </p>
      </div>
      <div className="card metric-card">
        <span className="metric-icon mint">
          <ArrowDownRight size={23} />
        </span>
        <span className="eyebrow">AFTER RECOMMENDATIONS</span>
        <strong className="projected">
          {formatPower(home.projectedWatts)}
        </strong>
        <p>Estimated load after suggested shutoffs</p>
      </div>
    </section>
  );
}
