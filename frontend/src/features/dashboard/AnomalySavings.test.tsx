// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home, UsageAnomaly } from "../../lib/types";
import { AnomalySavingsSetting, AnomalySavingsWidget } from "./AnomalySavings";
import { UsageAlerts } from "./UsageAlerts";

const anomaly: UsageAnomaly = {
  id: "a1",
  eventId: "e1",
  entityId: "switch.dryer",
  usualWatts: 1000,
  observedWatts: 5000,
  differenceWatts: 4000,
  recommendedAction: "Off",
  detectedUtc: new Date().toISOString(),
  status: "NotificationOnly",
  message: "Recommendation only.",
  commandId: null,
  estimatedSavedKwh: 0,
};
const home = {
  id: "home",
  name: "Home",
  connected: true,
  revoked: false,
  devices: [{ entityId: "switch.dryer", name: "Dryer", shutoffLevel: "Never" }],
  commands: [],
  anomalies: [anomaly],
  anomalySavingsEnabled: false,
  anomalySavings: {
    estimatedSavedKwh: 4,
    confirmedActions: 1,
    estimatedRatePerKwh: 0.16,
    assumedUndetectedMinutes: 60,
  },
} as unknown as Home;
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  localStorage.clear();
});

it("persists the setting and retains the old value if saving fails", async () => {
  const save = vi
    .fn()
    .mockRejectedValueOnce(new Error("Could not save"))
    .mockResolvedValueOnce({ ...home, anomalySavingsEnabled: true });
  const onSaved = vi.fn();
  render(
    <AnomalySavingsSetting
      home={home}
      api={{ anomalySavings: save } as unknown as Api}
      stale={false}
      onSaved={onSaved}
    />,
  );
  const toggle = screen.getByRole("switch");
  await act(async () => fireEvent.click(toggle));
  expect(screen.getByRole("alert").textContent).toBe("Could not save");
  expect(toggle.getAttribute("aria-checked")).toBe("false");
  expect(onSaved).not.toHaveBeenCalled();
  await act(async () => fireEvent.click(toggle));
  expect(save).toHaveBeenLastCalledWith("home", true);
  expect(onSaved).toHaveBeenCalledWith(
    expect.objectContaining({ anomalySavingsEnabled: true }),
  );
});

it("shows only the lifetime dollar savings total", () => {
  render(<AnomalySavingsWidget home={home} />);
  expect(screen.getByText("$0.64")).toBeTruthy();
  expect(screen.getByText("LIFETIME SAVINGS")).toBeTruthy();
  expect(
    screen.queryByText(/kWh|confirmed automatic|Detection|How we estimate/),
  ).toBeNull();
});

it("delivers anomaly browser notifications once across rerenders and remounts", () => {
  const send = vi.fn();
  vi.stubGlobal("isSecureContext", true);
  vi.stubGlobal(
    "Notification",
    class {
      static permission = "granted";
      constructor(title: string, options: unknown) {
        send(title, options);
      }
    },
  );
  localStorage.setItem("base-layer-notifications", "true");
  const view = render(
    <UsageAlerts homes={[home]} stale={false} onSelect={vi.fn()} />,
  );
  expect(send).toHaveBeenCalledTimes(1);
  view.rerender(
    <UsageAlerts homes={[{ ...home }]} stale={false} onSelect={vi.fn()} />,
  );
  view.unmount();
  render(<UsageAlerts homes={[home]} stale={false} onSelect={vi.fn()} />);
  expect(send).toHaveBeenCalledTimes(1);
  expect(send).toHaveBeenCalledWith(
    "Home: unusual device usage",
    expect.objectContaining({
      body: expect.stringContaining("Recommended action: turn it off"),
    }),
  );
});
