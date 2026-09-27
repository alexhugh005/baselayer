import { useEffect, useRef, useState } from "react";
import type { Home } from "../../lib/types";
import { anomalyDescription } from "./AnomalySavings";
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
  return (
    ((home.smartPowerOffEnabled && home.alwaysKeepBelowBatteryLimit) ||
      home.gridOutageRisk === "medium" ||
      home.gridOutageRisk === "high") &&
    hasUsageReading(home) &&
    home.householdWatts! >= home.limitWatts
  );
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
  const notified = useRef(new Map<string, string>());
  const anomalyNotified = useRef(new Set<string>());
  const automaticNotified = useRef(new Set<string>());
  const automaticLoaded = useRef(false);

  useEffect(() => {
    if (stale) return;
    const storageKey = `${preferenceKey}:smart-power-off-seen`;
    if (!automaticLoaded.current) {
      try {
        const saved: unknown = JSON.parse(
          localStorage.getItem(storageKey) ?? "[]",
        );
        if (Array.isArray(saved))
          automaticNotified.current = new Set(
            saved.filter((id): id is string => typeof id === "string"),
          );
      } catch {
        /* In-memory deduplication still works when storage is unavailable. */
      }
      automaticLoaded.current = true;
    }
    for (const home of homes) {
      const confirmed = (home.commands ?? []).filter(
        (c) =>
          c.automatic &&
          !c.anomalyId &&
          c.action !== "On" &&
          !c.isRestoration &&
          c.status === "Confirmed" &&
          !automaticNotified.current.has(c.id),
      );
      const recent = confirmed.filter(
        (c) => Date.now() - Date.parse(c.createdUtc) < 5 * 60 * 1000,
      );
      if (
        enabled &&
        supported &&
        Notification.permission === "granted" &&
        recent.length
      ) {
        try {
          const changes = recent.map((c) => {
            const name =
              home.devices.find((d) => d.entityId === c.entityId)?.name ??
              c.entityId;
            return c.action === "SetCurrent"
              ? `Reduced ${name} charging to ${c.currentAmps} A`
              : `Turned off ${name}`;
          });
          const notification = new Notification(`${home.name}: Smart Shutoff`, {
            body: `${changes.join("; ")} to reduce usage.${home.smartPowerOffStatus === "insufficient" ? " More devices need to be turned off to get below the limit. Open Base Layer to review." : home.smartPowerOffStatus === "review" ? " Usage is still high. Open Base Layer to review Sometimes devices." : ""}`,
            tag: `smart-power-off-${home.id}`,
          });
          notification.onclick = () => {
            window.focus();
            onSelect(home.id);
            notification.close();
          };
        } catch {
          setEnabled(false);
          setMessage(
            "This browser could not show a notification. Check Recent activity for automatic shutoffs.",
          );
        }
      }
      for (const command of confirmed)
        automaticNotified.current.add(command.id);
    }
    try {
      localStorage.setItem(
        storageKey,
        JSON.stringify([...automaticNotified.current].slice(-300)),
      );
    } catch {
      /* Keep deduplicating in memory. */
    }
  }, [homes, stale, enabled, supported, preferenceKey, onSelect]);

  useEffect(() => {
    if (stale) return;
    const existing = new Set(homes.map((home) => home.id));
    for (const id of notified.current.keys()) {
      if (!existing.has(id)) notified.current.delete(id);
    }
    for (const home of homes) {
      // Missing data cannot establish that an ongoing high-usage event ended.
      if (!hasUsageReading(home)) continue;
      if (!isUsageHigh(home)) {
        notified.current.delete(home.id);
        continue;
      }
      if (home.smartPowerOffStatus === "reducing") continue;
      const devices = (home.devices ?? []).filter((d) => d.recommended);
      const signature = `${home.gridOutageRisk}:${home.smartPowerOffStatus}:${devices
        .map((d) => d.entityId)
        .sort()
        .join(",")}`;
      if (!enabled || notified.current.get(home.id) === signature) continue;
      if (!supported || Notification.permission !== "granted") {
        setEnabled(false);
        setMessage(
          "Browser notifications are blocked. Allow them in your browser settings.",
        );
        break;
      }
      try {
        const reason =
          home.smartPowerOffEnabled && home.alwaysKeepBelowBatteryLimit
            ? "Your always-on battery limit is active."
            : `Grid outage risk is ${home.gridOutageRisk}.`;
        const notification = new Notification(`${home.name}: action needed`, {
          body: `${reason} Usage is ${formatPower(home.householdWatts)}; keep below ${formatPower(home.limitWatts)}. ${devices.length ? `Turn off ${devices.map((d) => d.name).join(", ")}. ` : ""}${home.smartPowerOffStatus === "insufficient" || !devices.length ? "Additional appliances must be turned off to get below the limit. " : ""}Open Base Layer to review devices.`,
          tag: `usage-${home.id}`,
        });
        notification.onclick = () => {
          window.focus();
          onSelect(home.id);
          notification.close();
        };
        notified.current.set(home.id, signature);
      } catch {
        setEnabled(false);
        setMessage(
          "This browser could not show a notification. In-site alerts remain active.",
        );
        break;
      }
    }
  }, [homes, stale, enabled, supported, onSelect]);

  useEffect(() => {
    if (
      stale ||
      !enabled ||
      !supported ||
      Notification.permission !== "granted"
    )
      return;
    const storageKey = `${preferenceKey}:anomalies-seen`;
    let seen: string[] = [];
    try {
      const stored: unknown = JSON.parse(
        localStorage.getItem(storageKey) ?? "[]",
      );
      if (Array.isArray(stored))
        seen = stored.filter((id): id is string => typeof id === "string");
    } catch {
      /* Keep in-memory deduplication when storage is unavailable. */
    }
    for (const home of homes) {
      for (const anomaly of home.anomalies ?? []) {
        if (anomaly.status === "Queued") continue;
        const key = `${anomaly.id}:${anomaly.status === "Confirmed" ? "confirmed" : "detected"}`;
        if (seen.includes(key) || anomalyNotified.current.has(key)) continue;
        seen.push(key);
        anomalyNotified.current.add(key);
        if (Date.now() - Date.parse(anomaly.detectedUtc) > 5 * 60 * 1000)
          continue;
        try {
          const notification = new Notification(
            `${home.name}: unusual device usage`,
            {
              body: `${anomalyDescription(home, anomaly)} ${anomaly.message}`,
              tag: `anomaly-${anomaly.id}`,
            },
          );
          notification.onclick = () => {
            window.focus();
            onSelect(home.id);
            notification.close();
          };
        } catch {
          setEnabled(false);
          setMessage(
            "Browser notifications are unavailable. In-site anomaly alerts remain active.",
          );
        }
      }
    }
    try {
      localStorage.setItem(storageKey, JSON.stringify(seen.slice(-300)));
    } catch {
      /* In-memory fallback. */
    }
  }, [homes, stale, enabled, supported, preferenceKey, onSelect]);

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
                Notify me when grid outage risk requires action or Smart Shutoff
                confirms a device reduction, or unusual device usage is
                detected. Works while Base Layer is open.
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
