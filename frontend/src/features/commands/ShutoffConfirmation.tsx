import { Button } from "../../components/ui/Button";
import { Modal } from "../../components/ui/Modal";
import { formatPower } from "../dashboard/power";
import type { Approval } from "./useShutoffApproval";
export function ShutoffConfirmation({
  approval,
  busy,
  error,
  onClose,
  onApprove,
}: {
  approval: Approval;
  busy: boolean;
  error: string;
  onClose: () => void;
  onApprove: () => void;
}) {
  return (
    <Modal title="Turn off selected devices?" onClose={onClose}>
      <p>
        Turn off these devices in <strong>{approval.homeName}</strong>. Base
        Layer won’t turn them back on.
      </p>
      <ul className="confirm-list">
        {approval.devices.map((d) => (
          <li key={d.entityId}>
            {d.name}
            <strong>{formatPower(d.powerWatts)}</strong>
          </li>
        ))}
      </ul>
      <p className="muted">
        Unconfirmed shutoffs are retried, up to 4 attempts within 2 minutes.
        Check Recent activity for results.
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="modal-actions">
        <Button variant="secondary" disabled={busy} onClick={onClose}>
          Cancel
        </Button>
        <Button disabled={busy} onClick={onApprove}>
          {busy ? "Sending…" : "Approve & turn off"}
        </Button>
      </div>
    </Modal>
  );
}
