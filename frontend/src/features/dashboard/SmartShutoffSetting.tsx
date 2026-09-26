import { useId, useRef, useState } from "react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";

export function SmartShutoffSetting({
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
      onSaved(await api.smartPowerOff(home.id, !home.smartPowerOffEnabled));
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
          Turn off allowed devices at or above {home.limitWatts / 1000} kW and
          restore devices when there is enough spare capacity.
        </p>
        {home.revoked && (
          <p className="muted">Reconnect this home to change this setting.</p>
        )}
        {busy && (
          <p className="settings-status" role="status">
            Saving…
          </p>
        )}
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
        aria-checked={!!home.smartPowerOffEnabled}
        aria-label={`Smart Shutoff and automatic restore for ${home.name}`}
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
