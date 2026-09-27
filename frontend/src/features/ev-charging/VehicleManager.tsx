import { useRef, useState } from "react";
import type { Api } from "../../lib/api";
import type { EvVehicle, Home } from "../../lib/types";
import { Button } from "../../components/ui/Button";
import { Modal } from "../../components/ui/Modal";
import { Select } from "../../components/ui/Select";
import { EvDeparturePlanner } from "./EvCharging";

export function VehicleManager(props: {
  home: Home;
  api: Api;
  stale: boolean;
  refresh: () => Promise<void>;
  updateHome: (home: Home) => void;
}) {
  const { home } = props;
  const vehicles =
    home.evVehicles ??
    home.devices
      .filter((d) => d.evCharging && d.evBattery)
      .map((d) => ({
        id: d.entityId,
        name: d.name,
        chargerEntityId: d.entityId,
        battery: d.evBattery!,
      }));
  const [selected, setSelected] = useState("");
  const [editing, setEditing] = useState<EvVehicle | "new" | null>(null);
  const vehicle = vehicles.find((v) => v.id === selected) ?? vehicles[0];
  const device = home.devices.find(
    (d) => d.entityId === vehicle?.chargerEntityId && d.evCharging,
  );
  const hasCharger = home.devices.some((d) => d.evCharging);
  return (
    <>
      <div className="ev-vehicle-toolbar">
        {!!vehicles.length && (
          <label className="smart-home-picker">
            Vehicle
            <Select
              value={vehicle.id}
              onChange={(e) => setSelected(e.target.value)}
            >
              {vehicles.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name}
                </option>
              ))}
            </Select>
          </label>
        )}
        <Button
          onClick={() => setEditing("new")}
          disabled={!hasCharger || home.revoked}
          aria-haspopup="dialog"
        >
          Add vehicle
        </Button>
      </div>
      {vehicle && device ? (
        <EvDeparturePlanner
          key={vehicle.id}
          {...props}
          device={{ ...device, evBattery: vehicle.battery }}
          vehicle={vehicle}
          onEdit={() => setEditing(vehicle)}
        />
      ) : (
        <section className="card empty">
          <h2>
            {vehicle
              ? vehicle.name
              : hasCharger
                ? "Add your first vehicle"
                : "Pair your EV charger"}
          </h2>
          <p>
            {vehicle
              ? "This vehicle’s charger is unavailable. Edit the vehicle to assign a paired charger."
              : hasCharger
                ? "Save each vehicle with its battery sensor and capacity."
                : "Pair the charger’s power sensor and current control in Settings."}
          </p>
          {vehicle && (
            <Button variant="secondary" onClick={() => setEditing(vehicle)}>
              Edit vehicle
            </Button>
          )}
          {!hasCharger && (
            <a className="button secondary" href="/settings">
              Open Settings
            </a>
          )}
        </section>
      )}
      {editing && (
        <VehicleEditor
          key={editing === "new" ? "new" : editing.id}
          home={home}
          api={props.api}
          vehicle={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(updated, id) => {
            props.updateHome(updated);
            setSelected(id);
            setEditing(null);
          }}
        />
      )}
    </>
  );
}

function VehicleEditor({
  home,
  api,
  vehicle,
  onClose,
  onSaved,
}: {
  home: Home;
  api: Api;
  vehicle?: EvVehicle;
  onClose: () => void;
  onSaved: (home: Home, id: string) => void;
}) {
  const [id] = useState(() => vehicle?.id ?? crypto.randomUUID());
  const [name, setName] = useState(vehicle?.name ?? "");
  const chargers = home.devices.filter((d) => d.evCharging);
  const [chargerId, setChargerId] = useState(
    vehicle?.chargerEntityId ?? chargers[0]?.entityId ?? "",
  );
  const initialBattery =
    vehicle?.battery ??
    chargers.find((d) => d.entityId === chargerId)?.evBattery;
  const [sensorId, setSensorId] = useState(
    initialBattery?.sensorEntityId ?? "",
  );
  const [capacity, setCapacity] = useState(
    String(initialBattery?.capacityKwh ?? ""),
  );
  const [efficiency, setEfficiency] = useState(
    String(initialBattery?.efficiencyPercent ?? 90),
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const saving = useRef(false);
  const sensors = home.evBatterySensors ?? [];
  const valid =
    name.trim() &&
    name.trim().length <= 100 &&
    chargers.some((d) => d.entityId === chargerId) &&
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
      title={vehicle ? "Edit vehicle" : "Add vehicle"}
      onClose={close}
      className="ev-vehicle-modal"
    >
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          if (!valid || saving.current) return;
          saving.current = true;
          setBusy(true);
          setError("");
          try {
            const updated = await api.saveEvVehicle(home.id, {
              id,
              name: name.trim(),
              chargerEntityId: chargerId,
              battery: {
                sensorEntityId: sensorId,
                capacityKwh: Number(capacity),
                efficiencyPercent: Number(efficiency),
              },
            });
            onSaved(updated, id);
          } catch (failure) {
            setError((failure as Error).message);
          } finally {
            saving.current = false;
            setBusy(false);
          }
        }}
      >
        <div className="ev-setup-fields">
          <label className="field">
            Vehicle name
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              maxLength={100}
              required
              disabled={busy}
              placeholder="e.g. Family car"
            />
          </label>
          <label className="field">
            Charger
            <Select
              value={chargerId}
              disabled={busy}
              onChange={(e) => {
                setChargerId(e.target.value);
                if (!vehicle) {
                  const battery = chargers.find(
                    (d) => d.entityId === e.target.value,
                  )?.evBattery;
                  setSensorId(battery?.sensorEntityId ?? "");
                  setCapacity(String(battery?.capacityKwh ?? ""));
                }
              }}
            >
              {!chargers.some((d) => d.entityId === chargerId) && (
                <option value={chargerId}>Saved charger (unavailable)</option>
              )}
              {chargers.map((d) => (
                <option key={d.entityId} value={d.entityId}>
                  {d.name}
                </option>
              ))}
            </Select>
          </label>
          <label className="field">
            Battery level sensor
            <Select
              value={sensorId}
              disabled={busy}
              required
              onChange={(e) => {
                setSensorId(e.target.value);
                setCapacity(
                  String(
                    sensors.find((s) => s.entityId === e.target.value)
                      ?.capacityKwh ?? "",
                  ),
                );
              }}
            >
              <option value="">Choose a battery sensor</option>
              {sensorId && !sensors.some((s) => s.entityId === sensorId) && (
                <option value={sensorId}>Saved sensor (unavailable)</option>
              )}
              {sensors.map((s) => (
                <option
                  key={s.entityId}
                  value={s.entityId}
                  disabled={home.evVehicles?.some(
                    (v) =>
                      v.id !== id &&
                      v.chargerEntityId !== chargerId &&
                      v.battery.sensorEntityId === s.entityId,
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
              required
              disabled={busy}
              value={capacity}
              onChange={(e) => setCapacity(e.target.value)}
            />
          </label>
          <label className="field">
            Charging efficiency (%)
            <input
              type="number"
              min="0.1"
              max="100"
              step="any"
              required
              disabled={busy}
              value={efficiency}
              onChange={(e) => setEfficiency(e.target.value)}
            />
          </label>
        </div>
        <p className="ev-help">
          Vehicles can share a charger. Select the vehicle that is plugged in
          before applying its plan.
        </p>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="ev-vehicle-actions">
          <Button type="button" variant="ghost" disabled={busy} onClick={close}>
            Cancel
          </Button>
          <Button disabled={!valid || busy || home.revoked}>
            {busy ? "Saving…" : vehicle ? "Save changes" : "Add vehicle"}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
