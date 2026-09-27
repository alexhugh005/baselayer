import type { Home } from "../../lib/types";

export function smartPowerOffMessage(home: Home) {
  const names = home.devices
    .filter((device) => device.recommended)
    .map((device) => device.name)
    .join(", ");
  switch (home.smartPowerOffStatus) {
    case "reducing":
      return home.gridOutageRisk === "high"
        ? "Grid outage risk is high. Smart Shutoff is reducing EV charging and turning off Anytime and Sometimes devices as needed."
        : "Grid outage risk is medium. Smart Shutoff is reducing EV charging or turning off Anytime devices and checking updated usage.";
    case "review":
      return `Turn off ${names} to get below the usage limit. Review these Sometimes devices, then approve their shutoff.`;
    case "insufficient":
      return `The measured Sometimes devices are not enough.${names ? ` Turn off ${names}.` : ""} Turn off additional appliances yourself to get below the usage limit and help the battery return to a working state. Never devices will not be turned off by Base Layer.`;
    default:
      return null;
  }
}
