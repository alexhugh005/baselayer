import { expect, it } from "vitest";
import type { Device } from "../../lib/types";
import { detectDeviceCategory } from "./categoryDetection";

const categories = [
  "toaster",
  "microwave",
  "coffee-maker",
  "refrigerator",
  "dishwasher",
  "washer",
  "dryer",
  "space-heater",
  "water-heater",
  "television",
  "computer",
  "ceiling-fan",
  "custom",
].map((id) => ({ id, name: id, standardWatts: null }));
const device: Device = {
  entityId: "switch.plug_1",
  name: "Smart plug",
  state: "on",
  powerWatts: 1200,
  allowed: false,
  recommended: false,
  powerSensorId: null,
};

it.each([
  ["Kitchen Toaster", "toaster"],
  ["Microwave", "microwave"],
  ["Coffee Maker", "coffee-maker"],
  ["Espresso machine", "coffee-maker"],
  ["Garage Fridge / Freezer", "refrigerator"],
  ["Dish washer", "dishwasher"],
  ["Clothes Washer", "washer"],
  ["Washing Machine", "washer"],
  ["Laundry Dryer", "dryer"],
  ["Space Heater", "space-heater"],
  ["Virtual Heater", "space-heater"],
  ["Bedroom Heater", "space-heater"],
  ["Electric Water Heater", "water-heater"],
  ["Living Room TV", "television"],
  ["Office PC", "computer"],
  ["Bedroom Ceiling Fan", "ceiling-fan"],
])("detects %s as %s", (name, expected) => {
  expect(detectDeviceCategory({ ...device, name }, categories)).toBe(expected);
});

it.each([
  "Smart plug",
  "Washer / Dryer",
  "Hair dryer",
  "Hand dryer",
  "Toaster oven",
  "Gas Dryer",
  "Heat pump dryer",
  "Tankless Water Heater",
  "Pool heater",
  "HVAC",
  "TV and Computer",
  "Dishwasher and dryer",
  "Ceiling fan and lights",
  "Pressure washer",
])("leaves unsupported or ambiguous %s unassigned", (name) => {
  expect(detectDeviceCategory({ ...device, name }, categories)).toBe("");
});

it("uses normalized entity IDs and saved power sensor identities", () => {
  expect(
    detectDeviceCategory(
      { ...device, entityId: "switch.kitchen_coffeeMaker" },
      categories,
    ),
  ).toBe("coffee-maker");
  expect(
    detectDeviceCategory(
      { ...device, powerSensorId: "sensor.fridge_power" },
      categories,
      [
        {
          entityId: "sensor.fridge_power",
          name: "Garage refrigerator power",
          unit: "W",
        },
      ],
    ),
  ).toBe("refrigerator");
});

it("rejects conflicting identities, unavailable categories, and saved choices", () => {
  expect(
    detectDeviceCategory(
      { ...device, name: "Toaster", entityId: "switch.microwave" },
      categories,
    ),
  ).toBe("");
  expect(detectDeviceCategory({ ...device, name: "Toaster" }, [])).toBe("");
  expect(
    detectDeviceCategory(
      { ...device, name: "Toaster", category: "custom" },
      categories,
    ),
  ).toBe("");
  expect(
    detectDeviceCategory(
      { ...device, name: "Toaster", powerStandardConfigured: true },
      categories,
    ),
  ).toBe("");
});
