import { useState } from "react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { Button } from "../../components/ui/Button";
import { formatPower } from "../dashboard/power";

export function RestoreQueue({
  home,
  api,
  onSaved,
  stale,
}: {
  home: Home;
  api: Api;
  onSaved: (home: Home) => void;
  stale: boolean;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState("");
  const queue = home.restoreQueue ?? [];
  if (!queue.length) return null;
  async function keepOff(entityId: string) {
    setBusy(entityId);
    setError("");
    try {
      onSaved(await api.keepOff(home.id, entityId));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return (
    <section className="card restore-queue" aria-label="Restore queue">
      <div className="section-heading">
        <h2>
          Restore queue <span className="count">{queue.length}</span>
        </h2>
      </div>
      <p className="muted">
        Devices turned off or EV charging reduced by Base Layer. Restores happen
        one at a time within the power limit. EVs increase as far as capacity
        allows and stay queued until their original current is restored.
      </p>
      <p className="muted">
        Demo timing: 15 seconds off, 5 seconds of stable spare capacity, and 5
        seconds between restorations. Removing a device stops future restore
        attempts; an already-sent request cannot be undone.
      </p>
      {(!home.smartPowerOffEnabled || stale || !home.connected) && (
        <p role="status">
          Restoration paused —{" "}
          {!home.smartPowerOffEnabled
            ? "enable Smart Shutoff in Device settings to resume."
            : "waiting for fresh home readings."}
        </p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {queue.map((entry) => (
        <div className="restore-row" key={entry.entityId}>
          <div>
            <strong>{entry.name}</strong>
            {entry.targetAmps != null && (
              <p className="muted">
                Charging: {entry.currentAmps ?? "Unknown"} A →{" "}
                {entry.targetAmps} A original limit
              </p>
            )}
            <p className="muted">
              {entry.estimatedWatts === null
                ? "Usage estimate unavailable — automatic restore paused"
                : `Estimated ${formatPower(entry.estimatedWatts)}`}{" "}
              ·{" "}
              {entry.status === "held"
                ? "Kept off by your Smart Usage plan"
                : entry.status === "restoring"
                  ? entry.targetAmps != null
                    ? "Restoring charging and checking usage"
                    : "Turning on and checking usage"
                  : entry.status === "failed"
                    ? "Restore failed — check the device in Home Assistant"
                    : entry.status === "paused"
                      ? "Paused"
                      : "Waiting for spare capacity"}
            </p>
          </div>
          <Button
            variant="secondary"
            disabled={busy !== null || stale}
            aria-label={
              entry.targetAmps != null
                ? `Keep ${entry.name} at current rate`
                : `Keep ${entry.name} off`
            }
            onClick={() => void keepOff(entry.entityId)}
          >
            {busy === entry.entityId
              ? "Removing…"
              : entry.targetAmps != null
                ? "Keep current rate"
                : "Keep off"}
          </Button>
        </div>
      ))}
    </section>
  );
}
