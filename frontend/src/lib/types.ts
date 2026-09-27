export interface PowerSensor {
  entityId: string;
  name: string;
  unit: string;
}
export type GridOutageRisk = "low" | "medium" | "high";
export type ShutoffLevel = "Never" | "Sometimes" | "Anytime";
export interface EvChargingSettings {
  currentEntityId: string;
  wattsPerAmp: number;
}
export interface EvBatterySettings {
  autoDetected?: boolean;
  sensorEntityId: string;
  capacityKwh: number | null;
  efficiencyPercent: number;
}
export interface EvVehicle {
  id: string;
  name: string;
  chargerEntityId: string;
  battery: EvBatterySettings;
}
export interface ChargeLimitControl {
  entityId: string;
  name: string;
  percent: number | null;
  min: number;
  max: number;
  step: number;
}
export interface EvBatterySensor {
  chargeLimitControl?: ChargeLimitControl | null;
  capacityKwh?: number | null;
  entityId: string;
  name: string;
  percent: number | null;
}
export interface CurrentControl {
  entityId: string;
  name: string;
  amps: number | null;
  min: number;
  max: number;
  step: number;
}
export type CircuitPriority = "never" | "soc_threshold" | "off_grid";
export interface DeviceCategory {
  id: string;
  name: string;
  standardWatts: number | null;
}
export interface DevicePowerSettings {
  category: string;
  standardWatts: number | null;
}
export interface Device {
  powerStandardConfigured?: boolean;
  category?: string | null;
  standardWatts?: number | null;
  standardWattsOverride?: number | null;
  anomalyThresholdWatts?: number | null;
  evBattery?: EvBatterySettings | null;
  circuitPriority?: {
    entityId: string;
    priority: CircuitPriority | null;
    options: CircuitPriority[];
  } | null;
  evCharging?: EvChargingSettings | null;
  evCurrent?: CurrentControl | null;
  isCircuit?: boolean;
  shutoffLevel?: ShutoffLevel;
  thermostatMinF?: number;
  thermostatMaxF?: number;
  entityId: string;
  name: string;
  state: string;
  powerWatts: number | null;
  allowed: boolean;
  recommended: boolean;
  powerSensorId: string | null;
}
export interface UsageAnomaly {
  id: string;
  eventId: string;
  entityId: string;
  usualWatts: number;
  observedWatts: number;
  differenceWatts: number;
  recommendedAction: "Off" | "Inspect";
  detectedUtc: string;
  status: string;
  message: string;
  commandId: string | null;
  estimatedSavedKwh: number;
}
export interface Command {
  chargeLimitPercent?: number | null;
  startCharge?: boolean;
  anomalyId?: string | null;
  outageEventId?: string | null;
  action?: "On" | "Off" | "SetCurrent" | "SetCircuitPriority";
  circuitPriority?: CircuitPriority | null;
  currentAmps?: number | null;
  previousCurrentAmps?: number | null;
  isRestoration?: boolean;
  automatic?: boolean;
  id: string;
  entityId: string;
  status: string;
  attempts: number;
  createdUtc: string;
  message: string | null;
}
export interface RestoreQueueEntry {
  currentAmps?: number | null;
  targetAmps?: number | null;
  entityId: string;
  name: string;
  estimatedWatts: number | null;
  queuedUtc: string;
  status: "waiting" | "restoring" | "failed" | "paused" | "unknown" | "held";
}
export interface Home {
  deviceCategories?: DeviceCategory[];
  evBatterySensors?: EvBatterySensor[];
  evVehicles?: EvVehicle[];
  anomalySavingsEnabled?: boolean;
  anomalySavings?: {
    estimatedSavedKwh: number;
    confirmedActions: number;
    estimatedRatePerKwh: number;
    assumedUndetectedMinutes: number;
  };
  anomalies?: UsageAnomaly[];
  outageRecovery?: {
    status: string;
    eventId: string | null;
    startedUtc: string | null;
    nextRestoreAtUtc: string | null;
    budgetWatts: number;
    measuredWatts: number | null;
    reservedWatts: number | null;
    circuits: {
      entityId: string;
      name: string;
      estimatedWatts: number | null;
      status: string;
      devices?: {
        entityId: string;
        name: string;
        targetState: string;
        status: string;
      }[];
    }[];
  };
  powerSupply?: {
    state: "grid" | "battery" | "solar" | "generator" | "none" | "unknown";
    isOnBattery: boolean | null;
    observedAtUtc: string | null;
    sources: { entityId: string; state: string }[];
  };
  currentControls?: CurrentControl[];
  restoreQueue?: RestoreQueueEntry[];
  gridOutageRisk?: GridOutageRisk;
  smartPowerOffEnabled?: boolean;
  smartPowerOffStatus?:
    | "off"
    | "unknown"
    | "monitoring"
    | "reducing"
    | "review"
    | "insufficient";
  powerSource: "wholeHouseMeter" | "deviceSum";
  id: string;
  name: string;
  connected: boolean;
  revoked: boolean;
  lastSeenUtc: string | null;
  householdWatts: number | null;
  limitWatts: number;
  projectedWatts: number | null;
  devices: Device[];
  commands: Command[];
  baseUrl: string;
  householdPowerSensorId: string | null;
  allowFutureDevices: boolean;
  powerSensors: PowerSensor[];
}
export interface ConnectionStart {
  authorizationUrl: string;
  state: string;
  expiresUtc: string;
}
export interface BatteryStatus {
  homeId: string;
  capacityKwh: number;
  storedEnergyKwh: number;
  stateOfChargePercent: number;
  observedAtUtc: string;
  isSimulated: boolean;
}
export interface SmartUsagePlan {
  homeId: string;
  battery: BatteryStatus;
  calculatedAtUtc: string;
  currentWatts: number;
  allOnWatts: number;
  minimumWatts: number;
  targetWatts: number;
  projectedWatts: number;
  currentHours: number | null;
  shortestHours: number | null;
  longestHours: number | null;
  projectedHours: number | null;
  limitWatts: number;
  changes: {
    entityId: string;
    name: string;
    action: "On" | "Off";
    estimatedWatts: number;
    shutoffLevel: ShutoffLevel;
  }[];
  excludedDevices: { entityId: string; name: string; reason: string }[];
  canApply: boolean;
  blockedReason: string | null;
  revision: string;
}
export interface HomeSettings {
  devicePowerStandards?: Record<string, DevicePowerSettings | null>;
  evCharging?: Record<string, EvChargingSettings | null>;
  gridOutageRisk?: GridOutageRisk;
  smartPowerOffEnabled?: boolean;
  shutoffLevels?: Record<string, ShutoffLevel>;
  thermostatLimits?: Record<string, { minF: number; maxF: number }>;
  powerSource: "wholeHouseMeter" | "deviceSum";
  allowAll: boolean;
  allowFutureDevices: boolean;
  allowedEntityIds: string[];
  householdPowerSensorId: string | null;
  devicePowerSensors: Record<string, string>;
}
