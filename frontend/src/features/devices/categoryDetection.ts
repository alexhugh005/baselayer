import type { Device, DeviceCategory, PowerSensor } from "../../lib/types";

const rules: [string, RegExp][] = [
  ["toaster", /\btoaster\b/],
  ["microwave", /\bmicrowave\b/],
  ["coffee-maker", /\b(coffee ?(maker|machine)|espresso|keurig|nespresso)\b/],
  ["refrigerator", /\b(refrigerator|fridge|freezer)\b/],
  ["dishwasher", /\bdish ?washer\b/],
  ["washer", /\b(washer|washing machine)\b/],
  ["dryer", /\bdryer\b/],
  ["space-heater", /(?<!water )\bheater\b/],
  ["water-heater", /\bwater heater\b/],
  ["television", /\b(television|tv)\b/],
  ["computer", /\b(desktop|computer|pc|workstation)\b/],
  ["ceiling-fan", /\bceiling fan\b/],
];

function normalize(value: string) {
  return (
    value
      .replace(/([a-z])([A-Z])/g, "$1 $2")
      .toLowerCase()
      .normalize("NFKD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/[^a-z0-9]+/g, " ")
      // Keep "dish washer" from also matching clothes washers.
      .replace(/\bdish washer\b/g, "dishwasher")
  );
}

/** Suggest only a single supported appliance type; power draw is not identity. */
export function detectDeviceCategory(
  device: Device,
  categories: DeviceCategory[],
  sensors: PowerSensor[] = [],
): string {
  if (device.category || device.powerStandardConfigured) return "";
  const sensor = sensors.find((s) => s.entityId === device.powerSensorId);
  const identity = [device, ...(sensor ? [sensor] : [])]
    .flatMap((item) => [item.name, item.entityId.split(".").slice(1).join(" ")])
    .map(normalize)
    .join(" | ");
  // These appliances need different standards from the available estimates.
  if (
    /\b(hair|hand|blow) dryer\b|\bpressure washer\b|\btoaster oven\b|\b(gas|heat pump|tankless|pool|spa|aquarium|pond|furnace|hvac)\b|\b(and|with) lights\b/.test(
      identity,
    )
  )
    return "";
  const matches = rules.filter(([, pattern]) => pattern.test(identity));
  if (matches.length !== 1) return "";
  const category = matches[0][0];
  return categories.some((c) => c.id === category) ? category : "";
}
