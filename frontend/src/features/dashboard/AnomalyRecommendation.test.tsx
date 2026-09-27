// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  within,
  waitFor,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { Dashboard } from "./Dashboard";
import { AnomalyRecommendation } from "./AnomalyRecommendation";

const home: Home = {
  id: "home",
  name: "Test home",
  connected: true,
  revoked: false,
  lastSeenUtc: new Date().toISOString(),
  householdWatts: 3200,
  limitWatts: 11000,
  projectedWatts: 3200,
  gridOutageRisk: "low",
  commands: [],
  baseUrl: "http://localhost:8123",
  powerSource: "deviceSum",
  householdPowerSensorId: null,
  allowFutureDevices: false,
  powerSensors: [],
  devices: [
    {
      entityId: "switch.heater",
      name: "Heater",
      state: "on",
      powerWatts: 3000,
      allowed: true,
      recommended: false,
      shutoffLevel: "Sometimes",
      powerSensorId: "sensor.heater",
      category: "space-heater",
      standardWatts: 1500,
      anomalyThresholdWatts: 2250,
    },
    {
      entityId: "switch.lamp",
      name: "Lamp",
      state: "on",
      powerWatts: 12,
      allowed: true,
      recommended: false,
      powerSensorId: "sensor.lamp",
    },
  ],
};
beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.open = true;
  };
  HTMLDialogElement.prototype.close = function () {
    this.open = false;
  };
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  localStorage.clear();
});

it("opens the existing recommendations dialog and turns off only the affected device at low grid risk", async () => {
  let current = home;
  const turnOff = vi.fn(async () => {
    current = {
      ...home,
      devices: home.devices.map((d) =>
        d.entityId === "switch.heater"
          ? { ...d, state: "off", powerWatts: 0.5 }
          : d,
      ),
    };
    return [];
  });
  render(
    <Dashboard
      api={
        {
          homes: async () => [current],
          turnOffAnomalies: turnOff,
        } as unknown as Api
      }
    />,
  );
  const alert = await screen.findByRole("alert", {
    name: "Unusual device usage",
  });
  expect(alert.classList.contains("recommendation")).toBe(true);
  expect(within(alert).getByText(/Heater \(3.00 kW\)/)).toBeTruthy();
  fireEvent.click(
    within(alert).getByRole("button", { name: "Device Recommendations" }),
  );
  const dialog = within(
    screen.getByRole("dialog", { name: "Device Recommendations" }),
  );
  expect(
    (
      dialog.getByRole("checkbox", {
        name: "Select Heater",
      }) as HTMLInputElement
    ).checked,
  ).toBe(true);
  expect(dialog.queryByRole("checkbox", { name: "Select Lamp" })).toBeNull();
  expect(dialog.queryByRole("meter")).toBeNull();
  fireEvent.click(dialog.getByRole("button", { name: "Turn Off" }));
  await waitFor(() =>
    expect(turnOff).toHaveBeenCalledWith(
      "home",
      ["switch.heater"],
      expect.any(String),
    ),
  );
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  expect(
    screen.queryByRole("alert", { name: "Unusual device usage" }),
  ).toBeNull();
});

it("shows protected devices for review without making them selectable", async () => {
  const protectedHome = {
    ...home,
    devices: [
      { ...home.devices[0], allowed: false, shutoffLevel: "Never" as const },
      { ...home.devices[0], entityId: "climate.room", name: "Thermostat" },
      {
        ...home.devices[0],
        entityId: "switch.span_panel_office_breaker",
        name: "Circuit",
        isCircuit: true,
      },
    ],
  };
  const turnOff = vi.fn();
  render(
    <Dashboard
      api={
        {
          homes: async () => [protectedHome],
          turnOffAnomalies: turnOff,
        } as unknown as Api
      }
    />,
  );
  fireEvent.click(
    await screen.findByRole("button", { name: "Device Recommendations" }),
  );
  const dialog = within(screen.getByRole("dialog"));
  for (const checkbox of dialog.getAllByRole("checkbox")) {
    expect((checkbox as HTMLInputElement).disabled).toBe(true);
    expect((checkbox as HTMLInputElement).checked).toBe(false);
  }
  expect(
    (dialog.getByRole("button", { name: "Turn Off" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  expect(turnOff).not.toHaveBeenCalled();
});

it("does not show historical events or unavailable readings as an active issue", () => {
  const view = render(
    <AnomalyRecommendation
      home={home}
      stale={false}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  expect(screen.getByRole("alert")).toBeTruthy();
  view.rerender(
    <AnomalyRecommendation
      home={{
        ...home,
        devices: home.devices.map((d) => ({ ...d, powerWatts: null })),
      }}
      stale={false}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  expect(screen.queryByRole("alert")).toBeNull();
  view.rerender(
    <AnomalyRecommendation
      home={home}
      stale={true}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  expect(screen.queryByRole("alert")).toBeNull();
});

it("keeps the live warning after 20 seconds and blocks recommendations during pending shutoff", () => {
  vi.useFakeTimers();
  const view = render(
    <AnomalyRecommendation
      home={home}
      stale={false}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  act(() => vi.advanceTimersByTime(60_000));
  expect(screen.getByRole("alert")).toBeTruthy();
  view.rerender(
    <AnomalyRecommendation
      home={{
        ...home,
        commands: [
          {
            id: "c1",
            entityId: "switch.heater",
            status: "AwaitingConfirmation",
            attempts: 1,
            createdUtc: new Date().toISOString(),
            message: null,
          },
        ],
      }}
      stale={false}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  expect(
    screen.getByText("Anomaly detection is confirming shutoff"),
  ).toBeTruthy();
  expect(
    screen.queryByRole("button", { name: "Device Recommendations" }),
  ).toBeNull();
});

it("disables submission if readings normalize while recommendations are open", async () => {
  vi.useFakeTimers();
  let current = home;
  const turnOff = vi.fn();
  render(
    <Dashboard
      api={
        {
          homes: async () => [current],
          turnOffAnomalies: turnOff,
        } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  fireEvent.click(
    screen.getByRole("button", { name: "Device Recommendations" }),
  );
  current = {
    ...home,
    devices: home.devices.map((d) => ({ ...d, powerWatts: 100 })),
  };
  await act(async () => vi.advanceTimersByTimeAsync(3000));
  const dialog = within(screen.getByRole("dialog"));
  expect(
    (dialog.getByRole("button", { name: "Turn Off" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  expect(turnOff).not.toHaveBeenCalled();
});

it("keeps automatic anomaly progress separate from Smart Shutoff even after the device reads off", async () => {
  const current = {
    ...home,
    smartPowerOffEnabled: true,
    smartPowerOffStatus: "monitoring" as const,
    devices: home.devices.map((d) => ({ ...d, state: "off", powerWatts: 0.5 })),
    commands: [
      {
        id: "anomaly-command",
        entityId: "switch.heater",
        anomalyId: "anomaly-1",
        automatic: true,
        status: "AwaitingConfirmation",
        attempts: 1,
        createdUtc: new Date().toISOString(),
        message: "Waiting for anomaly shutoff.",
      },
    ],
  };
  render(
    <Dashboard api={{ homes: async () => [current] } as unknown as Api} />,
  );
  expect(
    await screen.findByText("Anomaly detection is confirming shutoff"),
  ).toBeTruthy();
  expect(
    screen.queryByRole("region", { name: "Usage limit notification" }),
  ).toBeNull();
  expect(screen.queryByText("Smart Shutoff is checking usage")).toBeNull();
});

it("uses the anomaly endpoint and never shows Smart Shutoff progress while submitting", async () => {
  let finish!: (value: []) => void;
  const turnOffAnomalies = vi.fn(
    () =>
      new Promise<[]>((resolve) => {
        finish = resolve;
      }),
  );
  const turnOff = vi.fn();
  const current = {
    ...home,
    smartPowerOffEnabled: true,
    smartPowerOffStatus: "monitoring" as const,
  };
  render(
    <Dashboard
      api={
        {
          homes: async () => [current],
          turnOffAnomalies,
          turnOff,
        } as unknown as Api
      }
    />,
  );
  fireEvent.click(
    await screen.findByRole("button", { name: "Device Recommendations" }),
  );
  fireEvent.click(
    within(screen.getByRole("dialog")).getByRole("button", {
      name: "Turn Off",
    }),
  );
  await waitFor(() =>
    expect(turnOffAnomalies).toHaveBeenCalledWith(
      "home",
      ["switch.heater"],
      expect.any(String),
    ),
  );
  expect(turnOff).not.toHaveBeenCalled();
  expect(screen.queryByText("Smart Shutoff is checking usage")).toBeNull();
  expect(
    screen.getByText("Anomaly detection is turning off devices"),
  ).toBeTruthy();
  await act(async () => finish([]));
});

it("shows live shutoff stages and removes the notice after confirmation, including lagging power readings", () => {
  const command = {
    id: "heater-shutoff",
    entityId: "switch.heater",
    anomalyId: "anomaly-1",
    action: "Off" as const,
    automatic: true,
    status: "Pending",
    attempts: 0,
    createdUtc: new Date().toISOString(),
    message: null,
  };
  const props = { stale: false, busy: false, onReview: vi.fn() };
  const view = render(
    <AnomalyRecommendation
      {...props}
      home={{ ...home, commands: [command] }}
    />,
  );
  expect(
    screen.getByText("Anomaly detection is turning off devices"),
  ).toBeTruthy();
  expect(screen.getByRole("status").textContent).toContain(
    "Heater: sending shutoff command",
  );
  expect(
    screen.queryByRole("button", { name: "Device Recommendations" }),
  ).toBeNull();
  expect(screen.getByRole("alert").querySelector(".spin")).toBeTruthy();

  for (const [status, heading, message] of [
    [
      "AwaitingConfirmation",
      "confirming shutoff",
      "waiting for the device to confirm",
    ],
    ["Retrying", "retrying shutoff", "has not been confirmed; retrying"],
  ]) {
    view.rerender(
      <AnomalyRecommendation
        {...props}
        home={{ ...home, commands: [{ ...command, status, attempts: 1 }] }}
      />,
    );
    expect(screen.getByText(`Anomaly detection is ${heading}`)).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain(message);
  }

  view.rerender(
    <AnomalyRecommendation
      {...props}
      home={{
        ...home,
        devices: home.devices.map((device) => ({ ...device, state: "off" })),
        commands: [{ ...command, status: "Confirmed", attempts: 1 }],
      }}
    />,
  );
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.queryByRole("status")).toBeNull();
  expect(
    screen.queryByRole("button", { name: "Device Recommendations" }),
  ).toBeNull();

  // A later actual restart is a new issue, not a replay of the completed notice.
  view.rerender(
    <AnomalyRecommendation
      {...props}
      home={{
        ...home,
        commands: [{ ...command, status: "Confirmed", attempts: 1 }],
      }}
    />,
  );
  expect(screen.getByText("Unusual device usage")).toBeTruthy();
  expect(
    screen.getByRole("button", { name: "Device Recommendations" }),
  ).toBeTruthy();
});

it("stops showing progress after a failed action and allows review if excess usage remains", () => {
  render(
    <AnomalyRecommendation
      home={{
        ...home,
        commands: [
          {
            id: "failed-shutoff",
            entityId: "switch.heater",
            anomalyId: "anomaly-1",
            status: "Failed",
            attempts: 3,
            createdUtc: new Date().toISOString(),
            message: "Device did not confirm shutoff.",
          },
        ],
      }}
      stale={false}
      busy={false}
      onReview={vi.fn()}
    />,
  );
  expect(screen.queryByRole("status")).toBeNull();
  expect(
    screen.getByRole("button", { name: "Device Recommendations" }),
  ).toBeTruthy();
});
