// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import { useState } from "react";
import { VehicleManager } from "./VehicleManager";
import type { Device, Home, EvVehicle } from "../../lib/types";
import { EvDeparturePlanner } from "./EvCharging";
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
  localStorage.clear();
});
const device: Device = {
  entityId: "switch.ev",
  name: "My EV",
  state: "on",
  powerWatts: 7680,
  allowed: true,
  shutoffLevel: "Sometimes",
  recommended: false,
  powerSensorId: "sensor.ev",
  evCharging: { currentEntityId: "number.ev", wattsPerAmp: 240 },
  evCurrent: {
    entityId: "number.ev",
    name: "Current",
    amps: 32,
    min: 6,
    max: 48,
    step: 1,
  },
  evBattery: {
    sensorEntityId: "sensor.battery",
    capacityKwh: 75,
    efficiencyPercent: 90,
  },
};
const home: Home = {
  id: "home",
  name: "Home",
  connected: true,
  revoked: false,
  lastSeenUtc: new Date().toISOString(),
  householdWatts: 8000,
  limitWatts: 11000,
  projectedWatts: 8000,
  devices: [device],
  commands: [],
  baseUrl: "http://localhost",
  householdPowerSensorId: "sensor.total",
  allowFutureDevices: false,
  powerSensors: [],
  powerSource: "wholeHouseMeter",
  evBatterySensors: [
    { entityId: "sensor.battery", name: "EV battery", percent: 50 },
  ],
};
function setup(h = home, d = device) {
  const api = {
    evCurrent: vi.fn().mockResolvedValue({
      id: "command",
      entityId: d.entityId,
      status: "AwaitingConfirmation",
      currentAmps: 48,
    }),
    evBatterySettings: vi.fn().mockResolvedValue(h),
  } as unknown as Api;
  const props = {
    home: h,
    device: d,
    api,
    stale: false,
    refresh: vi.fn().mockResolvedValue(undefined),
    updateHome: vi.fn(),
  };
  const view = render(<EvDeparturePlanner {...props} />);
  const date = new Date(Date.now() + 60 * 60_000);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16);
  fireEvent.change(screen.getByLabelText(/Departure date/), {
    target: { value: local },
  });
  return { api, view, props };
}
it("applies maximum when time is short and waits for observed confirmation", async () => {
  const { api, view, props } = setup();
  expect(screen.getByText(/There isn’t enough time/)).toBeTruthy();
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Update charging" })),
  );
  expect(api.evCurrent).toHaveBeenCalledExactlyOnceWith(
    "home",
    "switch.ev",
    48,
    expect.any(String),
  );
  expect(screen.queryByText(/Current confirmed/)).toBeNull();
  view.rerender(
    <EvDeparturePlanner
      {...props}
      home={{
        ...home,
        commands: [
          {
            id: "command",
            entityId: "switch.ev",
            status: "Confirmed",
            currentAmps: 48,
            attempts: 1,
            createdUtc: new Date().toISOString(),
            message: "Observed 48 A",
          },
        ],
      }}
    />,
  );
  expect(screen.getByText("Current confirmed at 48 A")).toBeTruthy();
});
it("uses live battery updates for the estimate", () => {
  const { view, props } = setup();
  view.rerender(
    <EvDeparturePlanner
      {...props}
      home={{
        ...home,
        evBatterySensors: [
          { entityId: "sensor.battery", name: "EV battery", percent: 99 },
        ],
      }}
    />,
  );
  expect(screen.getByRole("button", { name: "Update charging" })).toBeTruthy();
  expect(screen.getByText("99%")).toBeTruthy();
});
it.each([
  "offline",
  "unavailable battery",
  "not allowed",
  "unavailable charger",
  "pending",
])("blocks changes when %s", (reason) => {
  const h = {
    ...home,
    connected: reason !== "offline",
    evBatterySensors:
      reason === "unavailable battery" ? [] : home.evBatterySensors,
    commands:
      reason === "pending"
        ? [
            {
              id: "pending",
              entityId: "switch.other",
              status: "Pending",
              attempts: 0,
              createdUtc: "",
              message: null,
            },
          ]
        : [],
  };
  const d = {
    ...device,
    allowed: reason !== "not allowed",
    state: reason === "unavailable charger" ? "unavailable" : "on",
  };
  const { api } = setup(h, d);
  const button = screen.queryByRole("button", {
    name: "Update charging",
  }) as HTMLButtonElement | null;
  expect(!button || button.disabled).toBe(true);
  expect(api.evCurrent).not.toHaveBeenCalled();
});
it("pairs a live battery sensor while the charger is off", async () => {
  const { api } = setup(home, {
    ...device,
    evBattery: null,
    state: "off",
    powerWatts: 0,
  });
  fireEvent.click(screen.getByRole("button", { name: "Add vehicle" }));
  const dialog = screen.getByRole("dialog", { name: "Add vehicle" });
  fireEvent.change(screen.getByLabelText("Battery level sensor"), {
    target: { value: "sensor.battery" },
  });
  fireEvent.change(screen.getByLabelText("Usable battery capacity (kWh)"), {
    target: { value: "75" },
  });
  await act(async () =>
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Add vehicle" }),
    ),
  );
  expect(api.evBatterySettings).toHaveBeenCalledExactlyOnceWith(
    "home",
    "switch.ev",
    {
      sensorEntityId: "sensor.battery",
      capacityKwh: 75,
      efficiencyPercent: 90,
    },
  );
});
it("surfaces a rejected command without claiming success", async () => {
  const { api } = setup();
  vi.mocked(api.evCurrent).mockRejectedValue(new Error("Charger disconnected"));
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Update charging" })),
  );
  expect(screen.getByRole("alert").textContent).toBe("Charger disconnected");
});

it("uses an automatically detected battery without requiring setup", () => {
  setup(home, {
    ...device,
    evBattery: { ...device.evBattery!, autoDetected: true },
  });
  expect(screen.queryByText(/Automatically detected/)).toBeNull();
  expect(screen.queryByText(/Applying sets the vehicle/)).toBeNull();
  expect(
    screen.queryByRole("region", { name: "Charging estimate" }),
  ).toBeNull();
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(
    (
      screen.getByRole("button", {
        name: "Update charging",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(false);
});
it("shows live battery info and asks only for missing capacity", () => {
  setup(home, {
    ...device,
    evBattery: { ...device.evBattery!, capacityKwh: null, autoDetected: true },
  });
  expect(screen.getByText("50%")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Edit vehicle" }));
  expect(screen.getByRole("dialog", { name: "Edit vehicle" })).toBeTruthy();
  expect(
    (screen.getByLabelText("Battery level sensor") as HTMLSelectElement).value,
  ).toBe("sensor.battery");
  expect(
    (screen.getByLabelText("Usable battery capacity (kWh)") as HTMLInputElement)
      .value,
  ).toBe("");
  expect(
    (
      screen.getByRole("button", {
        name: "Update charging",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
});
it("uses automatic detection when it becomes available during polling", () => {
  const { view, props } = setup(home, { ...device, evBattery: null });
  view.rerender(
    <EvDeparturePlanner
      {...props}
      device={{
        ...device,
        evBattery: { ...device.evBattery!, autoDetected: true },
      }}
    />,
  );
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(
    (
      screen.getByRole("button", {
        name: "Update charging",
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(false);
});

it.each([0, 3, null])(
  "starts charging while off with %s W after submitting the planned current",
  async (powerWatts) => {
    const { api } = setup(home, { ...device, state: "off", powerWatts });
    expect(screen.getByText("50%")).toBeTruthy();
    const apply = screen.getByRole("button", {
      name: "Start charge",
    }) as HTMLButtonElement;
    expect(apply.disabled).toBe(false);
    await act(async () => fireEvent.click(apply));
    expect(api.evCurrent).toHaveBeenCalledExactlyOnceWith(
      "home",
      "switch.ev",
      48,
      expect.any(String),
      undefined,
      undefined,
      true,
    );
  },
);

const limitControl = {
  entityId: "input_number.ev_target",
  name: "EV charge target",
  percent: 80,
  min: 50,
  max: 100,
  step: 1,
};
const limitedHome = {
  ...home,
  evBatterySensors: [
    { ...home.evBatterySensors![0], chargeLimitControl: limitControl },
  ],
};
it("uses the vehicle limit initially and sends an edited limit with the current", async () => {
  const { api } = setup(limitedHome);
  const input = screen.getByLabelText(/^Charge limit/) as HTMLInputElement;
  expect(input.value).toBe("80");
  fireEvent.change(input, { target: { value: "85" } });
  expect(screen.getByText("85%")).toBeTruthy();
  expect(screen.getByText("85%")).toBeTruthy();
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Update charging" })),
  );
  expect(api.evCurrent).toHaveBeenCalledExactlyOnceWith(
    "home",
    "switch.ev",
    48,
    expect.any(String),
    85,
  );
  expect(localStorage.getItem("ev-charge-limit:home:switch.ev")).toBe("85");
});
it("can apply a lower stopping limit when the battery has already reached it", async () => {
  const { api } = setup({
    ...limitedHome,
    evBatterySensors: [{ ...limitedHome.evBatterySensors[0], percent: 90 }],
  });
  expect(screen.getByText(/Charge target reached/)).toBeTruthy();
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Apply 80% limit" })),
  );
  expect(api.evCurrent).toHaveBeenCalledExactlyOnceWith(
    "home",
    "switch.ev",
    32,
    expect.any(String),
    80,
  );
});
it("keeps the slider within the detected vehicle range without applying automatically", () => {
  const { api } = setup(limitedHome);
  fireEvent.change(screen.getByLabelText(/^Charge limit/), {
    target: { value: "49" },
  });
  const slider = screen.getByRole("slider", {
    name: "Charge limit (%)",
  }) as HTMLInputElement;
  expect(slider.value).toBe("50");
  expect(slider.min).toBe("50");
  expect(slider.max).toBe("100");
  expect(slider.step).toBe("1");
  expect(api.evCurrent).not.toHaveBeenCalled();
});

it("adds and edits several vehicles sharing one charger without replacing the first", async () => {
  const first = {
    id: "first",
    name: "Family car",
    chargerEntityId: device.entityId,
    battery: device.evBattery!,
  };
  const api = {
    saveEvVehicle: vi.fn(async (_id: string, vehicle: EvVehicle) => ({
      ...home,
      evVehicles: [first, vehicle],
    })),
  } as unknown as Api;
  function Harness() {
    const [current, setCurrent] = useState<Home>({
      ...home,
      evVehicles: [first],
    });
    return (
      <VehicleManager
        home={current}
        api={api}
        stale={false}
        refresh={async () => {}}
        updateHome={setCurrent}
      />
    );
  }
  render(<Harness />);
  fireEvent.click(screen.getByRole("button", { name: "Add vehicle" }));
  let dialog = within(screen.getByRole("dialog"));
  fireEvent.change(dialog.getByLabelText("Vehicle name"), {
    target: { value: "Commuter" },
  });
  fireEvent.change(dialog.getByLabelText(/Usable battery capacity/), {
    target: { value: "60" },
  });
  await act(async () =>
    fireEvent.click(dialog.getByRole("button", { name: "Add vehicle" })),
  );
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(screen.getByRole("heading", { name: "Commuter" })).toBeTruthy();
  expect(screen.getByRole("option", { name: "Family car" })).toBeTruthy();
  const saved = vi.mocked(api.saveEvVehicle).mock.calls[0][1];
  expect(saved.chargerEntityId).toBe(device.entityId);
  expect(saved.battery.capacityKwh).toBe(60);
  fireEvent.click(screen.getByRole("button", { name: "Edit vehicle" }));
  dialog = within(screen.getByRole("dialog"));
  fireEvent.change(dialog.getByLabelText("Vehicle name"), {
    target: { value: "Daily car" },
  });
  await act(async () =>
    fireEvent.click(dialog.getByRole("button", { name: "Save changes" })),
  );
  expect(vi.mocked(api.saveEvVehicle).mock.calls[1][1].id).toBe(saved.id);
  expect(screen.getByRole("heading", { name: "Daily car" })).toBeTruthy();
  expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
    "Family car",
    "Daily car",
  ]);
});

it("keeps each vehicle's departure and target and applies the selected profile", async () => {
  const first = {
    id: "first",
    name: "Family car",
    chargerEntityId: device.entityId,
    battery: device.evBattery!,
  };
  const second = {
    ...first,
    id: "second",
    name: "Commuter",
    battery: { ...first.battery, capacityKwh: 60 },
  };
  const api = {
    evCurrent: vi.fn().mockResolvedValue({ id: "command", status: "Pending" }),
  } as unknown as Api;
  render(
    <VehicleManager
      home={{ ...limitedHome, evVehicles: [first, second] }}
      api={api}
      stale={false}
      refresh={async () => {}}
      updateHome={vi.fn()}
    />,
  );
  fireEvent.change(screen.getByLabelText(/^Charge limit/), {
    target: { value: "85" },
  });
  const date = new Date(Date.now() + 24 * 60 * 60_000);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16);
  fireEvent.change(screen.getByLabelText(/Departure date/), {
    target: { value: local },
  });
  fireEvent.change(screen.getByLabelText("Vehicle"), {
    target: { value: "second" },
  });
  expect(
    (screen.getByLabelText(/^Charge limit/) as HTMLInputElement).value,
  ).toBe("80");
  fireEvent.change(screen.getByLabelText(/^Charge limit/), {
    target: { value: "90" },
  });
  fireEvent.change(screen.getByLabelText("Vehicle"), {
    target: { value: "first" },
  });
  expect(
    (screen.getByLabelText(/^Charge limit/) as HTMLInputElement).value,
  ).toBe("85");
  expect(
    (screen.getByLabelText(/Departure date/) as HTMLInputElement).value,
  ).toBe(local);
  fireEvent.change(screen.getByLabelText("Vehicle"), {
    target: { value: "second" },
  });
  expect(
    (screen.getByLabelText(/^Charge limit/) as HTMLInputElement).value,
  ).toBe("90");
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Update charging" })),
  );
  expect(api.evCurrent).toHaveBeenCalledWith(
    "home",
    "switch.ev",
    expect.any(Number),
    expect.any(String),
    90,
    "second",
  );
});

it("cancels a new vehicle without saving and retains draft during polling", () => {
  const first = {
    id: "first",
    name: "Family car",
    chargerEntityId: device.entityId,
    battery: device.evBattery!,
  };
  const api = { saveEvVehicle: vi.fn() } as unknown as Api;
  const props = {
    home: { ...home, evVehicles: [first] },
    api,
    stale: false,
    refresh: async () => {},
    updateHome: vi.fn(),
  };
  const view = render(<VehicleManager {...props} />);
  fireEvent.click(screen.getByRole("button", { name: "Add vehicle" }));
  fireEvent.change(screen.getByLabelText("Vehicle name"), {
    target: { value: "Draft car" },
  });
  view.rerender(
    <VehicleManager
      {...props}
      home={{ ...props.home, householdWatts: 9000 }}
    />,
  );
  expect(
    (screen.getByLabelText("Vehicle name") as HTMLInputElement).value,
  ).toBe("Draft car");
  fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(api.saveEvVehicle).not.toHaveBeenCalled();
});
