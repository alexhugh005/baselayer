// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { smartPowerOffMessage } from "./smartPowerOffMessage";
import { DeviceSettings } from "../devices/DeviceSettings";
import { RestoreQueue } from "../devices/RestoreQueue";
import { Dashboard } from "./Dashboard";

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute("open");
  };
});
afterEach(() => {
  cleanup();
  window.history.replaceState({}, "", "/");
});
const home = {
  id: "one",
  name: "My home",
  smartPowerOffEnabled: false,
  devices: [],
  revoked: false,
  powerSource: "wholeHouseMeter",
  powerSensors: [],
  householdPowerSensorId: null,
  allowFutureDevices: false,
  connected: true,
  limitWatts: 11000,
  householdWatts: null,
  projectedWatts: null,
  commands: [],
} as unknown as Home;
it("saves Smart Shutoff with device settings and explains load management from the info icon", async () => {
  const saved = { ...home, smartPowerOffEnabled: true };
  const settings = vi.fn().mockResolvedValue(saved);
  const onSaved = vi.fn();
  render(
    <DeviceSettings
      home={home}
      api={{ settings } as unknown as Api}
      onSaved={onSaved}
      onClose={vi.fn()}
    />,
  );
  expect(screen.queryByText(/Smart Shutoff allows devices/)).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "About Smart Shutoff" }));
  expect(
    screen.getByText(/Smart Shutoff allows devices/).textContent,
  ).toContain("battery's power limit");
  fireEvent.click(
    screen.getByRole("switch", {
      name: "Enable Smart Shutoff and automatic restore",
    }),
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
  );
  expect(settings).toHaveBeenCalledWith(
    "one",
    expect.objectContaining({ smartPowerOffEnabled: true }),
  );
  expect(onSaved).toHaveBeenCalledWith(saved);
});
it("shares the saved Smart Shutoff value between Settings and Device settings", async () => {
  window.history.replaceState({}, "", "/settings");
  const saved = { ...home, smartPowerOffEnabled: true };
  const smartPowerOff = vi.fn().mockResolvedValue(saved);
  const settings = vi.fn().mockResolvedValue(home);
  const api = {
    homes: vi.fn().mockResolvedValue([home]),
    smartPowerOff,
    settings,
  } as unknown as Api;
  const view = render(<Dashboard api={api} />);
  await act(async () => {});
  expect(screen.getByRole("heading", { name: "Notifications" })).toBeTruthy();
  const toggle = screen.getByRole("switch", {
    name: /Smart Shutoff and automatic restore for My home/,
  });
  expect(toggle.getAttribute("aria-checked")).toBe("false");
  await act(async () => fireEvent.click(toggle));
  expect(smartPowerOff).toHaveBeenCalledExactlyOnceWith("one", true);
  expect(toggle.getAttribute("aria-checked")).toBe("true");

  window.history.replaceState({}, "", "/");
  view.rerender(<Dashboard api={api} />);
  fireEvent.click(screen.getByRole("button", { name: "Device settings" }));
  const deviceToggle = screen.getByRole("switch", {
    name: "Enable Smart Shutoff and automatic restore",
  }) as HTMLInputElement;
  expect(deviceToggle.checked).toBe(true);
  fireEvent.click(deviceToggle);
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
  );
  expect(settings).toHaveBeenCalledWith(
    "one",
    expect.objectContaining({ smartPowerOffEnabled: false }),
  );

  window.history.replaceState({}, "", "/settings");
  view.rerender(<Dashboard api={api} />);
  expect(
    screen
      .getByRole("switch", {
        name: /Smart Shutoff and automatic restore for My home/,
      })
      .getAttribute("aria-checked"),
  ).toBe("false");
});
it("keeps the saved value and shows an error when a Settings toggle fails", async () => {
  window.history.replaceState({}, "", "/settings");
  render(
    <Dashboard
      api={
        {
          homes: vi.fn().mockResolvedValue([home]),
          smartPowerOff: vi
            .fn()
            .mockRejectedValue(new Error("Could not save Smart Shutoff.")),
        } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  const toggle = screen.getByRole("switch", {
    name: /Smart Shutoff and automatic restore for My home/,
  });
  await act(async () => fireEvent.click(toggle));
  expect(toggle.getAttribute("aria-checked")).toBe("false");
  expect(screen.getByRole("alert").textContent).toBe(
    "Could not save Smart Shutoff.",
  );
});
it("shows queued power estimates and lets users keep a device off", async () => {
  const queued = {
    ...home,
    restoreQueue: [
      {
        entityId: "switch.dryer",
        name: "Dryer",
        estimatedWatts: 5000,
        queuedUtc: new Date().toISOString(),
        status: "paused" as const,
      },
    ],
  };
  const keepOff = vi.fn().mockResolvedValue(home);
  const onSaved = vi.fn();
  render(
    <RestoreQueue
      home={queued}
      api={{ keepOff } as unknown as Api}
      onSaved={onSaved}
      stale={false}
    />,
  );
  expect(screen.getByText(/Estimated 5.00 kW/)).toBeTruthy();
  expect(screen.getByRole("status").textContent).toContain(
    "Restoration paused",
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Keep Dryer off" })),
  );
  expect(keepOff).toHaveBeenCalledExactlyOnceWith("one", "switch.dryer");
  expect(onSaved).toHaveBeenCalledWith(home);
});
it("explains approval and insufficient capacity without claiming battery recovery", () => {
  expect(
    smartPowerOffMessage({ ...home, smartPowerOffStatus: "review" }),
  ).toContain("approve");
  expect(
    smartPowerOffMessage({ ...home, smartPowerOffStatus: "insufficient" }),
  ).toContain("Turn off additional appliances yourself");
});
