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
        This permanently removes <strong>{home.name}</strong>, its device
        settings, and command history from Base Layer. Pending commands are
        cancelled and the connection is revoked.
      </p>
      <p>
        Your devices and Home Assistant installation will remain. Shutoffs
        already sent cannot be undone. To add this home again, you will need to
        reconnect it.
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
          {busy ? "Deleting…" : "Yes, delete home"}
        </Button>
      </div>
    </Modal>
  );
}
