import { Clock3, CheckCircle2 } from "lucide-react";
import type { Command, Device } from "../../lib/types";
import { Badge } from "../../components/ui/Badge";
import { Button } from "../../components/ui/Button";
import { useRef, useState } from "react";
export function CommandHistory({
  commands,
  devices,
  onCancel,
}: {
  commands: Command[];
  devices: Device[];
  onCancel: (commandId: string) => Promise<void>;
}) {
  const [cancelling, setCancelling] = useState<string | null>(null);
  const [error, setError] = useState("");
  const inFlight = useRef(false);
  async function cancel(id: string) {
    if (inFlight.current) return;
    inFlight.current = true;
    setCancelling(id);
    setError("");
    try {
      await onCancel(id);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      inFlight.current = false;
      setCancelling(null);
    }
  }
  return (
    <section className="card history">
      <div className="section-heading">
        <div>
          <h2>Recent activity</h2>
        </div>
        <Clock3 size={20} />
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {commands.some((c) =>
        ["Pending", "AwaitingConfirmation", "Retrying"].includes(c.status),
      ) && (
        <p className="muted">
          Cancel stops retries. Shutoffs already sent cannot be undone.
        </p>
      )}
      {commands.length === 0 ? (
        <p className="muted">No activity yet.</p>
      ) : (
        commands.map((c) => (
          <div className="activity-row" key={c.id}>
            <CheckCircle2 size={18} />
            <div>
              <strong>
                {devices.find((d) => d.entityId === c.entityId)?.name ??
                  c.entityId}
              </strong>
              <p>{c.message ?? "Waiting for shutoff confirmation."}</p>
              <small>
                {new Date(c.createdUtc).toLocaleTimeString([], {
                  hour: "2-digit",
                  minute: "2-digit",
                })}{" "}
                · {c.attempts} attempt{c.attempts === 1 ? "" : "s"}
              </small>
            </div>
            <div className="command-actions">
              <Badge
                tone={
                  c.status === "Confirmed"
                    ? "green"
                    : ["Pending", "AwaitingConfirmation", "Retrying"].includes(
                          c.status,
                        )
                      ? "amber"
                      : "neutral"
                }
              >
                {c.status === "AwaitingConfirmation" ? "Verifying" : c.status}
              </Badge>
              {["Pending", "AwaitingConfirmation", "Retrying"].includes(
                c.status,
              ) && (
                <Button
                  variant="secondary"
                  disabled={cancelling !== null}
                  onClick={() => void cancel(c.id)}
                  aria-label={`Cancel command for ${devices.find((d) => d.entityId === c.entityId)?.name ?? c.entityId}`}
                >
                  {cancelling === c.id ? "Cancelling…" : "Cancel command"}
                </Button>
              )}
            </div>
          </div>
        ))
      )}
    </section>
  );
}
