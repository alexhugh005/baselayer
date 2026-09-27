import type { CurrentControl } from "../../lib/types";

export interface ChargingInputs {
  departure: string;
  targetPercent: string;
  batteryKwh: string;
  chargePercent: string;
  efficiencyPercent: string;
}

export type ChargingPlan =
  | { error: string }
  | {
      amps: number;
      maxAmps: number;
      powerKw: number;
      energyKwh: number;
      readyAt: number;
      departureAt: number;
      achievable: boolean;
      chargeAtDeparture: number;
      targetReached: boolean;
    };

/** Constant-rate estimate. Round UP on the charger's min-relative step grid. */
export function chargingPlan(
  inputs: ChargingInputs,
  control: CurrentControl | null | undefined,
  wattsPerAmp: number | undefined,
  now: number,
): ChargingPlan {
  const departureAt = new Date(inputs.departure).getTime();
  if (!Number.isFinite(departureAt) || departureAt <= now)
    return { error: "Choose a departure date and time in the future." };
  const capacity = Number(inputs.batteryKwh);
  const soc = Number(inputs.chargePercent);
  const target = Number(inputs.targetPercent);
  if (
    !inputs.targetPercent.trim() ||
    !Number.isFinite(target) ||
    target < 1 ||
    target > 100
  )
    return { error: "Enter a charge limit from 1 to 100%." };
  const efficiency = Number(inputs.efficiencyPercent) / 100;
  if (
    !inputs.batteryKwh.trim() ||
    !Number.isFinite(capacity) ||
    capacity <= 0 ||
    capacity > 1000
  )
    return {
      error:
        "Enter a usable battery capacity greater than 0 and up to 1,000 kWh.",
    };
  if (
    !inputs.chargePercent.trim() ||
    !Number.isFinite(soc) ||
    soc < 0 ||
    soc > 100
  )
    return { error: "Enter the car’s current battery level from 0 to 100%." };
  if (
    !inputs.efficiencyPercent.trim() ||
    !Number.isFinite(efficiency) ||
    efficiency <= 0 ||
    efficiency > 1
  )
    return {
      error: "Enter a charging efficiency greater than 0 and up to 100%.",
    };
  if (
    !control ||
    ![control.min, control.max, control.step, wattsPerAmp].every(
      (value) => typeof value === "number" && Number.isFinite(value),
    ) ||
    control.min < 0 ||
    control.max <= control.min ||
    control.step <= 0 ||
    !wattsPerAmp ||
    wattsPerAmp <= 0
  )
    return {
      error:
        "The charger’s current range is unavailable. Check its pairing in Settings.",
    };

  const maxAmps =
    control.min +
    Math.floor((control.max - control.min) / control.step + 1e-9) *
      control.step;
  if (maxAmps <= 0)
    return { error: "The charger has no supported positive charging current." };
  const energyKwh = Math.max(0, (capacity * (target - soc)) / 100);
  const hours = (departureAt - now) / 3_600_000;
  const required = (energyKwh * 1000) / (hours * wattsPerAmp * efficiency);
  const rounded =
    control.min +
    Math.max(0, Math.ceil((required - control.min) / control.step - 1e-9)) *
      control.step;
  const amps =
    energyKwh === 0
      ? 0
      : Math.min(
          maxAmps,
          Math.max(rounded, control.min === 0 ? control.step : control.min),
        );
  const powerKw = (amps * wattsPerAmp) / 1000;
  const readyAt =
    energyKwh === 0
      ? now
      : now + (energyKwh / (powerKw * efficiency)) * 3_600_000;
  return {
    amps: Number(amps.toFixed(8)),
    maxAmps,
    powerKw,
    energyKwh,
    readyAt,
    departureAt,
    achievable: readyAt <= departureAt + 1,
    chargeAtDeparture: Math.max(
      soc,
      Math.min(target, soc + ((powerKw * efficiency * hours) / capacity) * 100),
    ),
    targetReached: energyKwh === 0,
  };
}
