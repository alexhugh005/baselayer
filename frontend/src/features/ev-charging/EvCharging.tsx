import { useEffect, useRef, useState, type CSSProperties } from "react";
import { BatteryCharging, Car, Pencil, Plus, Zap } from "lucide-react";
import type { Api } from "../../lib/api";
import type { Command, Device, Home, EvVehicle } from "../../lib/types";
import { Button } from "../../components/ui/Button";
import { Modal } from "../../components/ui/Modal";
import { Select } from "../../components/ui/Select";
import { useHomes } from "../dashboard/useHomes";
import { chargingPlan } from "./chargingPlan";
import "./EvCharging.css";
import { VehicleManager } from "./VehicleManager";

const terminal = (c: Command) =>
  ["Confirmed", "Failed", "Expired", "Cancelled"].includes(c.status);
function nextDeparture() {
  const date = new Date();
  date.setDate(date.getDate() + 1);
  date.setHours(8, 0, 0, 0);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16);
}

export function EvCharging({ api }: { api: Api }) {
  const { homes, loading, error, refresh, updateHome } = useHomes(api);
  const [active, setActive] = useState(
    new URLSearchParams(window.location.search).get("home") ?? "",
  );
  const home =
    homes.find((h) => h.id === active) ??
    homes.find((h) => !h.revoked) ??
    homes[0];
  return (
    <div className="ev-page">
      <header className="page-heading">
        <div>
          <h1>
            <Car aria-hidden="true" /> EV charging
          </h1>
          <p>
            Your target charge by departure, at the lowest charging rate that
            fits.
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
          <h2>Connect your home</h2>
          <p>Connect Home Assistant to pair your EV charger.</p>
          <a className="button primary" href="/">
            Connect a home
          </a>
        </section>
      ) : (
        <>
          <label className="smart-home-picker">
            Home
            <Select value={home.id} onChange={(e) => setActive(e.target.value)}>
              {homes.map((h) => (
                <option key={h.id} value={h.id}>
                  {h.name}
                </option>
              ))}
            </Select>
          </label>
          <VehicleManager
            key={home.id}
            home={home}
            api={api}
            stale={!!error}
            refresh={refresh}
            updateHome={updateHome}
          />
        </>
      )}
    </div>
  );
}

export function EvDeparturePlanner({
  home,
  device,
  api,
  stale,
  refresh,
  updateHome,
  vehicle,
  onEdit,
}: {
  home: Home;
  vehicle?: EvVehicle;
  onEdit?: () => void;
  device: Device;
  api: Api;
  stale: boolean;
  refresh: () => Promise<void>;
  updateHome: (home: Home) => void;
}) {
  const departureKey = `ev-departure:${home.id}:${vehicle?.id ?? device.entityId}`;
  const [departure, setDeparture] = useState(() => {
    try {
      return localStorage.getItem(departureKey) || nextDeparture();
    } catch {
      return nextDeparture();
    }
  });
  useEffect(() => {
    try {
      localStorage.setItem(departureKey, departure);
    } catch {
      // Charging remains available when browser storage is disabled.
    }
  }, [departureKey, departure]);
  const chargeLimitControl = home.evBatterySensors?.find(
    (s) => s.entityId === device.evBattery?.sensorEntityId,
  )?.chargeLimitControl;
  const chargeLimitKey = `ev-charge-limit:${home.id}:${vehicle?.id ?? device.entityId}`;
  const [targetDraft, setTargetPercent] = useState<string | null>(() => {
    try {
      return localStorage.getItem(chargeLimitKey);
    } catch {
      return null;
    }
  });
  const targetPercent =
    targetDraft ?? String(chargeLimitControl?.percent ?? 100);
  useEffect(() => {
    if (targetDraft === null) return;
    try {
      localStorage.setItem(chargeLimitKey, targetDraft);
    } catch {
      /* Storage is optional. */
    }
  }, [chargeLimitKey, targetDraft]);
  const [now, setNow] = useState(Date.now);
  const [editing, setEditing] = useState(false);
  const vehicleAction = device.evBattery ? "Edit vehicle" : "Add vehicle";
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [submitted, setSubmitted] = useState<Command | null>(null);
  const inFlight = useRef(false);
  const requestKey = useRef<{
    amps: number;
    limit: number | undefined;
    start: boolean;
    key: string;
  } | null>(null);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const sensor = home.evBatterySensors?.find(
    (s) => s.entityId === device.evBattery?.sensorEntityId,
  );
  const inputs = {
    departure,
    targetPercent,
    batteryKwh: String(device.evBattery?.capacityKwh ?? ""),
    chargePercent: String(sensor?.percent ?? ""),
    efficiencyPercent: String(device.evBattery?.efficiencyPercent ?? 90),
  };
  const plan = chargingPlan(
    inputs,
    device.evCurrent,
    device.evCharging?.wattsPerAmp,
    now,
  );
  const outcome =
    home.commands.find((c) => c.id === submitted?.id) ?? submitted;
  const pending =
    home.commands.some((c) => !terminal(c)) ||
    !!(outcome && !terminal(outcome));
  const limitValid =
    !chargeLimitControl ||
    (chargeLimitControl.percent != null &&
      Number(targetPercent) >= chargeLimitControl.min &&
      Number(targetPercent) <= chargeLimitControl.max &&
      Math.abs(
        (Number(targetPercent) - chargeLimitControl.min) /
          chargeLimitControl.step -
          Math.round(
            (Number(targetPercent) - chargeLimitControl.min) /
              chargeLimitControl.step,
          ),
      ) < 0.000001);
  const blocked =
    stale || !home.connected || home.revoked
      ? "Reconnect your home to apply a charging rate."
      : !device.allowed || device.shutoffLevel === "Never"
        ? "Allow control of this charger in Settings."
        : !device.evBattery
          ? "No unique EV battery match was found. Choose Add vehicle to assign its battery sensor."
          : sensor?.percent == null
            ? "The vehicle’s battery reading is unavailable. Wait for a live reading."
            : !device.evBattery.capacityKwh
              ? "Battery detected. Choose Edit vehicle to enter its usable capacity."
              : !["on", "off"].includes(device.state)
                ? "The charger is unavailable. Wait for a fresh reading."
                : device.evCurrent?.amps == null
                  ? "The charger’s current control is unavailable."
                  : !limitValid
                    ? `Choose an available charge limit from ${chargeLimitControl?.min} to ${chargeLimitControl?.max}% in steps of ${chargeLimitControl?.step}%.`
                    : pending
                      ? "Waiting for pending device commands to finish."
                      : editing
                        ? "Save or close the vehicle dialog before applying a rate."
                        : "";
  async function apply() {
    if (inFlight.current || blocked || editing) return;
    const currentPlan = chargingPlan(
      inputs,
      device.evCurrent,
      device.evCharging?.wattsPerAmp,
      Date.now(),
    );
    if (
      "error" in currentPlan ||
      (currentPlan.targetReached && !chargeLimitControl)
    )
      return;
    const amps = currentPlan.targetReached
      ? device.evCurrent!.amps!
      : currentPlan.amps;
    const limit = chargeLimitControl ? Number(targetPercent) : undefined;
    const start = device.state === "off" && !currentPlan.targetReached;
    inFlight.current = true;
    setBusy(true);
    setError("");
    if (
      requestKey.current?.amps !== amps ||
      requestKey.current?.limit !== limit ||
      requestKey.current?.start !== start
    )
      requestKey.current = { amps, limit, start, key: crypto.randomUUID() };
    try {
      const options: [number?, string?, boolean?] = start
        ? [limit, vehicle?.id, true]
        : vehicle
          ? [limit, vehicle.id]
          : limit === undefined
            ? []
            : [limit];
      const command = await api.evCurrent(
        home.id,
        device.entityId,
        amps,
        requestKey.current!.key,
        ...options,
      );
      setSubmitted(command);
      requestKey.current = null;
      await refresh();
    } catch (failure) {
      setError((failure as Error).message);
    } finally {
      setBusy(false);
      inFlight.current = false;
    }
  }
  return (
    <>
      <section className="card ev-inputs ev-main-card">
        <div className="ev-card-heading">
          <div>
            <span className="eyebrow">YOUR NEXT DEPARTURE</span>
            <h2>{vehicle?.name ?? device.name}</h2>
          </div>
          <Button
            variant="ghost"
            className="ev-edit-button"
            aria-label={vehicleAction}
            title={vehicleAction}
            onClick={() => (onEdit ? onEdit() : setEditing(true))}
            aria-haspopup="dialog"
            disabled={busy}
          >
            {device.evBattery ? (
              <Pencil size={20} aria-hidden="true" />
            ) : (
              <Plus size={20} aria-hidden="true" />
            )}
          </Button>
        </div>
        {vehicle && vehicle.name !== device.name && (
          <p className="ev-help">Charger: {device.name}</p>
        )}
        <label className="field">
          Departure date and time
          <input
            type="datetime-local"
            value={departure}
            onChange={(e) => setDeparture(e.target.value)}
            disabled={busy}
            required
          />
          <small>
            Local time · {Intl.DateTimeFormat().resolvedOptions().timeZone}
          </small>
        </label>
        <label className="field">
          <span className="ev-charge-limit-label">
            <span>Charge limit (%)</span>
            <strong>{targetPercent}%</strong>
          </span>
          <input
            type="range"
            className="smart-slider"
            aria-label="Charge limit (%)"
            aria-valuetext={`${targetPercent}%`}
            style={
              {
                "--smart-slider-fill": `${Math.max(
                  0,
                  Math.min(
                    100,
                    ((Number(targetPercent) -
                      Math.max(1, chargeLimitControl?.min ?? 1)) /
                      ((chargeLimitControl?.max ?? 100) -
                        Math.max(1, chargeLimitControl?.min ?? 1))) *
                      100,
                  ),
                )}%`,
              } as CSSProperties
            }
            min={Math.max(1, chargeLimitControl?.min ?? 1)}
            max={chargeLimitControl?.max ?? 100}
            step={chargeLimitControl?.step ?? 1}
            value={targetPercent}
            onChange={(e) => setTargetPercent(e.target.value)}
            disabled={busy}
          />
          {chargeLimitControl && (
            <small>
              Vehicle limit: {chargeLimitControl.percent ?? "Unavailable"}%
            </small>
          )}
        </label>
        <div className="ev-charge-stats">
          <div className="ev-battery-reading">
            <BatteryCharging size={28} aria-hidden="true" />
            <div>
              <strong>
                {sensor?.percent == null || !home.connected || stale
                  ? "—"
                  : `${Number(sensor.percent.toFixed(1))}%`}
              </strong>
              <span>Current battery</span>
            </div>
          </div>
          {!("error" in plan) &&
            sensor?.percent != null &&
            home.connected &&
            !stale && (
              <div className="ev-charge-rate">
                <Zap size={22} aria-hidden="true" />
                <div>
                  <span>Charge rate</span>
                  <strong>
                    {plan.amps} A <small>· {plan.powerKw.toFixed(2)} kW</small>
                  </strong>
                </div>
              </div>
            )}
        </div>
        {!("error" in plan) && !plan.achievable && (
          <p className="ev-deadline-warning" role="status">
            There isn’t enough time to reach {targetPercent}%. Charging at{" "}
            {plan.maxAmps} A is estimated to reach{" "}
            {plan.chargeAtDeparture.toFixed(1)}% by departure.
          </p>
        )}
        {!("error" in plan) && plan.targetReached && (
          <p>Charge target reached. No additional charging is needed.</p>
        )}
        {(blocked || "error" in plan) && (
          <p className="ev-help">
            {blocked || ("error" in plan ? plan.error : "")}
          </p>
        )}
        {!chargeLimitControl && (
          <p className="ev-help">Set the same charge limit in your vehicle.</p>
        )}
        <Button
          className="ev-start-button"
          onClick={() => void apply()}
          disabled={
            !!blocked ||
            busy ||
            editing ||
            "error" in plan ||
            (plan.targetReached && !chargeLimitControl)
          }
        >
          {busy
            ? "Applying…"
            : !("error" in plan) && plan.targetReached
              ? `Apply ${targetPercent}% limit`
              : device.state === "off"
                ? "Start charge"
                : "Update charging"}
        </Button>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {outcome && (
          <p className="ev-help" role="status">
            {outcome.status === "Confirmed"
              ? outcome.startCharge
                ? "Charging started"
                : `Current confirmed at ${outcome.currentAmps} A`
              : (outcome.message ??
                (terminal(outcome)
                  ? `Charging change ${outcome.status.toLowerCase()}`
                  : "Applying charging settings…"))}
          </p>
        )}
      </section>
      {editing && (
        <BatterySetup
          home={home}
          device={device}
          api={api}
          onClose={() => setEditing(false)}
          onSaved={(h) => {
            updateHome(h);
            setEditing(false);
          }}
        />
      )}
    </>
  );
}

function BatterySetup({
  home,
  device,
  api,
  onClose,
  onSaved,
}: {
  home: Home;
  device: Device;
  api: Api;
  onSaved: (home: Home) => void;
  onClose: () => void;
}) {
  const [adding] = useState(!device.evBattery);
  const [sensorId, setSensorId] = useState(
    device.evBattery?.sensorEntityId ?? "",
  );
  const [capacity, setCapacity] = useState(
    String(device.evBattery?.capacityKwh ?? ""),
  );
  const [efficiency, setEfficiency] = useState(
    String(device.evBattery?.efficiencyPercent ?? 90),
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const saving = useRef(false);
  const sensors = home.evBatterySensors ?? [];
  const valid =
    sensorId &&
    Number(capacity) > 0 &&
    Number(capacity) <= 1000 &&
    Number(efficiency) > 0 &&
    Number(efficiency) <= 100;
  function close() {
    if (!saving.current) onClose();
  }
  return (
    <Modal
      title={adding ? "Add vehicle" : "Edit vehicle"}
      onClose={close}
      className="ev-vehicle-modal"
    >
      <form
        className="ev-vehicle-form"
        onSubmit={async (e) => {
          e.preventDefault();
          if (!valid || saving.current) return;
          saving.current = true;
          setBusy(true);
          setError("");
          try {
            onSaved(
              await api.evBatterySettings(home.id, device.entityId, {
                sensorEntityId: sensorId,
                capacityKwh: Number(capacity),
                efficiencyPercent: Number(efficiency),
              }),
            );
          } catch (failure) {
            setError((failure as Error).message);
          } finally {
            setBusy(false);
            saving.current = false;
          }
        }}
      >
        <div className="ev-setup-fields">
          <label className="field">
            Battery level sensor
            <Select
              value={sensorId}
              onChange={(e) => {
                setSensorId(e.target.value);
                setCapacity(
                  String(
                    sensors.find((s) => s.entityId === e.target.value)
                      ?.capacityKwh ?? "",
                  ),
                );
              }}
              disabled={busy}
              required
            >
              <option value="">Choose a battery sensor</option>
              {sensorId && !sensors.some((s) => s.entityId === sensorId) && (
                <option value={sensorId}>Saved sensor (unavailable)</option>
              )}
              {sensors.map((s) => (
                <option
                  key={s.entityId}
                  value={s.entityId}
                  disabled={home.devices.some(
                    (d) =>
                      d.entityId !== device.entityId &&
                      d.evBattery?.sensorEntityId === s.entityId,
                  )}
                >
                  {s.name} ·{" "}
                  {s.percent == null ? "Unavailable" : `${s.percent}%`}
                </option>
              ))}
            </Select>
          </label>
          <label className="field">
            Usable battery capacity (kWh)
            <input
              type="number"
              min="0.1"
              max="1000"
              step="any"
              value={capacity}
              onChange={(e) => setCapacity(e.target.value)}
              placeholder="e.g. 75"
              disabled={busy}
              required
            />
          </label>
          <label className="field">
            Charging efficiency (%)
            <input
              type="number"
              min="0.1"
              max="100"
              step="any"
              value={efficiency}
              onChange={(e) => setEfficiency(e.target.value)}
              disabled={busy}
              required
            />
          </label>
        </div>
        {!sensors.length && (
          <p>
            Home Assistant must expose a sensor with battery device class and %
            units.
          </p>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="ev-vehicle-actions">
          {device.evBattery && !device.evBattery.autoDetected && (
            <Button
              type="button"
              variant="secondary"
              disabled={busy || home.revoked}
              onClick={async () => {
                if (saving.current) return;
                saving.current = true;
                setBusy(true);
                setError("");
                try {
                  onSaved(
                    await api.evBatterySettings(home.id, device.entityId, null),
                  );
                } catch (failure) {
                  setError((failure as Error).message);
                } finally {
                  saving.current = false;
                  setBusy(false);
                }
              }}
            >
              Use automatic detection
            </Button>
          )}
          <Button type="button" variant="ghost" disabled={busy} onClick={close}>
            Cancel
          </Button>
          <Button disabled={!valid || busy || home.revoked}>
            {busy ? "Saving…" : adding ? "Add vehicle" : "Save changes"}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
