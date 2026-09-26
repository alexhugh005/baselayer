import type { Home } from "../../lib/types";

export function smartPowerOffMessage(home: Home) {
  switch (home.smartPowerOffStatus) {
    case "reducing":
      return "Smart Shutoff is reducing EV charging or turning off Anytime devices and checking updated usage.";
    case "review":
      return "Usage is still high after checking Anytime devices. Review the fewest Sometimes devices needed, then approve their shutoff.";
    case "insufficient":
      return "The measured Sometimes devices are not enough. Turn off additional appliances yourself to get below the usage limit and help the battery return to a working state. Never devices will not be turned off by Base Layer.";
    default:
      return null;
  }
}
