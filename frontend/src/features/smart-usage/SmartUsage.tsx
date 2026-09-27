import { Select } from "../../components/ui/Select";
import { useEffect, useRef, useState, type CSSProperties } from "react";
import {
  BatteryMedium,
  Clock3,
  Lightbulb,
  Power,
  RefreshCw,
} from "lucide-react";
import type { Api } from "../../lib/api";
import type { Command, Home, SmartUsagePlan } from "../../lib/types";
import { Button } from "../../components/ui/Button";
import { useHomes } from "../dashboard/useHomes";
import { formatPower } from "../dashboard/power";
import { duration, localEndTime, runtimeHours } from "./runtime";

const terminal = (command: Command) =>
  ["Confirmed", "Failed", "Expired", "Cancelled"].includes(command.status);

export function SmartUsage({ api }: { api: Api }) {
  const { homes, loading, error, refresh } = useHomes(api);
  const [active, setActive] = useState(
    new URLSearchParams(window.location.search).get("home") ?? "",
  );
  const home =
    homes.find((item) => item.id === active) ??
    homes.find((item) => !item.revoked) ??
    homes[0];
  return (
    <div className="smart-usage-page">
      <header className="page-heading">
        <div>
          <h1>
            <Lightbulb aria-hidden="true" /> Smart Usage
          </h1>
          <p>
            Make your battery last longer. Choose the time, then review the
            device changes.
          </p>
        </div>
      </header>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {loading ? (
        <p role="status">Loading your homes…</p>
      ) : !home ? (
        <section className="card empty">
          <h2>Connect a home to get started</h2>
          <p>Smart Usage needs battery and household power readings.</p>
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
          <UsagePlanner
            key={home.id}
            home={home}
            api={api}
            stale={!!error}
            refreshHome={refresh}
          />
        </>
      )}
    </div>
  );
}

export function UsagePlanner({
  home,
  api,
  stale,
  refreshHome,
}: {
  home: Home;
  api: Api;
  stale: boolean;
  refreshHome: () => Promise<void>;
}) {
  const [plan, setPlan] = useState<SmartUsagePlan | null>(null);
  const [target, setTarget] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [applyError, setApplyError] = useState("");
  const [busy, setBusy] = useState(false);
  const [submitted, setSubmitted] = useState<Command[] | null>(null);
  const [reload, setReload] = useState(0);
  const confirmation = useRef<{ revision: string; key: string } | null>(null);
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    setLoading(true);
    async function read() {
      try {
        const next = await api.smartUsage(
          home.id,
          target ?? undefined,
          controller.signal,
        );
        if (controller.signal.aborted) return;
        setPlan(next);
        setError("");
      } catch (failure) {
        if (!controller.signal.aborted) setError((failure as Error).message);
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
          timer = setTimeout(read, 3000);
        }
      }
    }
    timer = setTimeout(read, target === null ? 0 : 200);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [api, home.id, target, reload]);

  const outcomes = submitted?.map(
    (command) =>
      home.commands.find((item) => item.id === command.id) ?? command,
  );
  const pending =
    home.commands.some((command) => !terminal(command)) ||
    !!outcomes?.some((command) => !terminal(command));
  const offline = stale || !home.connected || home.revoked;
  const span = plan ? plan.allOnWatts - plan.minimumWatts : 0;
  const selectedWatts = plan
    ? Math.max(
        plan.minimumWatts,
        Math.min(plan.allOnWatts, target ?? plan.currentWatts),
      )
    : 0;
  const position =
    plan && span > 0 ? ((plan.allOnWatts - selectedWatts) / span) * 1000 : 0;
  const selectedHours = plan
    ? runtimeHours(plan.battery.storedEnergyKwh, selectedWatts)
    : null;
  const reviewing =
    loading || (!!plan && Math.abs(plan.targetWatts - selectedWatts) > 0.01);

  async function confirm() {
    if (
      !plan ||
      !plan.canApply ||
      busy ||
      reviewing ||
      error ||
      offline ||
      pending
    )
      return;
    setBusy(true);
    setApplyError("");
    const reviewed = plan;
    if (confirmation.current?.revision !== reviewed.revision)
      confirmation.current = {
        revision: reviewed.revision,
        key: crypto.randomUUID(),
      };
    try {
      const commands = await api.applySmartUsage(
        home.id,
        reviewed.targetWatts,
        reviewed.revision,
        confirmation.current.key,
      );
      if (!alive.current) return;
      setSubmitted(commands);
      confirmation.current = null;
      setTarget(null);
      setReload((value) => value + 1);
      await refreshHome();
    } catch (failure) {
      if (alive.current) {
        setApplyError((failure as Error).message);
        setReload((value) => value + 1);
      }
    } finally {
      if (alive.current) setBusy(false);
    }
  }

  return (
    <>
      {(error || offline) && (
        <div className="error banner" role="alert">
          {error ||
            "Controls are unavailable until fresh home readings return."}
          <Button
            variant="secondary"
            onClick={() => {
              setReload((value) => value + 1);
              void refreshHome();
            }}
          >
            Retry
          </Button>
        </div>
      )}
      {!plan ? (
        !error && (
          <p role="status">
            <RefreshCw className="spin" size={16} /> Reading battery and device
            usage…
          </p>
        )
      ) : (
        <>
          <section className="smart-summary" aria-label="Battery runtime range">
            <div className="card">
              <span>
                <BatteryMedium size={20} aria-hidden="true" /> Battery available{" "}
                {plan.battery.isSimulated && (
                  <span className="smart-simulation">Simulated</span>
                )}
              </span>
              <strong>{plan.battery.stateOfChargePercent.toFixed(0)}%</strong>
              <p>
                {plan.battery.storedEnergyKwh.toFixed(2)} of{" "}
                {plan.battery.capacityKwh.toFixed(2)} kWh
              </p>
            </div>
            <div className="card">
              <span>
                <Clock3 size={20} aria-hidden="true" /> Current runtime
              </span>
              <strong>{duration(plan.currentHours)}</strong>
              <p>{localEndTime(plan.calculatedAtUtc, plan.currentHours)}</p>
              <small>{formatPower(plan.currentWatts)} currently on</small>
            </div>
            <div className="card">
              <span>
                <Lightbulb size={20} aria-hidden="true" /> Longest estimated
                runtime
              </span>
              <strong>{duration(plan.longestHours)}</strong>
              <p>All available Anytime and Sometimes devices off</p>
              <small>{formatPower(plan.minimumWatts)} remaining load</small>
            </div>
          </section>
          <section className="card smart-planner" aria-label="Runtime planner">
            <div className="smart-planner-heading">
              <div>
                <h2>How long do you need?</h2>
                <p>Slide toward more time to use fewer devices.</p>
              </div>
              <span className="smart-limit">11 kW limit / battery</span>
            </div>
            <div className="smart-target" aria-live="polite">
              <label
                className="smart-slider-label"
                htmlFor={`runtime-${home.id}`}
              >
                Battery runtime
              </label>
              <strong>{duration(selectedHours)}</strong>
              <p>Until {localEndTime(plan.calculatedAtUtc, selectedHours)}</p>
            </div>
            <input
              id={`runtime-${home.id}`}
              className="smart-slider"
              style={
                { "--smart-slider-fill": `${position / 10}%` } as CSSProperties
              }
              type="range"
              min="0"
              max="1000"
              step="1"
              value={position}
              aria-valuetext={`${duration(selectedHours)}, ${localEndTime(plan.calculatedAtUtc, selectedHours)}`}
              disabled={busy || offline || span <= 0 || pending}
              onChange={(event) => {
                setLoading(true);
                setApplyError("");
                setTarget(
                  plan.allOnWatts - (Number(event.target.value) / 1000) * span,
                );
              }}
            />
            <div
              className="smart-runtime-presets"
              role="group"
              aria-label="Runtime presets"
            >
              {[4, 12, 24].map((hours) => (
                <Button
                  key={hours}
                  variant="secondary"
                  disabled={busy || offline || span <= 0 || pending}
                  onClick={() => {
                    const watts = Math.max(
                      plan.minimumWatts,
                      Math.min(
                        plan.allOnWatts,
                        (plan.battery.storedEnergyKwh * 1000) / hours,
                      ),
                    );
                    if (watts === target) return;
                    setLoading(true);
                    setApplyError("");
                    setTarget(watts);
                  }}
                >
                  {hours}h
                </Button>
              ))}
            </div>
            <div className="smart-plan-result" aria-live="polite">
              {reviewing ? (
                <p role="status">Calculating device changes…</p>
              ) : (
                <>
                  <span>With these device changes</span>
                  <strong>
                    {duration(plan.projectedHours)} ·{" "}
                    {formatPower(plan.projectedWatts)}
                  </strong>
                  <p>
                    Estimated until{" "}
                    {localEndTime(plan.calculatedAtUtc, plan.projectedHours)}
                  </p>
                </>
              )}
            </div>
            <div className="smart-changes-heading">
              <h3>
                Device changes{" "}
                <span className="count">{plan.changes.length}</span>
              </h3>
            </div>
            <div
              aria-busy={reviewing}
              className={
                reviewing ? "smart-changes refreshing" : "smart-changes"
              }
            >
              {plan.changes.length ? (
                <ul>
                  {plan.changes.map((change) => (
                    <li key={change.entityId}>
                      <span
                        className={`smart-action ${change.action.toLowerCase()}`}
                      >
                        <Power size={16} aria-hidden="true" /> Turn{" "}
                        {change.action.toLowerCase()}
                      </span>
                      <div>
                        <strong>{change.name}</strong>
                        <span>
                          {change.shutoffLevel} · Estimated{" "}
                          {formatPower(change.estimatedWatts)}
                        </span>
                      </div>
                    </li>
                  ))}
                </ul>
              ) : (
                <p>No device switches are needed for this selection.</p>
              )}
            </div>
            {plan.blockedReason && (
              <p className="error" role="alert">
                {plan.blockedReason}
              </p>
            )}
            {applyError && (
              <p className="error" role="alert">
                {applyError}
              </p>
            )}
            <div className="smart-confirm">
              <p>
                Confirm approves these switches, including Sometimes devices.
                Devices kept off by this plan stay out of automatic restore; use
                a shorter runtime to bring them back.
              </p>
              <Button
                disabled={
                  busy ||
                  reviewing ||
                  !plan.canApply ||
                  !!error ||
                  offline ||
                  pending
                }
                onClick={() => void confirm()}
              >
                {busy ? "Submitting…" : "Confirm plan"}
              </Button>
            </div>
          </section>
          {submitted !== null && (
            <section
              className="card smart-outcomes"
              aria-label="Plan progress"
              aria-live="polite"
            >
              <h2>
                {pending
                  ? "Applying your plan"
                  : outcomes?.some((command) => command.status !== "Confirmed")
                    ? "Some device changes need attention"
                    : "Plan confirmed"}
              </h2>
              <p>
                {pending
                  ? "Waiting for Home Assistant to confirm the device states. Turn-ons run one at a time after a fresh power check."
                  : "Review the latest measured runtime above."}
              </p>
              <ul>
                {outcomes?.map((command) => (
                  <li key={command.id}>
                    <strong>
                      {home.devices.find(
                        (device) => device.entityId === command.entityId,
                      )?.name ?? command.entityId}
                    </strong>{" "}
                    · {command.action} · {command.status}
                    {command.message && (
                      <p className="muted">{command.message}</p>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}
          <p className="muted smart-estimate-note">
            Estimates assume constant measured load, no charging, and all
            reported stored energy usable. Actual runtime can change.{" "}
            {plan.battery.isSimulated &&
              "Battery telemetry is simulated and its configured discharge rate is independent of these device changes."}{" "}
            Times use your browser’s local time zone. A zero measured load has
            no finite estimated end; battery standby losses are not modeled.
          </p>
          {plan.excludedDevices.length > 0 && (
            <details className="card smart-excluded">
              <summary>
                {plan.excludedDevices.length} devices outside this estimate’s
                adjustable range
              </summary>
              <p>
                These loads stay in current household usage. A complete
                all-on/all-off range needs usable power estimates and switching
                permissions.
              </p>
              <ul>
                {plan.excludedDevices.map((device) => (
                  <li key={device.entityId}>
                    <strong>{device.name}</strong> — {device.reason}
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </>
  );
}
