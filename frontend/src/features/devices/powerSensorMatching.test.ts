import { describe, expect, it } from "vitest";
import type { Device, PowerSensor } from "../../lib/types";
import { smartMatchPowerSensors } from "./powerSensorMatching";
const device = (
  name: string,
  entityId: string,
  powerSensorId: string | null = null,
): Device => ({
  name,
  entityId,
  powerSensorId,
  state: "on",
  powerWatts: null,
  allowed: false,
  recommended: false,
});
const sensor = (name: string, entityId: string): PowerSensor => ({
  name,
  entityId,
  unit: "W",
});
describe("power sensor suggestions", () => {
  it("does not let generic matching labels override conflicting room identifiers", () => {
    for (const other of ["bedroom_2", "kitchen"]) {
      expect(
        smartMatchPowerSensors(
          [device("Heater", "switch.bedroom_1_heater")],
          [sensor("Heater Power", `sensor.${other}_heater_power`)],
          null,
        )["switch.bedroom_1_heater"],
      ).toBe("");
    }
  });
  it("matches names and entity identifiers despite power suffixes", () => {
    expect(
      smartMatchPowerSensors(
        [
          device("Virtual Dryer", "switch.virtual_dryer"),
          device("Laundry", "switch.washer"),
        ],
        [
          sensor("Virtual Dryer Power", "sensor.dryer"),
          sensor("Different label", "sensor.washer_power"),
        ],
        null,
      ),
    ).toEqual({
      "switch.virtual_dryer": "sensor.dryer",
      "switch.washer": "sensor.washer_power",
    });
  });
  it("preserves manual selections and does not reuse their sensors or the house meter", () => {
    expect(
      smartMatchPowerSensors(
        [
          device("Dryer", "switch.dryer", "sensor.manual"),
          device("Manual", "switch.manual"),
          device("House", "switch.house"),
        ],
        [
          sensor("Dryer Power", "sensor.dryer"),
          sensor("Manual Power", "sensor.manual"),
          sensor("House Power", "sensor.house"),
        ],
        "sensor.house",
      ),
    ).toEqual({
      "switch.dryer": "sensor.manual",
      "switch.manual": "",
      "switch.house": "",
    });
  });
  it("leaves ambiguous sensors and competing devices unassigned", () => {
    expect(
      smartMatchPowerSensors(
        [device("Dryer", "switch.dryer")],
        [sensor("Dryer Power", "sensor.a"), sensor("Dryer Watts", "sensor.b")],
        null,
      )["switch.dryer"],
    ).toBe("");
    expect(
      Object.values(
        smartMatchPowerSensors(
          [device("Dryer", "switch.a"), device("Dryer", "switch.b")],
          [sensor("Dryer Power", "sensor.dryer")],
          null,
        ),
      ),
    ).toEqual(["", ""]);
  });
  it("retains room and numeric distinctions and rejects unrelated readings", () => {
    expect(
      smartMatchPowerSensors(
        [
          device("Bedroom 1 Heater", "switch.bedroom_1_heater"),
          device("Oven", "switch.oven"),
        ],
        [
          sensor("Bedroom 2 Heater Power", "sensor.bedroom_2_heater_power"),
          sensor("Household Power", "sensor.total"),
        ],
        null,
      ),
    ).toEqual({ "switch.bedroom_1_heater": "", "switch.oven": "" });
  });
});
