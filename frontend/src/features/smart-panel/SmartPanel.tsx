import { Select } from "../../components/ui/Select";
import { useId, useRef, useState } from "react";
import {
  CircuitBoard,
  LoaderCircle,
  Power,
  RefreshCw,
  TriangleAlert,
} from "lucide-react";
import type { Api } from "../../lib/api";
import type { CircuitPriority, Command, Device, Home } from "../../lib/types";
import { useHomes } from "../dashboard/useHomes";
import { Button } from "../../components/ui/Button";

const terminal = (command: Command) =>
  ["Confirmed", "Failed", "Expired", "Cancelled"].includes(command.status);

const priorityLabels: Record<CircuitPriority, string> = {
  off_grid: "Turn off",
  never: "Stay on",
  soc_threshold: "Stay on until battery threshold",
};

export function SmartPanel({ api }: { api: Api }) {
  const { homes, loading, error, refresh } = useHomes(api);
  const [active, setActive] = useState(
    new URLSearchParams(window.location.search).get("home") ?? "",
  );
  const home =
    homes.find((item) => item.id === active) ??
    homes.find((item) => !item.revoked) ??
    homes[0];
  return (
    <div className="smart-panel-page">
      <header className="page-heading">
        <div>
          <h1>
            <CircuitBoard aria-hidden="true" /> Smart Panel
          </h1>
          <p>See every circuit. Control power to each section of your home.</p>
        </div>
        <Button variant="secondary" onClick={() => void refresh()}>
          <RefreshCw size={16} /> Refresh
        </Button>
      </header>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {loading ? (
        <p role="status">Loading your panel…</p>
      ) : !home ? (
        <section className="card empty">
          <h2>Connect a home to see your panel</h2>
          <p>Connect Home Assistant with your SPAN panel integration.</p>
          <a className="button primary" href="/">
            Connect a home
          </a>
        </section>
      ) : (
        <>
          <label className="smart-home-picker">
            Home
            <Select
              aria-label="Selected home"
              value={home.id}
              onChange={(event) => setActive(event.target.value)}
            >
              {homes.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name}
                  {item.revoked ? " (disconnected)" : ""}
                </option>
              ))}
            </Select>
          </label>
          <CircuitPanel
            key={home.id}
            home={home}
            api={api}
            stale={!!error}
            refresh={refresh}
          />
        </>
      )}
    </div>
  );
}

export function CircuitPanel({
  home,
  api,
  stale,
  refresh,
}: {
  home: Home;
  api: Api;
  stale: boolean;
  refresh: () => Promise<void>;
}) {
  const circuits = home.devices.filter((device) => device.isCircuit);
  const offline = stale || !home.connected || home.revoked;
  const on = circuits.filter((device) => device.state === "on").length;
  const off = circuits.filter((device) => device.state === "off").length;
  return (
    <section className="card circuit-panel" aria-label="Circuit panel">
      <div className="circuit-panel-heading">
        <div>
          <span className="eyebrow">HOME CIRCUITS</span>
          <h2>{home.name}</h2>
        </div>
        <span className={`panel-connection ${offline ? "is-offline" : ""}`}>
          {offline ? "Connection unavailable" : "Connected to Home Assistant"}
        </span>
      </div>
      <div className="circuit-summary" aria-label="Circuit summary">
        <span>
          <strong>{circuits.length}</strong> circuits
        </span>
        <span>
          <strong>{offline ? "—" : on}</strong> on
        </span>
        <span>
          <strong>{offline ? "—" : off}</strong> off
        </span>
        {(offline || circuits.length > on + off) && (
          <span>
            <strong>
              {offline ? circuits.length : circuits.length - on - off}
            </strong>{" "}
            unavailable
          </span>
        )}
      </div>
      {offline && (
        <p role="status" className="notice">
          Circuit controls are unavailable until fresh Home Assistant readings
          return.
        </p>
      )}
      {circuits.length === 0 ? (
        <div className="empty">
          <h3>No panel circuits found</h3>
          <p>
            Enable your SPAN breaker switches in Home Assistant. They will
            appear here after the next sync.
          </p>
        </div>
      ) : (
        <div className="circuit-grid">
          {circuits.map((device) => (
            <CircuitRow
              key={device.entityId}
              device={device}
              home={home}
              api={api}
              offline={offline}
              refresh={refresh}
            />
          ))}
        </div>
      )}
      <p className="circuit-sync">
        Last seen{" "}
        {home.lastSeenUtc ? (
          <time dateTime={home.lastSeenUtc}>
            {new Date(home.lastSeenUtc).toLocaleString()}
          </time>
        ) : (
          "—"
        )}
      </p>
    </section>
  );
}

function CircuitRow({
  device,
  home,
  api,
  offline,
  refresh,
}: {
  device: Device;
  home: Home;
  api: Api;
  offline: boolean;
  refresh: () => Promise<void>;
}) {
  const [submitted, setSubmitted] = useState<Command | null>(null);
  const [busy, setBusy] = useState(false);
  const [savingPriority, setSavingPriority] = useState(false);
  const [error, setError] = useState("");
  const sending = useRef(false);
  const attempt = useRef<{ action: "On" | "Off"; key: string } | null>(null);
  const priorityAttempt = useRef<{
    priority: CircuitPriority;
    key: string;
  } | null>(null);
  const latest = home.commands.find(
    (command) => command.entityId === device.entityId,
  );
  const tracked = submitted
    ? (home.commands.find((command) => command.id === submitted.id) ??
      submitted)
    : null;
  const pending =
    home.commands.find(
      (command) => command.entityId === device.entityId && !terminal(command),
    ) ?? (tracked && !terminal(tracked) ? tracked : null);
  const known = !offline && ["on", "off"].includes(device.state);
  const on = known && device.state === "on";
  const priority = device.circuitPriority;
  const outageWarningId = useId();
  const showOutageWarning =
    priority?.priority === "never" || priority?.priority === "soc_threshold";
  const name = device.name
    .replace(/^SPAN\s+Panel\s*/i, "")
    .replace(/\s+Breaker$/i, "");
  const updating = busy || !!pending;
  const updatingPriority =
    savingPriority || pending?.action === "SetCircuitPriority";
  const targetState = pending
    ? pending.action === "On"
      ? "on"
      : "off"
    : on
      ? "off"
      : "on";
  const outcome = latest ?? tracked;
  const failure =
    !busy &&
    !pending &&
    outcome &&
    ["Failed", "Expired", "Cancelled"].includes(outcome.status)
      ? (outcome.message ?? `Command ${outcome.status.toLowerCase()}.`)
      : "";
  const feedback = updating ? "" : error || failure;
  async function toggle() {
    if (!known || pending || sending.current) return;
    sending.current = true;
    setBusy(true);
    setError("");
    const action = on ? "Off" : "On";
    if (attempt.current?.action !== action)
      attempt.current = { action, key: crypto.randomUUID() };
    try {
      const command = await api.circuitCommand(
        home.id,
        device.entityId,
        action,
        attempt.current.key,
      );
      setSubmitted(command);
      attempt.current = null;
      await refresh();
    } catch (failure) {
      setError((failure as Error).message);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }
  async function changePriority(value: CircuitPriority) {
    if (
      offline ||
      !priority?.priority ||
      pending ||
      sending.current ||
      value === priority.priority
    )
      return;
    sending.current = true;
    setBusy(true);
    setSavingPriority(true);
    setError("");
    if (priorityAttempt.current?.priority !== value)
      priorityAttempt.current = { priority: value, key: crypto.randomUUID() };
    try {
      const command = await api.circuitPriority(
        home.id,
        device.entityId,
        value,
        priorityAttempt.current.key,
      );
      setSubmitted(command);
      priorityAttempt.current = null;
      await refresh();
    } catch (failure) {
      setError((failure as Error).message);
    } finally {
      sending.current = false;
      setBusy(false);
      setSavingPriority(false);
    }
  }
  return (
    <article className={`circuit-row ${on ? "is-on" : ""}`}>
      <div className="circuit-row-main">
        <span className="circuit-icon">
          <Power size={20} aria-hidden="true" />
        </span>
        <div className="circuit-label">
          <h3>{name}</h3>
          <span
            className={`circuit-state${feedback ? " circuit-state-error" : ""}`}
            role={updating ? "status" : feedback ? "alert" : undefined}
            aria-label={
              updating
                ? updatingPriority
                  ? `Updating ${name} grid outage setting`
                  : `Turning ${name} ${targetState}`
                : undefined
            }
          >
            {updating ? (
              <>
                <LoaderCircle size={16} className="spin" aria-hidden="true" />
                <span>
                  {updatingPriority
                    ? "Saving setting…"
                    : `Turning ${targetState}…`}
                </span>
              </>
            ) : feedback ? (
              <>
                <TriangleAlert size={16} aria-hidden="true" />
                <span>{feedback}</span>
              </>
            ) : known ? (
              on ? (
                "On"
              ) : (
                "Off"
              )
            ) : (
              "Unavailable"
            )}
          </span>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={on}
          aria-label={`${name} circuit`}
          className="notification-toggle"
          disabled={!known || !!pending || busy}
          onClick={() => void toggle()}
        >
          <span className="toggle-track" aria-hidden="true">
            <span />
          </span>
        </button>
      </div>
      <label className="circuit-outage-setting">
        <span>When the grid goes down</span>
        <Select
          aria-label={`${name} grid outage setting`}
          aria-describedby={showOutageWarning ? outageWarningId : undefined}
          value={offline ? "" : (priority?.priority ?? "")}
          disabled={offline || !priority?.priority || !!pending || busy}
          onChange={(event) =>
            void changePriority(event.target.value as CircuitPriority)
          }
        >
          {(offline || !priority?.priority) && (
            <option value="">Setting unavailable</option>
          )}
          {priority?.options.map((option) => (
            <option key={option} value={option}>
              {priorityLabels[option]}
            </option>
          ))}
        </Select>
      </label>
      {showOutageWarning && (
        <div
          className="circuit-outage-warning"
          id={outageWarningId}
          role="alert"
        >
          <TriangleAlert size={18} aria-hidden="true" />
          <p>
            <strong>Battery backup warning</strong>
            Leaving this circuit on could prevent Base Layer from ensuring the
            battery kicks in during an outage. Set it to “Turn off”.
          </p>
        </div>
      )}
    </article>
  );
}
