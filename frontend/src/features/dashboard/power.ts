import { isDeviceRunning } from "../../lib/deviceState";
export const formatPower = (watts: number | null) =>
  watts === null ? "Unknown" : `${(watts / 1000).toFixed(2)} kW`;
export function recommendationIds(
  devices: {
    entityId: string;
    recommended: boolean;
    allowed: boolean;
    state: string;
  }[],
) {
  return devices
    .filter((d) => d.recommended && d.allowed && isDeviceRunning(d.state))
    .map((d) => d.entityId);
}
