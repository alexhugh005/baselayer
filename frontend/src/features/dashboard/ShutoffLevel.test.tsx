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
import type { Home, HomeSettings } from "../../lib/types";
import { Dashboard } from "./Dashboard";

afterEach(cleanup);

function setup(overrides: Partial<Home> = {}) {
  let home: Home = {
    id: "home",
    name: "Home",
    connected: true,
    revoked: false,
    lastSeenUtc: new Date().toISOString(),
    householdWatts: 100,
    limitWatts: 11000,
    gridOutageRisk: "medium",
    projectedWatts: 100,
    commands: [],
    baseUrl: "http://home.local",
    powerSource: "deviceSum",
    householdPowerSensorId: null,
    allowFutureDevices: true,
    smartPowerOffEnabled: true,
    powerSensors: [
      { entityId: "sensor.dryer", name: "Dryer power", unit: "W" },
    ],
    devices: [
      {
        entityId: "switch.dryer",
        name: "Dryer",
        state: "on",
        powerWatts: 100,
        powerSensorId: "sensor.dryer",
        allowed: true,
        recommended: false,
        shutoffLevel: "Sometimes",
      },
      {
        entityId: "climate.room",
        name: "Thermostat",
        state: "heat",
        powerWatts: null,
        powerSensorId: null,
        allowed: false,
        recommended: false,
        shutoffLevel: "Never",
      },
    ],
    ...overrides,
  };
  const settings = vi.fn(async (_id: string, data: HomeSettings) => {
    home = {
      ...home,
      devices: home.devices.map((d) => ({
        ...d,
        allowed: data.allowedEntityIds.includes(d.entityId),
        shutoffLevel: data.shutoffLevels?.[d.entityId] ?? d.shutoffLevel,
      })),
    };
    return home;
  });
  render(
    <Dashboard
      api={{ homes: vi.fn(async () => [home]), settings } as unknown as Api}
    />,
  );
  return settings;
}

const control = (name = "Dryer") =>
  screen.getByRole("button", { name: new RegExp(`^${name} Smart Shutoff:`) });

it("saves the full cycle without selecting the device or losing sensor settings", async () => {
  const settings = setup();
  await screen.findByRole("button", { name: "Dryer Smart Shutoff: Sometimes" });
  for (const level of ["Never", "Anytime", "Sometimes"]) {
    await act(async () => fireEvent.click(control()));
    expect(control().textContent).toBe(level);
    expect(settings).toHaveBeenLastCalledWith("home", {
      allowAll: false,
      allowFutureDevices: true,
      allowedEntityIds: level === "Never" ? [] : ["switch.dryer"],
      shutoffLevels: { "switch.dryer": level },
      powerSource: "deviceSum",
      householdPowerSensorId: null,
      devicePowerSensors: { "switch.dryer": "sensor.dryer" },
    });
    expect(
      (
        screen.getByRole("checkbox", {
          name: "Select Dryer",
        }) as HTMLInputElement
      ).checked,
    ).toBe(false);
  }
  expect(settings).toHaveBeenCalledTimes(3);
  expect(screen.queryByRole("dialog")).toBeNull();
  fireEvent.click(screen.getByText("Dryer"));
  expect(
    (screen.getByRole("checkbox", { name: "Select Dryer" }) as HTMLInputElement)
      .checked,
  ).toBe(true);
  await act(async () => fireEvent.click(control()));
  expect(
    (screen.getByRole("checkbox", { name: "Select Dryer" }) as HTMLInputElement)
      .checked,
  ).toBe(false);
});

it("retains the saved level after a failure and allows retry", async () => {
  const settings = setup();
  await screen.findByRole("button", { name: "Dryer Smart Shutoff: Sometimes" });
  settings.mockRejectedValueOnce(new Error("Could not save permission."));
  await act(async () => fireEvent.click(control()));
  expect(screen.getByRole("alert").textContent).toContain(
    "Could not save permission.",
  );
  expect(control().textContent).toBe("Sometimes");
  await act(async () => fireEvent.click(control()));
  expect(control().textContent).toBe("Never");
});

it("disables permission changes while a save is pending", async () => {
  const settings = setup();
  await screen.findByRole("button", { name: "Dryer Smart Shutoff: Sometimes" });
  let fail!: (error: Error) => void;
  settings.mockImplementationOnce(
    () =>
      new Promise((_resolve, reject) => {
        fail = reject;
      }),
  );
  fireEvent.click(control());
  expect((control() as HTMLButtonElement).disabled).toBe(true);
  expect((control("Thermostat") as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(control());
  expect(settings).toHaveBeenCalledTimes(1);
  await act(async () => fail(new Error("Try again.")));
  expect((control() as HTMLButtonElement).disabled).toBe(false);
});

it("skips automatic shutoff for thermostats", async () => {
  const settings = setup();
  await screen.findByRole("button", {
    name: "Thermostat Smart Shutoff: Never",
  });
  await act(async () => fireEvent.click(control("Thermostat")));
  expect(control("Thermostat").textContent).toBe("Sometimes");
  await act(async () => fireEvent.click(control("Thermostat")));
  expect(control("Thermostat").textContent).toBe("Never");
  expect(settings).toHaveBeenCalledTimes(2);
});

it("disables changes while disconnected", async () => {
  const settings = setup({ connected: false });
  await screen.findByRole("button", { name: "Dryer Smart Shutoff: Sometimes" });
  expect((control() as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(control());
  expect(settings).not.toHaveBeenCalled();
});
