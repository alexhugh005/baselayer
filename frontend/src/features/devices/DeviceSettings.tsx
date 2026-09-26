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
  onSaved: (home: Home) => void;
}) {
  const [thermostatLimits, setThermostatLimits] = useState(() =>
    Object.fromEntries(
      home.devices
        .filter((d) => d.entityId.startsWith("climate."))
        .map((d) => [
          d.entityId,
          {
            minF: String(d.thermostatMinF ?? 66),
            maxF: String(d.thermostatMaxF ?? 80),
          },
        ]),
    ),
  );
  const invalidLimits = Object.values(thermostatLimits).some(
    (range) =>
      range.minF.trim() === "" ||
      range.maxF.trim() === "" ||
      !/^[1-9]\d$/.test(range.minF) ||
      !/^[1-9]\d$/.test(range.maxF) ||
      Number(range.minF) >= Number(range.maxF),
  );
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
    if (invalidLimits) return;
    setBusy(true);
    setError("");
    try {
      const updatedHome = await api.settings(home.id, {
        thermostatLimits: Object.fromEntries(
          Object.entries(thermostatLimits).map(([id, range]) => [
            id,
            { minF: Number(range.minF), maxF: Number(range.maxF) },
          ]),
        ),
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
      onSaved(updatedHome);
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
      <section className="energy-source" aria-label="Total usage">
        <h3>Total usage</h3>
        <label className="meter-toggle">
          <input
            type="checkbox"
            role="switch"
            checked={powerSource === "wholeHouseMeter"}
            aria-controls="whole-home-meter"
            onChange={(e) => {
              setPowerSource(
                e.target.checked ? "wholeHouseMeter" : "deviceSum",
              );
              if (e.target.checked) {
                setMapping((current) =>
                  Object.fromEntries(
                    Object.entries(current).map(([id, sensor]) => [
                      id,
                      sensor === meter ? "" : sensor,
                    ]),
                  ),
                );
              }
            }}
          />
          I have a whole-home meter
        </label>
        <div id="whole-home-meter" hidden={powerSource !== "wholeHouseMeter"}>
          <label className="field">
            Whole-home power sensor
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
              {home.powerSensors.map((sensor) => (
                <option key={sensor.entityId} value={sensor.entityId}>
                  {sensor.name} ({sensor.unit})
                </option>
              ))}
            </select>
          </label>
        </div>
      </section>
      <h3>Devices</h3>
      <p className="muted">
        Pair sensors to show usage. Every shutoff needs approval.
      </p>
      <label className="field">
        Find a device
        <input
          type="search"
          placeholder="Search devices"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </label>
      <table className="device-settings-table">
        <caption className="visually-hidden">
          Device sensors and shutoff permissions
        </caption>
        <thead>
          <tr>
            <th scope="col">Device</th>
            <th scope="col">Power sensor</th>
            <th scope="col" className="shutoff-cell">
              <span>Allow shutoff</span>
              <label className="shutoff-checkbox">
                <input
                  type="checkbox"
                  ref={selectAll}
                  checked={all}
                  aria-label="Allow shutoff for all current devices"
                  onChange={(e) =>
                    setAllowed(
                      e.target.checked
                        ? home.devices.map((d) => d.entityId)
                        : [],
                    )
                  }
                />
              </label>
            </th>
          </tr>
        </thead>
        <tbody>
          {home.devices
            .filter((d) =>
              `${d.name} ${d.entityId}`
                .toLowerCase()
                .includes(search.toLowerCase()),
            )
            .map((d) => (
              <tr key={d.entityId}>
                <th scope="row">
                  <span>{d.name}</span>
                </th>
                <td>
                  <select
                    aria-label={`${d.name} power sensor`}
                    value={mapping[d.entityId] ?? ""}
                    onChange={(e) =>
                      setMapping((v) => ({
                        ...v,
                        [d.entityId]: e.target.value,
                      }))
                    }
                  >
                    <option value="">No sensor — usage unknown</option>
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
                          {s.name} ({s.unit})
                        </option>
                      ))}
                  </select>
                  {!d.powerSensorId &&
                    mapping[d.entityId] &&
                    mapping[d.entityId] === suggestions[d.entityId] && (
                      <small className="muted">Suggested match</small>
                    )}
                  {thermostatLimits[d.entityId] && (
                    <fieldset className="temperature-limits">
                      <legend>Temperature limits</legend>
                      <div className="temperature-limit-fields">
                        {(["minF", "maxF"] as const).map((key) => (
                          <label className="field" key={key}>
                            {key === "minF" ? "Minimum (°F)" : "Maximum (°F)"}
                            <input
                              type="text"
                              inputMode="numeric"
                              maxLength={2}
                              pattern="[1-9][0-9]"
                              aria-label={`${d.name} ${key === "minF" ? "minimum" : "maximum"} temperature (°F)`}
                              value={thermostatLimits[d.entityId][key]}
                              onChange={(e) =>
                                setThermostatLimits((current) => ({
                                  ...current,
                                  [d.entityId]: {
                                    ...current[d.entityId],
                                    [key]: e.target.value,
                                  },
                                }))
                              }
                            />
                          </label>
                        ))}
                      </div>
                    </fieldset>
                  )}
                </td>
                <td className="shutoff-cell">
                  <label className="shutoff-checkbox">
                    <input
                      type="checkbox"
                      aria-label={`Allow shutoff for ${d.name}`}
                      checked={allowed.includes(d.entityId)}
                      onChange={(e) =>
                        setAllowed((v) =>
                          e.target.checked
                            ? [...v, d.entityId]
                            : v.filter((id) => id !== d.entityId),
                        )
                      }
                    />
                  </label>
                </td>
              </tr>
            ))}
        </tbody>
      </table>
      {!home.devices.some((d) =>
        `${d.name} ${d.entityId}`.toLowerCase().includes(search.toLowerCase()),
      ) && (
        <p className="muted">
          {home.devices.length ? "No matching devices." : "No devices found."}
        </p>
      )}
      <label className="future-device-access">
        <input
          type="checkbox"
          checked={future}
          onChange={(e) => setFuture(e.target.checked)}
        />
        Allow shutoff for new devices automatically
      </label>
      <details className="sensor-pairing-help">
        <summary>About sensor pairing</summary>
        <p>
          Without a sensor, device usage is unknown; shutoff can still be
          allowed. When adding device readings, unpaired loads are excluded.
          Include each load once. A missing paired reading makes the total
          unknown.
        </p>
      </details>
      {invalidLimits && (
        <p role="alert">
          Enter two-digit temperatures with the minimum below the maximum.
        </p>
      )}
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
        <Button disabled={busy || invalidLimits} onClick={() => void save()}>
          {busy ? "Saving…" : "Save settings"}
        </Button>
      </div>
    </Modal>
  );
}
