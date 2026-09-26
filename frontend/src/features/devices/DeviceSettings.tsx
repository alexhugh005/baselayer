import { useEffect, useRef, useState } from "react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
import { Modal } from "../../components/ui/Modal";
import { Button } from "../../components/ui/Button";
import {
  sensorMatchScore,
  smartMatchPowerSensors,
} from "./powerSensorMatching";
export function DeviceSettings({
  home,
  api,
  onClose,
  onSaved,
}: {
  home: Home;
  api: Api;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [allowed, setAllowed] = useState(
    home.devices.filter((d) => d.allowed).map((d) => d.entityId),
  );
  const all =
    home.devices.length > 0 &&
    home.devices.every((d) => allowed.includes(d.entityId));
  const selectAll = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (selectAll.current)
      selectAll.current.indeterminate = allowed.length > 0 && !all;
  }, [allowed, all]);
  const [future, setFuture] = useState(home.allowFutureDevices),
    [meter, setMeter] = useState(home.householdPowerSensorId ?? "");
  const [powerSource, setPowerSource] = useState(
    home.powerSource ?? "wholeHouseMeter",
  );
  const [suggestions] = useState(() =>
    smartMatchPowerSensors(
      home.devices,
      home.powerSensors,
      home.householdPowerSensorId,
    ),
  );
  const [search, setSearch] = useState("");
  const [mapping, setMapping] = useState<Record<string, string>>(suggestions),
    [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  async function save() {
    setBusy(true);
    setError("");
    try {
      await api.settings(home.id, {
        allowAll: all,
        allowFutureDevices: future,
        allowedEntityIds: allowed,
        powerSource,
        householdPowerSensorId:
          powerSource === "wholeHouseMeter" ? meter || null : null,
        devicePowerSensors: Object.fromEntries(
          Object.entries(mapping).filter(([, v]) => v),
        ),
      });
      onSaved();
      onClose();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      title="Device settings"
      onClose={onClose}
      className="device-settings"
    >
      <p>
        Choose which devices Base Layer can control. Every shutoff needs your
        approval.
      </p>
      <fieldset className="power-source-options">
        <legend>Measure usage</legend>
        <label>
          <input
            type="radio"
            name="power-source"
            checked={powerSource === "wholeHouseMeter"}
            onChange={() => {
              setPowerSource("wholeHouseMeter");
              setMapping((current) =>
                Object.fromEntries(
                  Object.entries(current).map(([id, sensor]) => [
                    id,
                    sensor === meter ? "" : sensor,
                  ]),
                ),
              );
            }}
          />{" "}
          Whole-house meter
        </label>
        <label>
          <input
            type="radio"
            name="power-source"
            checked={powerSource === "deviceSum"}
            onChange={() => setPowerSource("deviceSum")}
          />{" "}
          Sum of device readings
        </label>
      </fieldset>
      {powerSource === "wholeHouseMeter" ? (
        <label className="field">
          Whole-house power meter
          <select
            value={meter}
            onChange={(e) => {
              const next = e.target.value;
              setMeter(next);
              setMapping((current) =>
                Object.fromEntries(
                  Object.entries(current).map(([id, sensor]) => [
                    id,
                    sensor === next ? "" : sensor,
                  ]),
                ),
              );
            }}
          >
            <option value="">No meter — usage unknown</option>
            {home.powerSensors.map((s) => (
              <option key={s.entityId} value={s.entityId}>
                {s.name} ({s.unit}) · {s.entityId}
              </option>
            ))}
          </select>
        </label>
      ) : (
        <p className="source-explanation">
          Includes all assigned sensors, even for view-only devices and standby
          usage. Other loads are excluded. Avoid overlapping meters, such as a
          power strip and its appliances. A missing reading makes the total
          unknown.
        </p>
      )}
      <p className="muted">
        Review suggested sensor matches before saving. Leave devices without a
        sensor as “Power unknown.”
      </p>
      <div className="permission-options">
        <label>
          <input
            type="checkbox"
            ref={selectAll}
            checked={all}
            onChange={(e) => {
              setAllowed(
                e.target.checked ? home.devices.map((d) => d.entityId) : [],
              );
            }}
          />{" "}
          Allow all current devices
        </label>
        <label>
          <input
            type="checkbox"
            checked={future}
            onChange={(e) => setFuture(e.target.checked)}
          />{" "}
          Allow future devices
        </label>
      </div>
      <label className="field">
        Find a device
        <input
          type="search"
          placeholder="Search by name or entity ID"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </label>
      <p className="mapping-summary">
        {Object.values(mapping).filter(Boolean).length} of {home.devices.length}{" "}
        devices metered. Check a device to allow control.
      </p>
      <div className="mapping-list">
        {home.devices
          .filter((d) =>
            `${d.name} ${d.entityId}`
              .toLowerCase()
              .includes(search.toLowerCase()),
          )
          .map((d) => (
            <div className="mapping-row" key={d.entityId}>
              <label>
                <input
                  type="checkbox"
                  checked={allowed.includes(d.entityId)}
                  onChange={(e) => {
                    setAllowed((v) =>
                      e.target.checked
                        ? [...v, d.entityId]
                        : v.filter((id) => id !== d.entityId),
                    );
                  }}
                />
                {d.name}
              </label>
              <small className="muted">
                {d.entityId}
                {!d.powerSensorId &&
                mapping[d.entityId] &&
                mapping[d.entityId] === suggestions[d.entityId]
                  ? " · Suggested match"
                  : ""}
              </small>
              <select
                aria-label={`${d.name} power sensor`}
                value={mapping[d.entityId] ?? ""}
                onChange={(e) =>
                  setMapping((v) => ({ ...v, [d.entityId]: e.target.value }))
                }
              >
                <option value="">Power unknown</option>
                {[...home.powerSensors]
                  .sort(
                    (a, b) =>
                      sensorMatchScore(d, b) - sensorMatchScore(d, a) ||
                      a.name.localeCompare(b.name),
                  )
                  .map((s) => (
                    <option
                      key={s.entityId}
                      value={s.entityId}
                      disabled={
                        (powerSource === "wholeHouseMeter" &&
                          s.entityId === meter) ||
                        Object.entries(mapping).some(
                          ([id, sensor]) =>
                            id !== d.entityId && sensor === s.entityId,
                        )
                      }
                    >
                      {s.name} ({s.unit}) · {s.entityId}
                    </option>
                  ))}
              </select>
            </div>
          ))}
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <p className="muted">
        Home Assistant grants account-level access. These settings limit what
        Base Layer can control.
      </p>
      <div className="modal-actions">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button disabled={busy} onClick={() => void save()}>
          {busy ? "Saving…" : "Save settings"}
        </Button>
      </div>
    </Modal>
  );
}
