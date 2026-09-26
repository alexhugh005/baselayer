import type { Device, Home } from "../../lib/types";
import { formatPower } from "./power";

export function ProjectedUsage({
  home,
  devices,
  stale = false,
}: {
  home: Home;
  devices: Device[];
  stale?: boolean;
}) {
  const known =
    !stale &&
    home.connected &&
    !home.revoked &&
    home.householdWatts !== null &&
    Number.isFinite(home.householdWatts) &&
    home.householdWatts >= 0 &&
    Number.isFinite(home.limitWatts) &&
    home.limitWatts > 0 &&
    devices.every(
      (device) =>
        device.powerWatts !== null &&
        Number.isFinite(device.powerWatts) &&
        device.powerWatts >= 0,
    );
  const projected = known
    ? Math.max(
        0,
        home.householdWatts! -
          devices.reduce((total, device) => total + device.powerWatts!, 0),
      )
    : null;
  const below = projected !== null && projected < home.limitWatts;
  const status =
    projected === null
      ? "Projection unavailable"
      : below
        ? "Below usage limit"
        : "At or above usage limit";

  return (
    <div
      className={`projected-usage ${projected === null ? "unknown" : below ? "below" : "above"}`}
    >
      <div className="projected-usage-heading" aria-live="polite">
        <strong>Projected Usage</strong>
        <span>
          {projected === null
            ? "Projection unavailable"
            : `${formatPower(projected)} · ${Number((home.limitWatts / 1000).toFixed(2))} kW`}
        </span>
      </div>
      <div
        className="meter"
        role={projected === null ? "status" : "meter"}
        aria-label="Projected Usage"
        aria-valuemin={projected === null ? undefined : 0}
        aria-valuemax={projected === null ? undefined : home.limitWatts}
        aria-valuenow={
          projected === null ? undefined : Math.min(projected, home.limitWatts)
        }
        aria-valuetext={
          projected === null
            ? status
            : `${formatPower(projected)}, ${status.toLowerCase()}`
        }
      >
        <div
          style={{
            width: `${projected === null ? 0 : Math.min(100, (projected / home.limitWatts) * 100)}%`,
          }}
        />
      </div>
    </div>
  );
}
