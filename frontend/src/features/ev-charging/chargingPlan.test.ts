import { expect, it } from "vitest";
import { chargingPlan, type ChargingInputs } from "./chargingPlan";
const now = Date.parse("2026-09-26T20:00:00Z");
const inputs: ChargingInputs = {
  departure: "2026-09-27T06:00:00Z",
  targetPercent: "100",
  batteryKwh: "75",
  chargePercent: "50",
  efficiencyPercent: "90",
};
const control = {
  entityId: "number.ev",
  name: "EV",
  amps: 32,
  min: 6,
  max: 48,
  step: 1,
};
function plan(
  overrides: Partial<ChargingInputs> = {},
  range = control,
  rating = 240,
) {
  const result = chargingPlan({ ...inputs, ...overrides }, range, rating, now);
  if ("error" in result) throw Error(result.error);
  return result;
}
it("rounds up to the lowest rate that meets departure, including charging losses", () => {
  const result = plan();
  expect(result.amps).toBe(18);
  expect(result.achievable).toBe(true);
  expect(result.readyAt).toBeLessThan(Date.parse(inputs.departure));
  expect(((17 * 240) / 1000) * 0.9 * 10).toBeLessThan(result.energyKwh);
});
it("uses maximum when impossible and reports the battery shortfall", () => {
  const result = plan({ departure: "2026-09-26T21:00:00Z" });
  expect(result.amps).toBe(48);
  expect(result.achievable).toBe(false);
  expect(result.chargeAtDeparture).toBeCloseTo(63.824);
});
it("honors minimum, fractional steps, and max not aligned to the step grid", () => {
  expect(plan({ chargePercent: "99" }).amps).toBe(6);
  expect(plan({}, { ...control, min: 6.5, step: 2 }).amps).toBe(18.5);
  expect(
    plan(
      { departure: "2026-09-26T20:01:00Z" },
      { ...control, min: 6, max: 47, step: 2 },
    ).amps,
  ).toBe(46);
  expect(plan({}, control, 690).amps).toBe(7);
});
it("needs no current command when already full", () => {
  expect(plan({ chargePercent: "100" })).toMatchObject({
    targetReached: true,
    amps: 0,
    energyKwh: 0,
  });
});
it.each([
  { departure: "2026-09-26T20:00:00Z" },
  { departure: "invalid" },
  { batteryKwh: "" },
  { batteryKwh: "0" },
  { chargePercent: "" },
  { chargePercent: "101" },
  { chargePercent: "-1" },
  { efficiencyPercent: "0" },
  { efficiencyPercent: "101" },
  { batteryKwh: "Infinity" },
])("rejects invalid inputs %j", (invalid) => {
  expect(
    chargingPlan({ ...inputs, ...invalid }, control, 240, now),
  ).toHaveProperty("error");
});
it("rejects missing or invalid current ranges", () => {
  expect(chargingPlan(inputs, null, 240, now)).toHaveProperty("error");
  expect(
    chargingPlan(inputs, { ...control, step: 0 }, 240, now),
  ).toHaveProperty("error");
});
it("interprets an explicit timezone and allows departure across midnight", () => {
  expect(plan({ departure: "2026-09-27T01:00:00-05:00" }).amps).toBe(18);
});

it("calculates energy and the slowest current to the selected charge limit", () => {
  const result = plan({ targetPercent: "80" });
  expect(result.energyKwh).toBeCloseTo(22.5);
  expect(result.amps).toBe(11);
  expect(result.chargeAtDeparture).toBe(80);
  expect(result.achievable).toBe(true);
});
it.each(["80", "90"])(
  "does not request more energy when battery is %s and limit is 80",
  (chargePercent) => {
    const result = plan({ targetPercent: "80", chargePercent });
    expect(result).toMatchObject({
      targetReached: true,
      energyKwh: 0,
      amps: 0,
    });
    expect(result.chargeAtDeparture).toBe(Number(chargePercent));
  },
);
it.each(["", "0", "101", "NaN"])(
  "rejects invalid charge limit %s",
  (targetPercent) => {
    expect(
      chargingPlan({ ...inputs, targetPercent }, control, 240, now),
    ).toHaveProperty("error");
  },
);
