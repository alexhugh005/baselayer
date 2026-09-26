import { EvChargeControl } from "./EvChargeControl";
import { useId } from "react";
import { isDeviceRunning } from "../../lib/deviceState";
import {
  Lamp,
  Wind,
  Power,
  Lock,
  Check,
  Flame,
  Plug,
  Link,
  WashingMachine,
  Thermometer,
  CarFront,
} from "lucide-react";
import type { Device } from "../../lib/types";
import { formatPower } from "../dashboard/power";
import { Badge } from "../../components/ui/Badge";
import { LoadingStatus } from "../../components/ui/LoadingStatus";
import { Button } from "../../components/ui/Button";

function getDeviceIcon(device: Device) {
  const description = `${device.entityId} ${device.name}`
    .toLowerCase()
    .replace(/[_.-]+/g, " ");

  if (
    device.evCharging ||
    device.evCurrent ||
    /\b(ev|evse|electric vehicle|electric car)\b/.test(description)
  )
    return CarFront;
  if (
    device.entityId.startsWith("climate.") ||
    /\bthermostat\b/.test(description)
  )
    return Thermometer;
  if (/\bdryer\b/.test(description)) return WashingMachine;
  if (device.entityId.startsWith("light.")) return Lamp;
  if (device.entityId.startsWith("fan.")) return Wind;
  if (device.entityId.includes("heater")) return Flame;
  return Plug;
}

export function DeviceList({
  devices,
  selected,
  onToggle,
  disabled,
  loading = false,
  powerSuggestions = {},
  connectingDevice = "",
  onConnectPowerSource,
  changingShutoffDevice = "",
  onCycleShutoffLevel,
  onSetEvCurrent,
  pendingCommands = false,
}: {
  onSetEvCurrent?: (id: string, amps: number) => Promise<void>;
  pendingCommands?: boolean;
  devices: Device[];
  selected: string[];
  onToggle: (id: string) => void;
  disabled: boolean;
  loading?: boolean;
  powerSuggestions?: Record<string, string>;
  connectingDevice?: string;
  onConnectPowerSource?: (id: string) => void;
  changingShutoffDevice?: string;
  onCycleShutoffLevel?: (id: string) => void;
}) {
  const selectionId = useId();
  return (
    <div className="device-list">
      {devices.map((d) => {
        const inputId = `${selectionId}-${d.entityId}`;
        const shutoffLevel = d.allowed
          ? (d.shutoffLevel ?? "Sometimes")
          : "Never";
        const Icon = getDeviceIcon(d);
        const enabled =
          d.allowed &&
          d.shutoffLevel !== "Never" &&
          !d.entityId.startsWith("climate.") &&
          isDeviceRunning(d.state) &&
          !disabled;
        return (
          <div
            className={`device-row ${selected.includes(d.entityId) ? "selected" : ""}`}
            key={d.entityId}
          >
            <div className="device-selection">
              <input
                id={inputId}
                type="checkbox"
                checked={selected.includes(d.entityId)}
                disabled={!enabled}
                onChange={() => onToggle(d.entityId)}
                aria-label={`Select ${d.name}`}
              />
              <label className="device-icon" htmlFor={inputId}>
                <Icon size={22} />
              </label>
              <span className="device-name">
                <strong>
                  <label htmlFor={inputId}>{d.name}</label>
                </strong>
                <small>
                  {isDeviceRunning(d.state)
                    ? "Running"
                    : d.state === "off"
                      ? "Off"
                      : "Unavailable"}
                  {" · "}
                  {onCycleShutoffLevel ? (
                    <button
                      type="button"
                      className="device-shutoff-level"
                      aria-label={`${d.name} Smart Shutoff: ${shutoffLevel}`}
                      aria-busy={changingShutoffDevice === d.entityId}
                      title="Click to change Smart Shutoff permission"
                      disabled={
                        disabled ||
                        !!connectingDevice ||
                        !!changingShutoffDevice
                      }
                      onClick={() => onCycleShutoffLevel(d.entityId)}
                    >
                      {shutoffLevel}
                    </button>
                  ) : (
                    shutoffLevel
                  )}
                  {d.entityId.startsWith("climate.")
                    ? " · Temperature control only"
                    : ""}
                </small>
              </span>
              <span className="device-tag">
                {d.recommended && <Badge tone="amber">Suggested</Badge>}
              </span>
              <strong className="device-power">
                {d.powerWatts === null &&
                d.powerSensorId &&
                !disabled &&
                d.state !== "unavailable" &&
                d.state !== "unknown" ? (
                  <LoadingStatus>Loading…</LoadingStatus>
                ) : (
                  formatPower(d.powerWatts)
                )}
              </strong>
              <span
                className="access-icon"
                title={
                  d.allowed ? "Control permitted" : "Control not permitted"
                }
              >
                {d.allowed ? <Check size={16} /> : <Lock size={16} />}
              </span>
            </div>
            {d.evCharging && onSetEvCurrent && (
              <EvChargeControl
                device={d}
                disabled={
                  disabled ||
                  pendingCommands ||
                  !d.allowed ||
                  d.shutoffLevel === "Never"
                }
                onSetCurrent={(amps) => onSetEvCurrent(d.entityId, amps)}
              />
            )}
            {!d.powerSensorId &&
              powerSuggestions[d.entityId] &&
              onConnectPowerSource && (
                <Button
                  type="button"
                  variant="secondary"
                  className="connect-power-source"
                  disabled={
                    disabled || !!connectingDevice || !!changingShutoffDevice
                  }
                  aria-label={`Connect Suggest Source for ${d.name}`}
                  onClick={() => onConnectPowerSource(d.entityId)}
                >
                  <Link size={16} aria-hidden="true" />
                  {connectingDevice === d.entityId
                    ? "Connecting…"
                    : "Connect Suggest Source"}
                </Button>
              )}
          </div>
        );
      })}
      {devices.length === 0 && (
        <p className="empty">
          {loading ? (
            <LoadingStatus>Loading devices…</LoadingStatus>
          ) : (
            <>
              <Power /> No devices found.
            </>
          )}
        </p>
      )}
    </div>
  );
}
