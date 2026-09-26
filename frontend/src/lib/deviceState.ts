/** Climate entities report modes such as heat/cool rather than literal "on". */
export function isDeviceRunning(state: string): boolean {
  return !["off", "unknown", "unavailable"].includes(state);
}
