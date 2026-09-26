export interface PowerSensor {
  entityId: string;
  name: string;
  unit: string;
}
export interface Device {
  entityId: string;
  name: string;
  state: string;
  powerWatts: number | null;
  allowed: boolean;
  recommended: boolean;
  powerSensorId: string | null;
}
export interface Command {
  id: string;
  entityId: string;
  status: string;
  attempts: number;
  createdUtc: string;
  message: string | null;
}
export interface Home {
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
export interface HomeSettings {
  powerSource: "wholeHouseMeter" | "deviceSum";
  allowAll: boolean;
  allowFutureDevices: boolean;
  allowedEntityIds: string[];
  householdPowerSensorId: string | null;
  devicePowerSensors: Record<string, string>;
}
