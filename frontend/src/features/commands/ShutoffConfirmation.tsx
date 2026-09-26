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
        You’re approving shutoff of these devices in{" "}
        <strong>{approval.homeName}</strong>. They will stay off until you turn
        them on again.
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
        We’ll verify the result in Home Assistant and retry unconfirmed commands
        up to 4 attempts, with backoff. Approval expires after 2 minutes. If a
        device becomes ineligible, this review closes automatically.
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
