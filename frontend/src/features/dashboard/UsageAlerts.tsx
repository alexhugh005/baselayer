import { useEffect, useRef, useState } from "react";
import type { Home } from "../../lib/types";
import { formatPower } from "./power";

export function hasUsageReading(home: Home) {
  return (
    home.connected &&
    !home.revoked &&
    home.householdWatts !== null &&
    Number.isFinite(home.householdWatts) &&
    Number.isFinite(home.limitWatts) &&
    home.limitWatts > 0
  );
}

export function isUsageHigh(home: Home) {
  return hasUsageReading(home) && home.householdWatts! >= home.limitWatts;
}

export function UsageAlerts({
  homes,
  stale,
  onSelect,
  settingsPage = false,
  preferenceKey = "base-layer-notifications",
}: {
  homes: Home[];
  stale: boolean;
  onSelect: (id: string) => void;
  settingsPage?: boolean;
  preferenceKey?: string;
}) {
  const supported = window.isSecureContext && "Notification" in window;
  const [enabled, updateEnabled] = useState(() => {
    try {
      return (
        supported &&
        Notification.permission === "granted" &&
        localStorage.getItem(preferenceKey) === "true"
      );
    } catch {
      return false;
    }
  });
  function setEnabled(value: boolean) {
    updateEnabled(value);
    try {
      localStorage.setItem(preferenceKey, String(value));
    } catch {
      setMessage(
        "Your preference could not be saved. It applies until you leave this page.",
      );
    }
  }
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const notified = useRef(new Set<string>());

  useEffect(() => {
    if (stale) return;
    const existing = new Set(homes.map((home) => home.id));
    for (const id of notified.current) {
      if (!existing.has(id)) notified.current.delete(id);
    }
    for (const home of homes) {
      // Missing data cannot establish that an ongoing high-usage event ended.
      if (!hasUsageReading(home)) continue;
      if (!isUsageHigh(home)) {
        notified.current.delete(home.id);
        continue;
      }
      if (!enabled || notified.current.has(home.id)) continue;
      if (!supported || Notification.permission !== "granted") {
        setEnabled(false);
        setMessage(
          "Browser notifications are blocked. Allow them in your browser settings.",
        );
        break;
      }
      try {
        const notification = new Notification(
          `${home.name}: usage limit reached`,
          {
            body: `${home.powerSource === "deviceSum" ? "Monitored device usage" : "Household usage"} is ${formatPower(home.householdWatts)}. Limit: ${formatPower(home.limitWatts)}. Open Base Layer to review devices.`,
            tag: `usage-${home.id}`,
          },
        );
        notification.onclick = () => {
          window.focus();
          onSelect(home.id);
          notification.close();
        };
        notified.current.add(home.id);
      } catch {
        setEnabled(false);
        setMessage(
          "This browser could not show a notification. In-site alerts remain active.",
        );
        break;
      }
    }
  }, [homes, stale, enabled, supported, onSelect]);

  async function enable() {
    setBusy(true);
    setMessage("");
    try {
      const permission = await Notification.requestPermission();
      setEnabled(permission === "granted");
      if (permission !== "granted") {
        setMessage(
          permission === "denied"
            ? "Browser notifications are blocked. Allow them in your browser settings."
            : "Permission was not granted. In-site alerts remain active.",
        );
      }
    } catch {
      setMessage(
        "Browser notifications are unavailable. In-site alerts remain active.",
      );
    } finally {
      setBusy(false);
    }
  }

  if (!settingsPage) return null;

  return (
    <section className="usage-alerts" aria-label="Usage alerts">
      {settingsPage && (
        <div className="settings-section">
          <h2>Notifications</h2>
          <div className="notification-settings">
            <div>
              <h3>Browser notifications</h3>
              <p className="muted" id="notification-help">
                Notify me when a home reaches its usage limit. Works while Base
                Layer is open.
              </p>
              {(message || !supported || busy) && (
                <p className="settings-status" role="status">
                  {message ||
                    (!supported
                      ? "Notifications aren’t available in this browser."
                      : "Waiting for notification permission…")}
                </p>
              )}
            </div>
            <button
              type="button"
              className="notification-toggle"
              role="switch"
              aria-checked={enabled}
              aria-label="Browser notifications"
              aria-describedby="notification-help"
              disabled={busy || !supported}
              onClick={() => {
                setMessage("");
                if (enabled) setEnabled(false);
                else void enable();
              }}
            >
              <span className="toggle-track" aria-hidden="true">
                <span />
              </span>
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
