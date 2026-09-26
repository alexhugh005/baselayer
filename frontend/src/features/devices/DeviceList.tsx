import { isDeviceRunning } from "../../lib/deviceState";
import { Lamp, Wind, Power, Lock, Check, Flame, Plug } from "lucide-react";
import type { Device } from "../../lib/types";
import { formatPower } from "../dashboard/power";
import { Badge } from "../../components/ui/Badge";
export function DeviceList({
  devices,
  selected,
  onToggle,
  disabled,
}: {
  devices: Device[];
  selected: string[];
  onToggle: (id: string) => void;
  disabled: boolean;
}) {
  return (
    <div className="device-list">
      {devices.map((d) => {
        const Icon = d.entityId.startsWith("light.")
          ? Lamp
          : d.entityId.startsWith("fan.")
            ? Wind
            : d.entityId.includes("heater")
              ? Flame
              : Plug;
        const enabled = d.allowed && isDeviceRunning(d.state) && !disabled;
        return (
          <label
            className={`device-row ${selected.includes(d.entityId) ? "selected" : ""}`}
            key={d.entityId}
          >
            <input
              type="checkbox"
              checked={selected.includes(d.entityId)}
              disabled={!enabled}
              onChange={() => onToggle(d.entityId)}
              aria-label={`Select ${d.name}`}
            />
            <span className="device-icon">
              <Icon size={22} />
            </span>
            <span className="device-name">
              <strong>{d.name}</strong>
              <small>
                {isDeviceRunning(d.state)
                  ? "Running"
                  : d.state === "off"
                    ? "Off"
                    : "Unavailable"}
                {!d.allowed ? " · View only" : ""}
              </small>
            </span>
            <span className="device-tag">
              {d.recommended && <Badge tone="amber">Suggested</Badge>}
            </span>
            <strong className="device-power">
              {formatPower(d.powerWatts)}
            </strong>
            <span
              className="access-icon"
              title={d.allowed ? "Control permitted" : "Control not permitted"}
            >
              {d.allowed ? <Check size={16} /> : <Lock size={16} />}
            </span>
          </label>
        );
      })}
      {devices.length === 0 && (
        <p className="empty">
          <Power /> Devices will appear after the integration connects.
        </p>
      )}
    </div>
  );
}
