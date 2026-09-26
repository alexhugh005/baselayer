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
import type { Command, Home } from "../../lib/types";
import { CircuitPanel, SmartPanel } from "./SmartPanel";

afterEach(cleanup);
const entityId = "switch.span_panel_living_room_breaker";
const home = {
  id: "one",
  name: "Energy Lab",
  connected: true,
  revoked: false,
  commands: [],
  devices: [
    {
      entityId,
      name: "SPAN Panel Living Room Breaker",
      state: "on",
      isCircuit: true,
      allowed: false,
      circuitPriority: {
        entityId: "select.span_panel_living_room_circuit_priority",
        priority: "off_grid",
        options: ["never", "soc_threshold", "off_grid"],
      },
    },
    { entityId: "switch.lamp", name: "Lamp", state: "on", isCircuit: false },
  ],
} as unknown as Home;
const command = {
  id: "cmd",
  entityId,
  action: "Off",
  status: "Pending",
  createdUtc: new Date().toISOString(),
  attempts: 0,
  message: null,
} as Command;

it("sends explicit on/off commands and keeps the observed state until confirmed", async () => {
  const circuitCommand = vi.fn().mockResolvedValue(command);
  const api = { circuitCommand } as unknown as Api;
  const refresh = vi.fn().mockResolvedValue(undefined);
  const { rerender } = render(
    <CircuitPanel home={home} api={api} stale={false} refresh={refresh} />,
  );
  expect(screen.getAllByRole("switch")).toHaveLength(1);
  const toggle = screen.getByRole("switch", { name: "Living Room circuit" });
  await act(async () => fireEvent.click(toggle));
  expect(circuitCommand).toHaveBeenCalledWith(
    "one",
    entityId,
    "Off",
    expect.any(String),
  );
  expect(toggle.getAttribute("aria-checked")).toBe("true");
  expect((toggle as HTMLButtonElement).disabled).toBe(true);
  expect(
    screen.getByRole("status", { name: "Turning Living Room off" }).textContent,
  ).toBe("");
  const offHome = {
    ...home,
    devices: [{ ...home.devices[0], state: "off" }],
    commands: [{ ...command, status: "Confirmed" }],
  };
  rerender(
    <CircuitPanel home={offHome} api={api} stale={false} refresh={refresh} />,
  );
  expect(toggle.getAttribute("aria-checked")).toBe("false");
  expect((toggle as HTMLButtonElement).disabled).toBe(false);
  expect(screen.queryByRole("status")).toBeNull();
  circuitCommand.mockResolvedValue({ ...command, id: "on", action: "On" });
  await act(async () => fireEvent.click(toggle));
  expect(circuitCommand).toHaveBeenLastCalledWith(
    "one",
    entityId,
    "On",
    expect.any(String),
  );
});

it("saves the native outage setting with a spinner until the panel confirms it", async () => {
  let resolve!: (command: Command) => void;
  const circuitPriority = vi.fn().mockImplementation(
    () =>
      new Promise<Command>((done) => {
        resolve = done;
      }),
  );
  const api = { circuitPriority, circuitCommand: vi.fn() } as unknown as Api;
  const refresh = vi.fn().mockResolvedValue(undefined);
  const { rerender } = render(
    <CircuitPanel home={home} api={api} stale={false} refresh={refresh} />,
  );
  const select = screen.getByRole("combobox", {
    name: "Living Room grid outage setting",
  }) as HTMLSelectElement;
  fireEvent.change(select, { target: { value: "never" } });
  expect(circuitPriority).toHaveBeenCalledWith(
    "one",
    entityId,
    "never",
    expect.any(String),
  );
  expect(select.disabled).toBe(true);
  expect(
    screen.getByRole("status", {
      name: "Updating Living Room grid outage setting",
    }).textContent,
  ).toBe("");
  const pending = {
    ...command,
    action: "SetCircuitPriority",
    circuitPriority: "never",
  } as Command;
  await act(async () => resolve(pending));
  expect(select.value).toBe("off_grid");
  expect(select.disabled).toBe(true);
  expect(screen.queryByText("Battery backup warning")).toBeNull();
  expect((screen.getByRole("switch") as HTMLButtonElement).disabled).toBe(true);
  rerender(
    <CircuitPanel
      home={{
        ...home,
        devices: [
          {
            ...home.devices[0],
            circuitPriority: {
              ...home.devices[0].circuitPriority!,
              priority: "never",
            },
          },
        ],
        commands: [{ ...pending, status: "Confirmed" }],
      }}
      api={api}
      stale={false}
      refresh={refresh}
    />,
  );
  expect(select.value).toBe("never");
  expect(select.disabled).toBe(false);
  expect(screen.getByRole("alert").textContent).toContain(
    "could prevent Base Layer from ensuring the battery kicks in during an outage",
  );
  expect(screen.queryByRole("status")).toBeNull();
  expect(api.circuitCommand).not.toHaveBeenCalled();
});

it("shows priority failures, preserves the observed value, and retries the same request", async () => {
  const circuitPriority = vi
    .fn()
    .mockRejectedValue(new Error("Unable to save outage setting"));
  render(
    <CircuitPanel
      home={home}
      api={{ circuitPriority } as unknown as Api}
      stale={false}
      refresh={vi.fn()}
    />,
  );
  const select = screen.getByRole("combobox") as HTMLSelectElement;
  await act(async () =>
    fireEvent.change(select, { target: { value: "soc_threshold" } }),
  );
  expect(select.value).toBe("off_grid");
  expect(select.disabled).toBe(false);
  expect(screen.getByRole("alert").textContent).toContain(
    "Unable to save outage setting",
  );
  await act(async () =>
    fireEvent.change(select, { target: { value: "soc_threshold" } }),
  );
  expect(circuitPriority.mock.calls[1]).toEqual(circuitPriority.mock.calls[0]);
});

it.each(["offline", "stale", "revoked", "missing", "unavailable"])(
  "disables outage settings when %s",
  (condition) => {
    const changed: Home = {
      ...home,
      connected: condition !== "offline",
      revoked: condition === "revoked",
      devices: [
        {
          ...home.devices[0],
          circuitPriority:
            condition === "missing"
              ? null
              : {
                  ...home.devices[0].circuitPriority!,
                  priority: condition === "unavailable" ? null : "off_grid",
                },
        },
      ],
    };
    render(
      <CircuitPanel
        home={changed}
        api={{} as Api}
        stale={condition === "stale"}
        refresh={vi.fn()}
      />,
    );
    expect((screen.getByRole("combobox") as HTMLSelectElement).disabled).toBe(
      true,
    );
  },
);

it.each(["offline", "stale", "revoked", "unavailable"])(
  "disables controls with %s readings",
  (condition) => {
    const changed = {
      ...home,
      connected: condition !== "offline",
      revoked: condition === "revoked",
      devices: [
        {
          ...home.devices[0],
          state: condition === "unavailable" ? "unavailable" : "on",
        },
      ],
    };
    const circuitCommand = vi.fn();
    render(
      <CircuitPanel
        home={changed}
        api={{ circuitCommand } as unknown as Api}
        stale={condition === "stale"}
        refresh={vi.fn()}
      />,
    );
    const toggle = screen.getByRole("switch");
    expect((toggle as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(toggle);
    expect(circuitCommand).not.toHaveBeenCalled();
    expect(screen.getByText("Unavailable")).toBeTruthy();
  },
);

it("shows a rejected request and reuses its idempotency key on retry", async () => {
  const circuitCommand = vi
    .fn()
    .mockRejectedValue(new Error("Home Assistant unavailable"));
  render(
    <CircuitPanel
      home={home}
      api={{ circuitCommand } as unknown as Api}
      stale={false}
      refresh={vi.fn()}
    />,
  );
  await act(async () => fireEvent.click(screen.getByRole("switch")));
  expect(screen.getByRole("alert").textContent).toBe(
    "Home Assistant unavailable",
  );
  await act(async () => fireEvent.click(screen.getByRole("switch")));
  expect(circuitCommand.mock.calls[0]).toEqual(circuitCommand.mock.calls[1]);
});

it("shows terminal command failure and allows a fresh attempt", () => {
  render(
    <CircuitPanel
      home={{
        ...home,
        commands: [
          {
            ...command,
            status: "Failed",
            message: "No confirmation received.",
          },
        ],
      }}
      api={{} as Api}
      stale={false}
      refresh={vi.fn()}
    />,
  );
  expect(screen.getByRole("alert").textContent).toContain("No confirmation");
  expect((screen.getByRole("switch") as HTMLButtonElement).disabled).toBe(
    false,
  );
});

it("shows an empty panel and supports switching homes", async () => {
  const other = { ...home, id: "two", name: "Other home", devices: [] };
  render(
    <SmartPanel
      api={
        { homes: vi.fn().mockResolvedValue([home, other]) } as unknown as Api
      }
    />,
  );
  await act(async () => {});
  fireEvent.change(screen.getByRole("combobox", { name: "Selected home" }), {
    target: { value: "two" },
  });
  expect(
    screen.getByRole("heading", { name: "No panel circuits found" }),
  ).toBeTruthy();
  expect(screen.queryByRole("switch")).toBeNull();
});
