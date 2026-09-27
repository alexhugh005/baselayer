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

  async function toggle(always?: boolean) {
    if (saving.current || stale || home.revoked) return;
    saving.current = true;
    setBusy(true);
    setError("");
    try {
      onSaved(
        always === undefined
          ? await api.smartPowerOff(home.id, !home.smartPowerOffEnabled)
          : await api.smartPowerOff(
              home.id,
              always || !!home.smartPowerOffEnabled,
              always,
            ),
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      saving.current = false;
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="notification-settings">
        <div>
          <h3>{home.name}</h3>
          <p className="muted" id={helpId}>
            Reduce usage following device permissions. Automatically restore
            devices when there is enough capacity. By default, the limit applies
            during elevated grid risk.
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
      <div className="notification-settings">
        <div>
          <h3>Always stay below battery limit</h3>
          <p className="muted" id={`${helpId}-limit`}>
            Keep usage below {(home.limitWatts / 1000).toFixed(0)} kW, even when
            grid risk is low. Turning this on enables Smart Shutoff. Anytime
            devices can reduce charging or turn off; Sometimes devices need
            approval except at high risk. Never devices stay untouched. If these
            changes are not enough, you will need to reduce usage yourself.
          </p>
          {home.alwaysKeepBelowBatteryLimit && !home.smartPowerOffEnabled && (
            <p className="muted">Paused while Smart Shutoff is off.</p>
          )}
        </div>
        <button
          type="button"
          className="notification-toggle"
          role="switch"
          aria-checked={!!home.alwaysKeepBelowBatteryLimit}
          aria-label={`Always stay below battery limit for ${home.name}`}
          aria-describedby={`${helpId}-limit`}
          disabled={busy || stale || home.revoked}
          onClick={() => void toggle(!home.alwaysKeepBelowBatteryLimit)}
        >
          <span className="toggle-track" aria-hidden="true">
            <span />
          </span>
        </button>
      </div>
    </div>
  );
}
