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
import type { Device, Home } from "../../lib/types";
import { EvChargeControl } from "./EvChargeControl";
import { DeviceSettings } from "./DeviceSettings";
import { Dashboard } from "../dashboard/Dashboard";

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.open = true;
  };
  HTMLDialogElement.prototype.close = function () {
    this.open = false;
  };
});
afterEach(cleanup);
const device: Device = {
  entityId: "switch.ev",
  name: "EV charger",
  state: "on",
  allowed: true,
  shutoffLevel: "Anytime",
  recommended: false,
  powerWatts: 7680,
  powerSensorId: "sensor.ev",
  evCharging: { currentEntityId: "input_number.ev_current", wattsPerAmp: 240 },
  evCurrent: {
    entityId: "input_number.ev_current",
    name: "EV current",
    amps: 32,
    min: 6,
    max: 48,
    step: 1,
  },
};
const home: Home = {
  id: "home",
  name: "Home",
  connected: true,
  revoked: false,
  lastSeenUtc: new Date().toISOString(),
  householdWatts: 13680,
  limitWatts: 11000,
  projectedWatts: 13680,
  devices: [device],
  commands: [],
  baseUrl: "http://localhost:8123",
  householdPowerSensorId: "sensor.total",
  allowFutureDevices: false,
  powerSource: "wholeHouseMeter",
  smartPowerOffEnabled: true,
  powerSensors: [
    { entityId: "sensor.ev", name: "EV power", unit: "W" },
    { entityId: "sensor.total", name: "Total", unit: "W" },
  ],
  currentControls: [device.evCurrent!],
};
it("sends supported current and rejects out-of-range and fractional-step values", async () => {
  const set = vi.fn().mockResolvedValue(undefined);
  render(
    <EvChargeControl device={device} disabled={false} onSetCurrent={set} />,
  );
  expect(screen.queryByRole("spinbutton")).toBeNull();
  expect(screen.queryByText(/6–48/)).toBeNull();
  fireEvent.click(
    screen.getByRole("button", { name: "Edit EV charger charging current" }),
  );
  const input = screen.getByRole("spinbutton");
  const button = screen.getByRole("button", {
    name: "Save",
  }) as HTMLButtonElement;
  for (const value of ["5", "49", "16.5", ""]) {
    fireEvent.change(input, { target: { value } });
    expect(button.disabled).toBe(true);
    expect(input.getAttribute("aria-invalid")).toBe("true");
    fireEvent.keyDown(input, { key: "Enter" });
    fireEvent.click(button);
    expect(set).not.toHaveBeenCalled();
  }
  fireEvent.change(input, { target: { value: "20" } });
  await act(async () => fireEvent.click(button));
  expect(set).toHaveBeenCalledExactlyOnceWith(20);
  expect(screen.queryByRole("spinbutton")).toBeNull();
});
it.each([10, 40])("accepts the configured boundary of %s A", async (amps) => {
  const set = vi.fn().mockResolvedValue(undefined);
  render(
    <EvChargeControl
      device={{
        ...device,
        evCurrent: { ...device.evCurrent!, min: 10, max: 40 },
      }}
      disabled={false}
      onSetCurrent={set}
    />,
  );
  fireEvent.click(
    screen.getByRole("button", { name: "Edit EV charger charging current" }),
  );
  const input = screen.getByRole("spinbutton");
  const save = screen.getByRole("button", {
    name: "Save",
  }) as HTMLButtonElement;
  expect(input.getAttribute("min")).toBe("10");
  expect(input.getAttribute("max")).toBe("40");
  for (const value of ["9", "41"]) {
    fireEvent.change(input, { target: { value } });
    expect(save.disabled).toBe(true);
    fireEvent.keyDown(input, { key: "Enter" });
    expect(set).not.toHaveBeenCalled();
  }
  fireEvent.change(input, { target: { value: String(amps) } });
  expect(save.disabled).toBe(false);
  expect(input.getAttribute("aria-invalid")).toBe("false");
  await act(async () => fireEvent.keyDown(input, { key: "Enter" }));
  expect(set).toHaveBeenCalledExactlyOnceWith(amps);
});
it("disables unavailable controls and surfaces rejected requests", async () => {
  const set = vi.fn().mockRejectedValue(new Error("Home disconnected"));
  const view = render(
    <EvChargeControl device={device} disabled={false} onSetCurrent={set} />,
  );
  fireEvent.click(
    screen.getByRole("button", { name: "Edit EV charger charging current" }),
  );
  fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "20" } });
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Save" })),
  );
  expect(screen.getByRole("alert").textContent).toBe("Home disconnected");
  view.rerender(
    <EvChargeControl
      device={{ ...device, evCurrent: null }}
      disabled={false}
      onSetCurrent={set}
    />,
  );
  expect((screen.getByRole("spinbutton") as HTMLInputElement).disabled).toBe(
    true,
  );
});
it("cancels edits without sending a command and restores focus and the saved value", () => {
  const set = vi.fn();
  render(
    <EvChargeControl device={device} disabled={false} onSetCurrent={set} />,
  );
  const open = () =>
    fireEvent.click(
      screen.getByRole("button", { name: "Edit EV charger charging current" }),
    );
  open();
  expect(document.activeElement).toBe(screen.getByRole("spinbutton"));
  fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "20" } });
  fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("spinbutton")).toBeNull();
  expect(document.activeElement).toBe(
    screen.getByRole("button", { name: "Edit EV charger charging current" }),
  );
  open();
  expect((screen.getByRole("spinbutton") as HTMLInputElement).value).toBe("32");
  fireEvent.keyDown(screen.getByRole("spinbutton"), { key: "Escape" });
  expect(screen.queryByRole("spinbutton")).toBeNull();
  expect(set).not.toHaveBeenCalled();
});
it("saves EV pairing and charger rating with Anytime permission", async () => {
  const settings = vi.fn().mockResolvedValue(home);
  render(
    <DeviceSettings
      home={{ ...home, devices: [{ ...device, evCharging: null }] }}
      api={{ settings } as unknown as Api}
      onClose={vi.fn()}
      onSaved={vi.fn()}
    />,
  );
  fireEvent.change(
    screen.getByRole("combobox", { name: "EV charger EV current control" }),
    { target: { value: "input_number.ev_current" } },
  );
  fireEvent.change(
    screen.getByRole("spinbutton", {
      name: "EV charger charger watts per amp",
    }),
    { target: { value: "690" } },
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
  );
  expect(settings).toHaveBeenCalledWith(
    "home",
    expect.objectContaining({
      evCharging: {
        "switch.ev": {
          currentEntityId: "input_number.ev_current",
          wattsPerAmp: 690,
        },
      },
      shutoffLevels: { "switch.ev": "Anytime" },
    }),
  );
});
it("connects dashboard charging control to the API and refreshes state", async () => {
  const homes = vi.fn().mockResolvedValue([home]);
  const evCurrent = vi.fn().mockResolvedValue({});
  render(<Dashboard api={{ homes, evCurrent } as unknown as Api} />);
  await act(async () => {});
  fireEvent.click(
    screen.getByRole("button", { name: "Edit EV charger charging current" }),
  );
  fireEvent.change(
    screen.getByRole("spinbutton", { name: "EV charger charging current (A)" }),
    { target: { value: "20" } },
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Save" })),
  );
  expect(evCurrent).toHaveBeenCalledWith(
    "home",
    "switch.ev",
    20,
    expect.any(String),
  );
  expect(homes).toHaveBeenCalledTimes(2);
});

it("shows partial EV restoration, its original target, and a keep-current-rate action", async () => {
  const { RestoreQueue } = await import("./RestoreQueue");
  const queued: Home = {
    ...home,
    restoreQueue: [
      {
        entityId: device.entityId,
        name: device.name,
        estimatedWatts: 1200,
        queuedUtc: new Date().toISOString(),
        status: "waiting",
        currentAmps: 27,
        targetAmps: 32,
      },
    ],
  };
  const keepOff = vi.fn().mockResolvedValue({ ...home, restoreQueue: [] });
  const onSaved = vi.fn();
  render(
    <RestoreQueue
      home={queued}
      api={{ keepOff } as unknown as Api}
      onSaved={onSaved}
      stale={false}
    />,
  );
  expect(screen.getByText("Charging: 27 A → 32 A original limit")).toBeTruthy();
  expect(
    screen.getByText(/Demo timing: 15 seconds off, 5 seconds/),
  ).toBeTruthy();
  await act(async () =>
    fireEvent.click(
      screen.getByRole("button", { name: "Keep EV charger at current rate" }),
    ),
  );
  expect(keepOff).toHaveBeenCalledWith(home.id, device.entityId);
  expect(onSaved).toHaveBeenCalledWith(
    expect.objectContaining({ restoreQueue: [] }),
  );
});
