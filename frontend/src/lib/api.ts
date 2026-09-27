import type {
  Home,
  CircuitPriority,
  ConnectionStart,
  Command,
  HomeSettings,
  SmartUsagePlan,
  EvBatterySettings,
  EvVehicle,
} from "./types";
export type TokenProvider = () => Promise<string | null>;
export function createApi(getToken: TokenProvider) {
  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const token = await getToken();
    if (!token) throw new Error("Sign in to continue.");
    const response = await fetch(
      `${import.meta.env.VITE_API_URL ?? ""}/api${path}`,
      {
        ...init,
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
          ...init?.headers,
        },
      },
    );
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(problem.title ?? `Request failed (${response.status}).`);
    }
    return response.status === 204 ? (undefined as T) : response.json();
  }
  return {
    homes: () => request<Home[]>("/homes"),
    smartUsage: (homeId: string, targetWatts?: number, signal?: AbortSignal) =>
      request<SmartUsagePlan>(
        `/homes/${homeId}/smart-usage${targetWatts === undefined ? "" : `?targetWatts=${targetWatts}`}`,
        { signal },
      ),
    applySmartUsage: (
      homeId: string,
      targetWatts: number,
      revision: string,
      idempotencyKey: string,
    ) =>
      request<Command[]>(`/homes/${homeId}/smart-usage`, {
        method: "POST",
        body: JSON.stringify({ targetWatts, revision, idempotencyKey }),
      }),
    connect: (name: string, baseUrl: string) =>
      request<ConnectionStart>("/connections/home-assistant/start", {
        method: "POST",
        body: JSON.stringify({ name, baseUrl }),
      }),
    complete: (code: string, state: string) =>
      request<Home>("/connections/home-assistant/complete", {
        method: "POST",
        body: JSON.stringify({ code, state }),
      }),
    settings: (homeId: string, settings: HomeSettings) =>
      request<Home>(`/homes/${homeId}/settings`, {
        method: "PUT",
        body: JSON.stringify(settings),
      }),
    anomalySavings: (homeId: string, enabled: boolean) =>
      request<Home>(`/homes/${homeId}/anomaly-savings`, {
        method: "PUT",
        body: JSON.stringify({ enabled }),
      }),
    smartPowerOff: (
      homeId: string,
      enabled: boolean,
      alwaysKeepBelowBatteryLimit?: boolean,
    ) =>
      request<Home>(`/homes/${homeId}/smart-power-off`, {
        method: "PUT",
        body: JSON.stringify({ enabled, alwaysKeepBelowBatteryLimit }),
      }),
    keepOff: (homeId: string, entityId: string) =>
      request<Home>(
        `/homes/${homeId}/restore-queue/${encodeURIComponent(entityId)}`,
        { method: "DELETE" },
      ),
    circuitCommand: (
      homeId: string,
      entityId: string,
      action: "On" | "Off",
      idempotencyKey: string,
    ) =>
      request<Command>(`/homes/${homeId}/circuits/commands`, {
        method: "POST",
        body: JSON.stringify({ entityId, action, idempotencyKey }),
      }),
    circuitPriority: (
      homeId: string,
      entityId: string,
      priority: CircuitPriority,
      idempotencyKey: string,
    ) =>
      request<Command>(`/homes/${homeId}/circuits/priority`, {
        method: "PUT",
        body: JSON.stringify({ entityId, priority, idempotencyKey }),
      }),
    evCurrent: (
      homeId: string,
      entityId: string,
      amps: number,
      idempotencyKey: string,
      chargeLimitPercent?: number,
      vehicleId?: string,
      startCharge?: boolean,
    ) =>
      request<Command>(`/homes/${homeId}/ev/current`, {
        method: "POST",
        body: JSON.stringify({
          entityId,
          amps,
          idempotencyKey,
          chargeLimitPercent,
          vehicleId,
          startCharge,
        }),
      }),
    saveEvVehicle: (homeId: string, vehicle: EvVehicle) =>
      request<Home>(`/homes/${homeId}/ev/vehicles`, {
        method: "PUT",
        body: JSON.stringify(vehicle),
      }),
    evBatterySettings: (
      homeId: string,
      entityId: string,
      settings: EvBatterySettings | null,
    ) =>
      request<Home>(`/homes/${homeId}/ev/battery`, {
        method: "PUT",
        body: JSON.stringify({ entityId, settings }),
      }),
    turnOff: (homeId: string, entityIds: string[], idempotencyKey: string) =>
      request<Command[]>(`/homes/${homeId}/commands`, {
        method: "POST",
        body: JSON.stringify({ entityIds, idempotencyKey }),
      }),
    turnOffAnomalies: (
      homeId: string,
      entityIds: string[],
      idempotencyKey: string,
    ) =>
      request<Command[]>(`/homes/${homeId}/commands`, {
        method: "POST",
        body: JSON.stringify({ entityIds, idempotencyKey, forAnomaly: true }),
      }),
    revoke: (homeId: string) =>
      request<void>(`/homes/${homeId}/connection`, { method: "DELETE" }),
    cancelCommand: (homeId: string, commandId: string) =>
      request<Command>(`/homes/${homeId}/commands/${commandId}`, {
        method: "DELETE",
      }),
    deleteHome: (homeId: string) =>
      request<void>(`/homes/${homeId}`, { method: "DELETE" }),
  };
}
export type Api = ReturnType<typeof createApi>;
