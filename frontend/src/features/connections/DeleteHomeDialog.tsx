import { useRef, useState } from "react";
import { Modal } from "../../components/ui/Modal";
import { Button } from "../../components/ui/Button";
import type { Home } from "../../lib/types";

export function DeleteHomeDialog({
  home,
  onDelete,
  onClose,
}: {
  home: Home;
  onDelete: (homeId: string) => Promise<void>;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const inFlight = useRef(false);
  async function remove() {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError("");
    try {
      await onDelete(home.id);
      onClose();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }
  return (
    <Modal
      title={`Delete ${home.name}?`}
      onClose={() => {
        if (!inFlight.current) onClose();
      }}
    >
      <p>
        Permanently delete <strong>{home.name}</strong>, its settings, and
        activity from Base Layer. This also disconnects the home and cancels
        pending commands.
      </p>
      <p>
        Home Assistant and your devices are kept. Shutoffs already sent cannot
        be undone. Adding this home again requires reconnecting.
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        <Button variant="secondary" disabled={busy} onClick={onClose}>
          Keep home
        </Button>
        <Button variant="danger" disabled={busy} onClick={() => void remove()}>
          {busy ? "Deleting…" : "Delete home"}
        </Button>
      </div>
    </Modal>
  );
}
