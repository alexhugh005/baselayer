// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { DeviceSettings } from "./DeviceSettings";

HTMLDialogElement.prototype.showModal = function () {
  this.setAttribute("open", "");
};
HTMLDialogElement.prototype.close = function () {
  this.removeAttribute("open");
};
afterEach(cleanup);
const home: Home = {
  id: "home",
  name: "Home",
  connected: true,
  revoked: false,
  lastSeenUtc: null,
  householdWatts: 1200,
  limitWatts: 11000,
  projectedWatts: 1200,
  commands: [],
  baseUrl: "http://home.local",
  householdPowerSensorId: null,
  allowFutureDevices: false,
  powerSource: "deviceSum",
  powerSensors: [
    { entityId: "sensor.toaster", name: "Toaster power", unit: "W" },
  ],
  deviceCategories: [
    { id: "toaster", name: "Toaster", standardWatts: 1200 },
    { id: "custom", name: "Other / custom", standardWatts: null },
  ],
  devices: [
    {
      entityId: "switch.toaster",
      name: "Toaster",
      state: "on",
      powerWatts: 1200,
      allowed: true,
      recommended: false,
      powerSensorId: "sensor.toaster",
      shutoffLevel: "Sometimes",
    },
  ],
};
function setup(value = home) {
  const save = vi.fn().mockResolvedValue(value);
  render(
    <DeviceSettings
      home={value}
      api={{ settings: save } as unknown as Api}
      onClose={vi.fn()}
      onSaved={vi.fn()}
    />,
  );
  return save;
}
it("uses category estimates, previews the 50% trigger, and saves overrides", async () => {
  const save = setup();
  fireEvent.change(screen.getByLabelText("Toaster category"), {
    target: { value: "toaster" },
  });
  expect(
    screen.getByText(/Category estimate: 1,200 W. Action at 1,800 W or more/),
  ).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Toaster standard power (W)"), {
    target: { value: "1400" },
  });
  expect(
    screen.getByText(/Custom standard: 1,400 W. Action at 2,100 W or more/),
  ).toBeTruthy();
  fireEvent.click(screen.getByText("Save settings"));
  await waitFor(() =>
    expect(save).toHaveBeenCalledWith(
      "home",
      expect.objectContaining({
        devicePowerStandards: {
          "switch.toaster": { category: "toaster", standardWatts: 1400 },
        },
      }),
    ),
  );
});
it("requires valid custom watts and blocks an empty or zero standard", async () => {
  const save = setup();
  fireEvent.change(screen.getByLabelText("Toaster category"), {
    target: { value: "custom" },
  });
  fireEvent.click(screen.getByText("Save settings"));
  expect(save).not.toHaveBeenCalled();
  expect(screen.getByRole("alert").textContent).toContain(
    "Other / custom requires a value",
  );
  fireEvent.change(screen.getByLabelText("Toaster standard power (W)"), {
    target: { value: "0" },
  });
  fireEvent.click(screen.getByText("Save settings"));
  expect(save).not.toHaveBeenCalled();
  fireEvent.change(screen.getByLabelText("Toaster standard power (W)"), {
    target: { value: "100" },
  });
  fireEvent.click(screen.getByText("Save settings"));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
});
it("saves null for category defaults and allows clearing an assigned category", async () => {
  const save = setup({
    ...home,
    devices: [{ ...home.devices[0], category: "toaster", standardWatts: 1200 }],
  });
  expect(screen.getByText(/Category estimate: 1,200 W/)).toBeTruthy();
  fireEvent.click(screen.getByText("Save settings"));
  await waitFor(() =>
    expect(save).toHaveBeenCalledWith(
      "home",
      expect.objectContaining({
        devicePowerStandards: {
          "switch.toaster": { category: "toaster", standardWatts: null },
        },
      }),
    ),
  );
  fireEvent.change(screen.getByLabelText("Toaster category"), {
    target: { value: "" },
  });
  fireEvent.click(screen.getByText("Save settings"));
  await waitFor(() =>
    expect(save).toHaveBeenLastCalledWith(
      "home",
      expect.objectContaining({
        devicePowerStandards: { "switch.toaster": null },
      }),
    ),
  );
});
