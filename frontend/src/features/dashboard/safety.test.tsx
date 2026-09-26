// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  renderHook,
  screen,
} from "@testing-library/react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { useShutoffApproval } from "../commands/useShutoffApproval";
import { DeviceSettings } from "../devices/DeviceSettings";
import { useHomes } from "./useHomes";
import { UsageOverview } from "./UsageOverview";

function home(): Home {
  return {
    id: "home-1",
    name: "Test home",
    connected: true,
    revoked: false,
    lastSeenUtc: null,
    householdWatts: 13000,
    limitWatts: 11000,
    projectedWatts: 8000,
    commands: [],
    baseUrl: "http://localhost:8123",
    householdPowerSensorId: null,
    powerSource: "wholeHouseMeter",
    allowFutureDevices: false,
    powerSensors: [],
    devices: ["dryer", "heater"].map((name) => ({
      entityId: `switch.${name}`,
      name,
      state: "on",
      powerWatts: 5000,
      allowed: true,
      recommended: true,
      powerSensorId: null,
    })),
  };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
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
});

describe("shutoff approval safety", () => {
  it("retains the same immutable operation across ambiguous network failures", async () => {
    const turnOff = vi
      .fn()
      .mockRejectedValueOnce(new Error("connection lost"))
      .mockResolvedValue([]);
    const api = { turnOff } as unknown as Api;
    const { result } = renderHook(() =>
      useShutoffApproval(api, home(), false, async () => {}),
    );
    act(() => result.current.setSelected(["switch.dryer"]));
    act(() => result.current.review());
    const key = result.current.approval!.idempotencyKey;
    act(() => result.current.setSelected(["switch.heater"]));
    await act(() => result.current.send());
    expect(result.current.error).toBe("connection lost");
    expect(result.current.approval?.idempotencyKey).toBe(key);
    await act(() => result.current.send());
    expect(turnOff.mock.calls).toEqual([
      ["home-1", ["switch.dryer"], key],
      ["home-1", ["switch.dryer"], key],
    ]);
    expect(result.current.approval).toBeNull();
  });

  it("blocks concurrent submissions and closing an in-flight approval", async () => {
    const pending = deferred<[]>();
    const turnOff = vi.fn(() => pending.promise);
    const { result } = renderHook(() =>
      useShutoffApproval(
        { turnOff } as unknown as Api,
        home(),
        false,
        async () => {},
      ),
    );
    act(() => result.current.setSelected(["switch.dryer"]));
    act(() => result.current.review());
    let request!: Promise<void>;
    act(() => {
      request = result.current.send();
      void result.current.send();
      result.current.close();
    });
    expect(turnOff).toHaveBeenCalledTimes(1);
    expect(result.current.approval).not.toBeNull();
    await act(async () => {
      pending.resolve([]);
      await request;
    });
    expect(result.current.busy).toBe(false);
  });

  it.each(["off", "permission", "offline", "stale"])(
    "discards selection and approval when %s, without reviving them",
    (reason) => {
      const original = home();
      const api = {} as Api;
      const { result, rerender } = renderHook(
        ({ current, stale }) =>
          useShutoffApproval(api, current, stale, async () => {}),
        { initialProps: { current: original, stale: false } },
      );
      act(() => result.current.setSelected(["switch.dryer"]));
      act(() => result.current.review());
      const next = structuredClone(original);
      if (reason === "off") next.devices[0].state = "off";
      if (reason === "permission") next.devices[0].allowed = false;
      if (reason === "offline") next.connected = false;
      rerender({ current: next, stale: reason === "stale" });
      expect(result.current.selected).toEqual([]);
      expect(result.current.approval).toBeNull();
      rerender({ current: original, stale: false });
      expect(result.current.selected).toEqual([]);
    },
  );
});

describe("device permissions", () => {
  it("suggests meters for review and saves device-sum mode separately from control access", async () => {
    const current = home();
    current.powerSensors = [
      { entityId: "sensor.dryer_power", name: "dryer power", unit: "W" },
      { entityId: "sensor.heater_power", name: "heater power", unit: "W" },
    ];
    current.devices.forEach((d) => {
      d.allowed = false;
    });
    const settings = vi.fn().mockResolvedValue(current);
    render(
      <DeviceSettings
        home={current}
        api={{ settings } as unknown as Api}
        onClose={() => {}}
        onSaved={() => {}}
      />,
    );
    expect(
      (
        screen.getByRole("combobox", {
          name: "dryer power sensor",
        }) as HTMLSelectElement
      ).value,
    ).toBe("sensor.dryer_power");
    expect(settings).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole("radio", { name: "Sum of device readings" }),
    );
    expect(
      screen.queryByRole("combobox", { name: "Whole-house power meter" }),
    ).toBeNull();
    fireEvent.change(
      screen.getByRole("combobox", { name: "heater power sensor" }),
      { target: { value: "" } },
    );
    await act(async () =>
      fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
    );
    expect(settings).toHaveBeenCalledWith(
      "home-1",
      expect.objectContaining({
        powerSource: "deviceSum",
        householdPowerSensorId: null,
        allowedEntityIds: [],
        devicePowerSensors: { "switch.dryer": "sensor.dryer_power" },
      }),
    );
  });

  it("reflects existing all access, clears it, and keeps future access separate", async () => {
    const settings = vi.fn().mockResolvedValue(home());
    render(
      <DeviceSettings
        home={home()}
        api={{ settings } as unknown as Api}
        onClose={() => {}}
        onSaved={() => {}}
      />,
    );
    const all = screen.getByRole("checkbox", {
      name: "Allow all current devices",
    }) as HTMLInputElement;
    expect(all.checked).toBe(true);
    fireEvent.click(all);
    expect(
      (screen.getByRole("checkbox", { name: "dryer" }) as HTMLInputElement)
        .checked,
    ).toBe(false);
    fireEvent.click(
      screen.getByRole("checkbox", {
        name: "Allow future devices",
      }),
    );
    await act(async () =>
      fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
    );
    expect(settings).toHaveBeenCalledWith(
      "home-1",
      expect.objectContaining({
        allowAll: false,
        allowedEntityIds: [],
        allowFutureDevices: true,
      }),
    );
  });

  it("shows partial access as indeterminate and saves explicit selections", async () => {
    const current = home();
    current.devices[1].allowed = false;
    const settings = vi.fn().mockResolvedValue(current);
    render(
      <DeviceSettings
        home={current}
        api={{ settings } as unknown as Api}
        onClose={() => {}}
        onSaved={() => {}}
      />,
    );
    const all = screen.getByRole("checkbox", {
      name: "Allow all current devices",
    }) as HTMLInputElement;
    expect(all.indeterminate).toBe(true);
    await act(async () =>
      fireEvent.click(screen.getByRole("button", { name: "Save settings" })),
    );
    expect(settings).toHaveBeenCalledWith(
      "home-1",
      expect.objectContaining({
        allowAll: false,
        allowedEntityIds: ["switch.dryer"],
        allowFutureDevices: false,
      }),
    );
  });
});

describe("poll freshness", () => {
  it("does not let an older poll overwrite a newer manual refresh", async () => {
    const old = deferred<Home[]>(),
      latest = deferred<Home[]>();
    const homes = vi
      .fn()
      .mockReturnValueOnce(old.promise)
      .mockReturnValueOnce(latest.promise);
    const api = { homes } as unknown as Api;
    const { result } = renderHook(() => useHomes(api));
    let refresh!: Promise<void>;
    act(() => {
      refresh = result.current.refresh();
    });
    const fresh = home();
    fresh.connected = false;
    await act(async () => {
      latest.resolve([fresh]);
      await refresh;
    });
    await act(async () => {
      old.resolve([home()]);
      await old.promise;
    });
    expect(result.current.homes[0].connected).toBe(false);
  });

  it("preserves a newer offline error when an earlier request succeeds", async () => {
    const old = deferred<Home[]>();
    const homes = vi
      .fn()
      .mockReturnValueOnce(old.promise)
      .mockRejectedValueOnce(new Error("Offline"));
    const api = { homes } as unknown as Api;
    const { result } = renderHook(() => useHomes(api));
    await act(() => result.current.refresh());
    await act(async () => {
      old.resolve([home()]);
      await old.promise;
    });
    expect(result.current.error).toBe("Offline");
    expect(result.current.homes).toEqual([]);
  });
});

it("never exposes unknown household usage as a measured zero", () => {
  const current = home();
  current.householdWatts = null;
  render(<UsageOverview home={current} />);
  const meter = screen.queryByRole("meter");
  expect(meter?.getAttribute("aria-valuenow") ?? null).toBeNull();
});
