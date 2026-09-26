import type { Device, PowerSensor } from "../../lib/types";

// Retain location and numeric identifiers; remove only measurement vocabulary.
const measurementWords = new Set([
  "power",
  "watts",
  "watt",
  "kw",
  "w",
  "consumption",
  "electric",
  "electrical",
]);
function tokens(value: string) {
  return value
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .split(/[^a-z0-9]+/)
    .filter((word) => word && !measurementWords.has(word));
}
function similarity(a: string, b: string) {
  const left = new Set(tokens(a)),
    right = new Set(tokens(b));
  if (!left.size || !right.size) return 0;
  const common = [...left].filter((word) => right.has(word)).length;
  return common / new Set([...left, ...right]).size;
}
export function sensorMatchScore(device: Device, sensor: PowerSensor) {
  const identities = [device, sensor].map((item) =>
    tokens(`${item.name} ${item.entityId.split(".").slice(1).join(" ")}`),
  );
  const locations = new Set([
    "bedroom",
    "kitchen",
    "bathroom",
    "garage",
    "basement",
    "attic",
    "office",
    "upstairs",
    "downstairs",
    "living",
    "laundry",
  ]);
  for (const isIdentity of [
    (word: string) => /^\d+$/.test(word),
    (word: string) => locations.has(word),
  ]) {
    const [left, right] = identities.map((words) =>
      [...new Set(words.filter(isIdentity))].sort().join(" "),
    );
    if (left && right && left !== right) return 0;
  }
  return Math.max(
    similarity(device.name, sensor.name),
    similarity(
      device.entityId.split(".").slice(1).join("."),
      sensor.entityId.split(".").slice(1).join("."),
    ),
  );
}

export function smartMatchPowerSensors(
  devices: Device[],
  sensors: PowerSensor[],
  householdMeterId: string | null,
): Record<string, string> {
  const mapping = Object.fromEntries(
    devices.map((d) => [d.entityId, d.powerSensorId ?? ""]),
  );
  const reserved = new Set(Object.values(mapping).filter(Boolean));
  if (householdMeterId) reserved.add(householdMeterId);
  const candidates = devices
    .filter((d) => !mapping[d.entityId])
    .map((device) => {
      const ranked = sensors
        .filter((s) => !reserved.has(s.entityId))
        .map((sensor) => ({ sensor, score: sensorMatchScore(device, sensor) }))
        .sort((a, b) => b.score - a.score);
      const best = ranked[0];
      // Do not guess when two sensors are nearly indistinguishable.
      return best &&
        best.score >= 0.75 &&
        best.score - (ranked[1]?.score ?? 0) >= 0.15
        ? { device, sensor: best.sensor }
        : null;
    })
    .filter((candidate) => candidate !== null);
  for (const { device, sensor } of candidates) {
    // A sensor claimed by multiple devices remains a manual choice.
    if (
      candidates.filter((c) => c.sensor.entityId === sensor.entityId).length ===
      1
    )
      mapping[device.entityId] = sensor.entityId;
  }
  return mapping;
}
