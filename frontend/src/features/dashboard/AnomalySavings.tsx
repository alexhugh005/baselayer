import { useId, useRef, useState } from "react";
import { DollarSign } from "lucide-react";
import type { Api } from "../../lib/api";
import type { Home, UsageAnomaly } from "../../lib/types";
import { formatPower } from "./power";

export function anomalyDescription(home: Home, anomaly: UsageAnomaly) {
  const name =
    home.devices.find((d) => d.entityId === anomaly.entityId)?.name ??
    anomaly.entityId;
  const recommendation =
    anomaly.status === "Confirmed"
      ? "Shutoff confirmed. Review the device before turning it back on."
      : ["Pending", "AwaitingConfirmation", "Retrying"].includes(anomaly.status)
        ? "Shutoff is in progress."
        : anomaly.recommendedAction === "Off"
          ? "Recommended action: turn it off."
          : "Recommended action: inspect the device.";
  return `${name}: ${formatPower(Math.abs(anomaly.differenceWatts))} ${anomaly.differenceWatts > 0 ? "more" : "less"} than usual. ${recommendation}`;
}

export function AnomalySavingsSetting({
  home,
  api,
  stale,
  onSaved,
}: {
  home: Home;
  api: Api;
  stale: boolean;
  onSaved: (home: Home) => void;
}) {
  const helpId = useId();
  const saving = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function toggle() {
    if (saving.current || stale || home.revoked) return;
    saving.current = true;
    setBusy(true);
    setError("");
    try {
      onSaved(await api.anomalySavings(home.id, !home.anomalySavingsEnabled));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      saving.current = false;
      setBusy(false);
    }
  }
  return (
    <div className="notification-settings">
      <div>
        <h3>{home.name}</h3>
        <p className="muted" id={helpId}>
          Choose device categories and standard watts in Device settings. Turn
          off eligible Sometimes and Anytime devices drawing at least 50% above
          standard power; keep them off for review. When disabled, or for Never
          devices, send recommendations only.
        </p>
        {home.revoked && (
          <p className="muted">Reconnect this home to change this setting.</p>
        )}
        {busy && <p role="status">Saving…</p>}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
      <button
        type="button"
        className="notification-toggle"
        role="switch"
        aria-checked={!!home.anomalySavingsEnabled}
        aria-label={`Anomaly savings detection for ${home.name}`}
        aria-describedby={helpId}
        disabled={busy || stale || home.revoked}
        onClick={() => void toggle()}
      >
        <span className="toggle-track" aria-hidden="true">
          <span />
        </span>
      </button>
    </div>
  );
}

export function AnomalySavingsWidget({ home }: { home: Home }) {
  const savings = home.anomalySavings;
  const kwh = savings?.estimatedSavedKwh ?? 0;
  const rate = savings?.estimatedRatePerKwh ?? 0.16;
  return (
    <div
      className="card metric-card savings-card"
      aria-label="Estimated lifetime savings"
    >
      <span className="eyebrow">
        <DollarSign size={18} aria-hidden="true" />
        LIFETIME SAVINGS
      </span>
      <strong title="Estimated lifetime savings">
        {new Intl.NumberFormat("en-US", {
          style: "currency",
          currency: "USD",
        }).format(kwh * rate)}
      </strong>
    </div>
  );
}
