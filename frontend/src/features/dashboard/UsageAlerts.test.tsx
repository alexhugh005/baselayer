// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import type { Home } from "../../lib/types";
import { UsageAlerts, isUsageHigh } from "./UsageAlerts";

const home = (values: Partial<Home> = {}) =>
  ({
    id: "one",
    name: "My home",
    connected: true,
    revoked: false,
    householdWatts: 11000,
    limitWatts: 11000,
    gridOutageRisk: "medium",
    powerSource: "wholeHouseMeter",
    ...values,
  }) as Home;
afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

function notifications(permission = "granted") {
  const send = vi.fn();
  class MockNotification {
    static permission = permission;
    static requestPermission = vi.fn(async () => permission);
    constructor(title: string, options: NotificationOptions) {
      send(title, options);
    }
  }
  vi.stubGlobal("isSecureContext", true);
  vi.stubGlobal("Notification", MockNotification);
  return { send, request: MockNotification.requestPermission };
}

async function enable() {
  await act(async () => {
    fireEvent.click(
      screen.getByRole("switch", { name: "Browser notifications" }),
    );
  });
}

describe("usage alerts", () => {
  it("keeps browser notifications without rendering duplicate dashboard banners", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const { container } = render(
      <UsageAlerts
        homes={[home(), home({ id: "two", name: "Other home" })]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    expect(container.innerHTML).toBe("");
    expect(send).toHaveBeenCalledTimes(2);
  });

  it.each([
    { householdWatts: null },
    { householdWatts: NaN },
    { householdWatts: 10999 },
    { connected: false },
    { revoked: true },
    { limitWatts: 0 },
  ])("ignores invalid or below-limit readings: %s", (values) => {
    expect(isUsageHigh(home(values))).toBe(false);
  });

  it("keeps controls off the dashboard and restores the saved preference after navigation", async () => {
    const { request } = notifications();
    const select = vi.fn();
    const settings = render(
      <UsageAlerts settingsPage homes={[]} stale={false} onSelect={select} />,
    );
    expect(screen.queryByRole("alert")).toBeNull();
    await enable();
    expect(localStorage.getItem("base-layer-notifications")).toBe("true");
    settings.unmount();
    const dashboard = render(
      <UsageAlerts homes={[home()]} stale={false} onSelect={select} />,
    );
    expect(screen.queryByRole("switch")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
    dashboard.unmount();
    render(
      <UsageAlerts settingsPage homes={[]} stale={false} onSelect={select} />,
    );
    expect(screen.getByRole("switch").getAttribute("aria-checked")).toBe(
      "true",
    );
    fireEvent.click(screen.getByRole("switch"));
    expect(localStorage.getItem("base-layer-notifications")).toBe("false");
    expect(request).toHaveBeenCalledTimes(1);
  });

  it("requires opt-in, deduplicates polls, preserves events across unknown data, and rearms below the limit", async () => {
    const { send, request } = notifications();
    const select = vi.fn();
    const { rerender } = render(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={select}
      />,
    );
    expect(request).not.toHaveBeenCalled();
    expect(send).not.toHaveBeenCalled();
    await enable();
    expect(send).toHaveBeenCalledTimes(1);
    for (const values of [
      { householdWatts: 12000 },
      { householdWatts: null },
      { connected: false },
      {},
    ]) {
      rerender(
        <UsageAlerts
          settingsPage
          homes={[home(values)]}
          stale={false}
          onSelect={select}
        />,
      );
    }
    expect(send).toHaveBeenCalledTimes(1);
    rerender(
      <UsageAlerts
        settingsPage
        homes={[home({ householdWatts: 10000 })]}
        stale={false}
        onSelect={select}
      />,
    );
    rerender(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={select}
      />,
    );
    expect(send).toHaveBeenCalledTimes(2);
    fireEvent.click(
      screen.getByRole("switch", { name: "Browser notifications" }),
    );
    rerender(
      <UsageAlerts
        settingsPage
        homes={[home({ householdWatts: 10000 })]}
        stale={false}
        onSelect={select}
      />,
    );
    rerender(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={select}
      />,
    );
    expect(send).toHaveBeenCalledTimes(2);
  });

  it("suppresses stale readings until a successful refresh", async () => {
    const { send } = notifications();
    const select = vi.fn();
    const { rerender } = render(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={true}
        onSelect={select}
      />,
    );
    await enable();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(send).not.toHaveBeenCalled();
    rerender(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={select}
      />,
    );
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("falls back to in-site alerts if the browser cannot display notifications", async () => {
    notifications();
    class FailingNotification {
      static permission = "granted";
      static requestPermission = async () => "granted";
      constructor() {
        throw new Error("Unsupported constructor");
      }
    }
    vi.stubGlobal("Notification", FailingNotification);
    render(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    await enable();
    expect(screen.getByRole("status").textContent).toContain("could not show");
    expect(screen.getByRole("switch").getAttribute("aria-checked")).toBe(
      "false",
    );
  });

  it("keeps in-site alerts when permission is denied", async () => {
    const { send } = notifications("denied");
    render(
      <UsageAlerts
        settingsPage
        homes={[home()]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    await enable();
    expect(screen.getByRole("status").textContent).toContain("blocked");
    expect(screen.getByRole("switch").getAttribute("aria-checked")).toBe(
      "false",
    );
    expect(send).not.toHaveBeenCalled();
  });
});

describe("Smart Shutoff notifications", () => {
  const automaticHome = (status = "Confirmed") =>
    home({
      householdWatts: 10000,
      devices: [
        { entityId: "switch.dryer", name: "Dryer" } as Home["devices"][number],
      ],
      commands: [
        {
          id: "auto-1",
          entityId: "switch.dryer",
          automatic: true,
          status,
          attempts: 1,
          createdUtc: new Date().toISOString(),
          message: null,
        },
      ],
    });
  it("notifies only after confirmation, even below the limit, and deduplicates across navigation", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const select = vi.fn();
    const view = render(
      <UsageAlerts
        homes={[automaticHome("AwaitingConfirmation")]}
        stale={false}
        onSelect={select}
      />,
    );
    expect(send).not.toHaveBeenCalled();
    view.rerender(
      <UsageAlerts homes={[automaticHome()]} stale={false} onSelect={select} />,
    );
    expect(send).toHaveBeenCalledExactlyOnceWith(
      "My home: Smart Shutoff",
      expect.objectContaining({ body: "Turned off Dryer to reduce usage." }),
    );
    view.rerender(
      <UsageAlerts homes={[automaticHome()]} stale={false} onSelect={select} />,
    );
    view.unmount();
    render(
      <UsageAlerts homes={[automaticHome()]} stale={false} onSelect={select} />,
    );
    expect(send).toHaveBeenCalledTimes(1);
  });
  it("does not send when notifications are off or for failed commands", () => {
    const { send } = notifications();
    const view = render(
      <UsageAlerts
        homes={[automaticHome()]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    expect(send).not.toHaveBeenCalled();
    view.unmount();
    localStorage.setItem("base-layer-notifications", "true");
    render(
      <UsageAlerts
        homes={[automaticHome("Failed")]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    expect(send).not.toHaveBeenCalled();
  });
  it("does not misreport a confirmed restoration as a shutoff", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const current = automaticHome();
    current.commands[0].action = "On";
    render(<UsageAlerts homes={[current]} stale={false} onSelect={() => {}} />);
    expect(send).not.toHaveBeenCalled();
  });
  it("waits for fresh data and includes the need for additional shutoffs", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const current = {
      ...automaticHome(),
      smartPowerOffStatus: "insufficient" as const,
    };
    const view = render(
      <UsageAlerts homes={[current]} stale={true} onSelect={() => {}} />,
    );
    expect(send).not.toHaveBeenCalled();
    view.rerender(
      <UsageAlerts homes={[current]} stale={false} onSelect={() => {}} />,
    );
    expect(send).toHaveBeenCalledWith(
      "My home: Smart Shutoff",
      expect.objectContaining({
        body: expect.stringContaining("More devices need to be turned off"),
      }),
    );
  });
});

describe("grid outage risk alerts", () => {
  const device = (id: string, name: string) =>
    ({
      entityId: id,
      name,
      recommended: true,
      shutoffLevel: "Sometimes",
    }) as Home["devices"][number];

  it("allows over-limit usage without notifications at low risk", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const current = home({ gridOutageRisk: "low", householdWatts: 23000 });
    expect(isUsageHigh(current)).toBe(false);
    render(<UsageAlerts homes={[current]} stale={false} onSelect={() => {}} />);
    expect(send).not.toHaveBeenCalled();
  });

  it("waits for reductions and names every needed Sometimes device, updating when the list changes", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const current = home({
      smartPowerOffEnabled: true,
      smartPowerOffStatus: "reducing",
      devices: [],
    });
    const view = render(
      <UsageAlerts homes={[current]} stale={false} onSelect={() => {}} />,
    );
    expect(send).not.toHaveBeenCalled();
    const review = {
      ...current,
      smartPowerOffStatus: "review" as const,
      devices: [device("switch.dryer", "Dryer"), device("switch.oven", "Oven")],
    };
    view.rerender(
      <UsageAlerts homes={[review]} stale={false} onSelect={() => {}} />,
    );
    expect(send).toHaveBeenCalledExactlyOnceWith(
      "My home: action needed",
      expect.objectContaining({
        body: expect.stringContaining("Turn off Dryer, Oven."),
      }),
    );
    view.rerender(
      <UsageAlerts homes={[review]} stale={false} onSelect={() => {}} />,
    );
    expect(send).toHaveBeenCalledTimes(1);
    view.rerender(
      <UsageAlerts
        homes={[
          {
            ...review,
            smartPowerOffStatus: "insufficient",
            devices: [...review.devices, device("switch.pool", "Pool")],
          },
        ]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    expect(send).toHaveBeenCalledTimes(2);
    expect(send.mock.calls[1][1].body).toContain("Turn off Dryer, Oven, Pool.");
    expect(send.mock.calls[1][1].body).toContain("Additional appliances");
    view.rerender(
      <UsageAlerts
        homes={[{ ...review, gridOutageRisk: "low" }]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    view.rerender(
      <UsageAlerts homes={[review]} stale={false} onSelect={() => {}} />,
    );
    expect(send).toHaveBeenCalledTimes(3);
  });

  it("describes EV current reductions and does not misreport current restoration as a shutoff", () => {
    const { send } = notifications();
    localStorage.setItem("base-layer-notifications", "true");
    const current = home({
      householdWatts: 10000,
      devices: [device("switch.ev", "EV")],
      commands: [
        {
          id: "current",
          entityId: "switch.ev",
          action: "SetCurrent",
          currentAmps: 20,
          automatic: true,
          status: "Confirmed",
          attempts: 1,
          createdUtc: new Date().toISOString(),
          message: null,
        },
      ],
    });
    const view = render(
      <UsageAlerts homes={[current]} stale={false} onSelect={() => {}} />,
    );
    expect(send).toHaveBeenCalledExactlyOnceWith(
      "My home: Smart Shutoff",
      expect.objectContaining({
        body: "Reduced EV charging to 20 A to reduce usage.",
      }),
    );
    view.rerender(
      <UsageAlerts
        homes={[
          {
            ...current,
            commands: [
              {
                ...current.commands[0],
                id: "restore",
                currentAmps: 32,
                isRestoration: true,
              },
            ],
          },
        ]}
        stale={false}
        onSelect={() => {}}
      />,
    );
    expect(send).toHaveBeenCalledTimes(1);
  });
});
