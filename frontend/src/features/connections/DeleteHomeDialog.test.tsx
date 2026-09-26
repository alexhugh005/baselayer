// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import { DeleteHomeDialog } from "./DeleteHomeDialog";
import { CommandHistory } from "../commands/CommandHistory";
import type { Home, Command } from "../../lib/types";
beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.open = true;
  };
  HTMLDialogElement.prototype.close = function () {
    this.open = false;
  };
});
afterEach(cleanup);
const home = { id: "home-1", name: "Test House" } as Home;
it("requires explicit confirmation and lets the user keep the home", () => {
  const remove = vi.fn(),
    close = vi.fn();
  render(<DeleteHomeDialog home={home} onDelete={remove} onClose={close} />);
  expect(
    screen.getByRole("dialog", { name: "Delete Test House?" }),
  ).toBeTruthy();
  expect(remove).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Keep home" }));
  expect(close).toHaveBeenCalledOnce();
  expect(remove).not.toHaveBeenCalled();
});
it("deletes the confirmed home once and blocks dismissal while deleting", async () => {
  let finish!: () => void;
  const remove = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    ),
    close = vi.fn();
  render(<DeleteHomeDialog home={home} onDelete={remove} onClose={close} />);
  fireEvent.click(screen.getByRole("button", { name: "Delete home" }));
  fireEvent.click(screen.getByRole("button", { name: "Deleting…" }));
  fireEvent.click(screen.getByRole("button", { name: "Close" }));
  const dialog = screen.getByRole("dialog");
  expect(fireEvent(dialog, new Event("cancel", { cancelable: true }))).toBe(
    false,
  );
  expect(close).not.toHaveBeenCalled();
  expect(remove).toHaveBeenCalledExactlyOnceWith("home-1");
  await act(async () => finish());
  expect(close).toHaveBeenCalledOnce();
});
it("keeps the confirmation open after a failed deletion", async () => {
  const close = vi.fn();
  render(
    <DeleteHomeDialog
      home={home}
      onDelete={vi.fn().mockRejectedValue(new Error("Offline"))}
      onClose={close}
    />,
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Delete home" })),
  );
  expect(screen.getByRole("alert").textContent).toBe("Offline");
  expect(close).not.toHaveBeenCalled();
});
it("only offers revocation for non-terminal commands and submits the selected command", async () => {
  const cancel = vi.fn().mockResolvedValue(undefined);
  const statuses = [
    "Pending",
    "Retrying",
    "AwaitingConfirmation",
    "Confirmed",
    "Failed",
    "Expired",
    "Cancelled",
  ];
  const commands: Command[] = statuses.map((status) => ({
    id: status,
    entityId: status,
    status,
    attempts: 1,
    message: null,
    createdUtc: new Date().toISOString(),
  }));
  render(<CommandHistory commands={commands} devices={[]} onCancel={cancel} />);
  expect(
    screen.getAllByRole("button", { name: /Cancel command/ }),
  ).toHaveLength(3);
  await act(async () =>
    fireEvent.click(
      screen.getByRole("button", { name: "Cancel command for Retrying" }),
    ),
  );
  expect(cancel).toHaveBeenCalledExactlyOnceWith("Retrying");
});
