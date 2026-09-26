// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home, SmartUsagePlan } from "../../lib/types";
import { AppShell } from "../../app/AppShell";
import { UsagePlanner } from "./SmartUsage";
import { duration, localEndTime, runtimeHours } from "./runtime";

const home = {
  id: "home-one",
  name: "Home",
  connected: true,
  revoked: false,
  devices: [],
  commands: [],
} as unknown as Home;
const initial: SmartUsagePlan = {
  homeId: home.id,
  battery: {
    homeId: home.id,
    capacityKwh: 13.5,
    storedEnergyKwh: 10,
    stateOfChargePercent: 74,
    observedAtUtc: "2026-09-26T23:00:00Z",
    isSimulated: true,
  },
  calculatedAtUtc: "2026-09-26T23:00:00Z",
  currentWatts: 5000,
  allOnWatts: 12000,
  minimumWatts: 1000,
  targetWatts: 5000,
  projectedWatts: 5000,
  currentHours: 2,
  shortestHours: 10 / 12,
  longestHours: 10,
  projectedHours: 2,
  limitWatts: 11000,
  changes: [],
  excludedDevices: [],
  canApply: true,
  blockedReason: null,
  revision: "initial",
};
beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  window.history.replaceState({}, "", "/");
});
async function tick(ms = 250) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}
function setup(overrides: Partial<SmartUsagePlan> = {}) {
  const smartUsage = vi
    .fn()
    .mockImplementation(async (_id: string, target?: number) => ({
      ...initial,
      ...overrides,
      targetWatts: target ?? 5000,
    }));
  const applySmartUsage = vi.fn().mockResolvedValue([]);
  const api = { smartUsage, applySmartUsage } as unknown as Api;
  const refreshHome = vi.fn().mockResolvedValue(undefined);
  render(
    <UsagePlanner
      home={home}
      api={api}
      stale={false}
      refreshHome={refreshHome}
    />,
  );
  return { smartUsage, applySmartUsage, api, refreshHome };
}
it("adds an accessible lightbulb navigation entry in the sidebar", () => {
  window.history.replaceState({}, "", "/smart-usage");
  render(
    <AppShell account={null}>
      <div>Page</div>
    </AppShell>,
  );
  expect(
    screen.getByRole("link", { name: "Smart Usage" }).getAttribute("href"),
  ).toBe("/smart-usage");
  expect(
    screen
      .getByRole("link", { name: "Smart Usage" })
      .getAttribute("aria-current"),
  ).toBe("page");
});
it("previews slider changes without switching devices and confirms the reviewed revision", async () => {
  const { smartUsage, applySmartUsage } = setup();
  await tick();
  smartUsage.mockResolvedValueOnce({
    ...initial,
    targetWatts: 1000,
    projectedWatts: 1000,
    projectedHours: 10,
    revision: "longer",
    changes: [
      {
        entityId: "switch.dryer",
        name: "Dryer",
        action: "Off",
        estimatedWatts: 4000,
        shutoffLevel: "Sometimes",
      },
    ],
  });
  fireEvent.change(screen.getByRole("slider", { name: "Battery runtime" }), {
    target: { value: "1000" },
  });
  expect(
    (screen.getByRole("button", { name: "Confirm plan" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  expect(applySmartUsage).not.toHaveBeenCalled();
  await tick();
  expect(screen.getByText("Dryer")).toBeTruthy();
  expect(screen.getByText("Turn off")).toBeTruthy();
  expect(screen.getAllByText(/Sep 27/).length).toBeGreaterThan(0);
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Confirm plan" })),
  );
  expect(applySmartUsage).toHaveBeenCalledWith(
    home.id,
    1000,
    "longer",
    expect.any(String),
  );
});
it("blocks plans above the battery power limit", async () => {
  setup({
    canApply: false,
    blockedReason: "This selection exceeds the 11 kW limit per battery.",
    projectedWatts: 12000,
  });
  await tick();
  expect(screen.getByRole("alert").textContent).toContain("11 kW");
  expect(
    (screen.getByRole("button", { name: "Confirm plan" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
});
it("keeps controls blocked after a preview refresh fails", async () => {
  const { smartUsage } = setup();
  await tick();
  smartUsage.mockRejectedValueOnce(new Error("Battery telemetry unavailable"));
  await tick(3000);
  expect(screen.getByRole("alert").textContent).toContain(
    "Battery telemetry unavailable",
  );
  expect(
    (screen.getByRole("button", { name: "Confirm plan" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
});
it("ignores an older slider response after a newer selection", async () => {
  const { smartUsage } = setup();
  await tick();
  let resolveOlder!: (value: SmartUsagePlan) => void;
  smartUsage.mockImplementationOnce(
    () =>
      new Promise<SmartUsagePlan>((resolve) => {
        resolveOlder = resolve;
      }),
  );
  fireEvent.change(screen.getByRole("slider"), { target: { value: "900" } });
  await tick();
  smartUsage.mockResolvedValueOnce({
    ...initial,
    targetWatts: 1000,
    revision: "newer",
    changes: [],
  });
  fireEvent.change(screen.getByRole("slider"), { target: { value: "1000" } });
  await tick();
  await act(async () =>
    resolveOlder({
      ...initial,
      revision: "older",
      changes: [
        {
          entityId: "old",
          name: "Old device",
          action: "Off",
          estimatedWatts: 1,
          shutoffLevel: "Anytime",
        },
      ],
    }),
  );
  expect(screen.queryByText("Old device")).toBeNull();
});
it("shows turn-on plans and pending confirmation separately from success", async () => {
  const { applySmartUsage } = setup({
    changes: [
      {
        entityId: "switch.lamp",
        name: "Lamp",
        action: "On",
        estimatedWatts: 500,
        shutoffLevel: "Sometimes",
      },
    ],
  });
  applySmartUsage.mockResolvedValue([
    {
      id: "command",
      entityId: "switch.lamp",
      action: "On",
      status: "Pending",
      attempts: 0,
      message: null,
      createdUtc: initial.calculatedAtUtc,
    },
  ]);
  await tick();
  expect(screen.getByText("Turn on")).toBeTruthy();
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Confirm plan" })),
  );
  expect(
    screen.getByRole("heading", { name: "Applying your plan" }),
  ).toBeTruthy();
  expect(screen.queryByRole("heading", { name: "Plan confirmed" })).toBeNull();
});
it("handles zero load without invalid dates and formats local dates across midnight", () => {
  expect(runtimeHours(10, 0)).toBeNull();
  expect(runtimeHours(0, 0)).toBe(0);
  expect(duration(null)).toBe("No finite limit");
  expect(localEndTime(initial.calculatedAtUtc, null)).toContain(
    "zero measured load",
  );
  const expected = new Intl.DateTimeFormat(undefined, {
    weekday: "short",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    timeZoneName: "short",
  }).format(new Date("2026-09-27T09:00:00Z"));
  expect(localEndTime(initial.calculatedAtUtc, 10)).toBe(expected);
});
