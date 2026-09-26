import type { Home, ConnectionStart, Command, HomeSettings } from "./types";
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
    turnOff: (homeId: string, entityIds: string[], idempotencyKey: string) =>
      request<Command[]>(`/homes/${homeId}/commands`, {
        method: "POST",
        body: JSON.stringify({ entityIds, idempotencyKey }),
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
