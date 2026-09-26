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
