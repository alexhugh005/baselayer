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
import type { Device, Home } from "../../lib/types";
import { Dashboard } from "../dashboard/Dashboard";

afterEach(cleanup);

const device = (name: string, overrides: Partial<Device> = {}): Device => ({
  entityId: `switch.${name.toLowerCase()}`,
  name,
  state: "on",
  powerWatts: null,
  powerSensorId: null,
  allowed: true,
  shutoffLevel: "Sometimes",
  recommended: false,
  ...overrides,
});
const home: Home = {
  id: "home",
  name: "Home",
  connected: true,
  revoked: false,
  lastSeenUtc: new Date().toISOString(),
  householdWatts: 100,
  limitWatts: 5000,
  projectedWatts: 100,
  baseUrl: "http://home.local",
  commands: [],
  allowFutureDevices: true,
  smartPowerOffEnabled: true,
  powerSource: "wholeHouseMeter",
  householdPowerSensorId: "sensor.house",
  devices: [
    device("Dryer"),
    device("Washer", { allowed: false, shutoffLevel: "Never" }),
    device("Fan", { powerSensorId: "sensor.manual", shutoffLevel: "Anytime" }),
    device("Unknown"),
  ],
  powerSensors: [
    { entityId: "sensor.dryer_power", name: "Dryer Power", unit: "W" },
    { entityId: "sensor.washer_power", name: "Washer Power", unit: "W" },
    { entityId: "sensor.manual", name: "Fan Power", unit: "W" },
    { entityId: "sensor.house", name: "House Power", unit: "W" },
  ],
};
const connectButton = () =>
  screen.getByRole("button", {
    name: "Connect Suggest Source for Dryer",
  });

it("connects only the clicked suggestion without opening settings or selecting the device", async () => {
  let finish!: (home: Home) => void;
  const settings = vi.fn(
    () =>
      new Promise<Home>((resolve) => {
        finish = resolve;
      }),
  );
  render(
    <Dashboard
      api={
        { homes: vi.fn().mockResolvedValue([home]), settings } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  expect(
    screen.getAllByRole("button", { name: /Connect Suggest Source/ }),
  ).toHaveLength(2);
  fireEvent.click(connectButton());
  expect(connectButton().textContent).toBe("Connecting…");
  expect((connectButton() as HTMLButtonElement).disabled).toBe(true);
  expect(
    (
      screen.getByRole("button", {
        name: "Connect Suggest Source for Washer",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
  expect(
    (screen.getByRole("checkbox", { name: "Select Dryer" }) as HTMLInputElement)
      .checked,
  ).toBe(false);
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(settings).toHaveBeenCalledExactlyOnceWith("home", {
    allowAll: false,
    allowFutureDevices: true,
    allowedEntityIds: ["switch.dryer", "switch.fan", "switch.unknown"],
    powerSource: "wholeHouseMeter",
    householdPowerSensorId: "sensor.house",
    devicePowerSensors: {
      "switch.fan": "sensor.manual",
      "switch.dryer": "sensor.dryer_power",
    },
  });
  await act(async () =>
    finish({
      ...home,
      devices: home.devices.map((d) =>
        d.entityId === "switch.dryer"
          ? { ...d, powerSensorId: "sensor.dryer_power", powerWatts: 2500 }
          : d,
      ),
    }),
  );
  expect(
    screen.queryByRole("button", {
      name: "Connect Suggest Source for Dryer",
    }),
  ).toBeNull();
  expect(screen.getByText("2.50 kW")).toBeTruthy();
  expect(
    (
      screen.getByRole("button", {
        name: "Connect Suggest Source for Washer",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(false);
  fireEvent.click(screen.getByText("Dryer"));
  expect(
    (screen.getByRole("checkbox", { name: "Select Dryer" }) as HTMLInputElement)
      .checked,
  ).toBe(true);
});

it("shows save errors and leaves the suggestion available to retry", async () => {
  const settings = vi
    .fn()
    .mockRejectedValue(new Error("Could not connect the sensor."));
  render(
    <Dashboard
      api={
        { homes: vi.fn().mockResolvedValue([home]), settings } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  await act(async () => fireEvent.click(connectButton()));
  expect(screen.getByRole("alert").textContent).toContain(
    "Could not connect the sensor.",
  );
  expect((connectButton() as HTMLButtonElement).disabled).toBe(false);
  await act(async () => fireEvent.click(connectButton()));
  expect(settings).toHaveBeenCalledTimes(2);
});

it("disables connecting while the home is disconnected", async () => {
  const settings = vi.fn();
  render(
    <Dashboard
      api={
        {
          homes: vi.fn().mockResolvedValue([{ ...home, connected: false }]),
          settings,
        } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  expect((connectButton() as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(connectButton());
  expect(settings).not.toHaveBeenCalled();
});
