import { Select } from "../../components/ui/Select";
import { Info } from "lucide-react";
import { useState } from "react";
import type { Api } from "../../lib/api";
import type { Home, ShutoffLevel, EvChargingSettings } from "../../lib/types";
import { Modal } from "../../components/ui/Modal";
import { Button } from "../../components/ui/Button";
import { detectDeviceCategory } from "./categoryDetection";
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
  const categories = home.deviceCategories ?? [];
  const [detectedCategories] = useState(() =>
    Object.fromEntries(
      home.devices.map((d) => [
        d.entityId,
        detectDeviceCategory(d, categories, home.powerSensors),
      ]),
    ),
  );
  const [standards, setStandards] = useState(() =>
    Object.fromEntries(
      home.devices.map((d) => [
        d.entityId,
        {
          category: d.category ?? detectedCategories[d.entityId],
          watts:
            d.standardWattsOverride == null
              ? ""
              : String(d.standardWattsOverride),
        },
      ]),
    ),
  );
  const invalidStandards = Object.values(standards).some(
    ({ category, watts }) =>
      !!category &&
      ((category === "custom" && watts.trim() === "") ||
        (watts !== "" &&
          (watts.trim() === "" ||
            !Number.isFinite(Number(watts)) ||
            Number(watts) < 1 ||
            Number(watts) > 1_000_000))),
  );
  const [evCharging, setEvCharging] = useState<
    Record<string, EvChargingSettings | null>
  >(() =>
    Object.fromEntries(
      home.devices
        .filter((d) => d.entityId.startsWith("switch.") && !d.isCircuit)
        .map((d) => [d.entityId, d.evCharging ?? null]),
    ),
  );
  const invalidEv = Object.values(evCharging).some(
    (config) =>
      config &&
      (!Number.isFinite(config.wattsPerAmp) ||
        config.wattsPerAmp < 100 ||
        config.wattsPerAmp > 1000),
  );
  const [smartEnabled, setSmartEnabled] = useState(!!home.smartPowerOffEnabled);
  const [showSmartInfo, setShowSmartInfo] = useState(false);
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
  const [levels, setLevels] = useState<Record<string, ShutoffLevel>>(() =>
    Object.fromEntries(
      home.devices.map((d) => [
        d.entityId,
        d.allowed ? (d.shutoffLevel ?? "Sometimes") : "Never",
      ]),
    ),
  );
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
    if (invalidLimits || invalidEv || invalidStandards) return;
    setBusy(true);
    setError("");
    try {
      const updatedHome = await api.settings(home.id, {
        evCharging,
        devicePowerStandards: Object.fromEntries(
          Object.entries(standards).map(([id, config]) => [
            id,
            config.category
              ? {
                  category: config.category,
                  standardWatts:
                    config.watts === "" ? null : Number(config.watts),
                }
              : null,
          ]),
        ),
        thermostatLimits: Object.fromEntries(
          Object.entries(thermostatLimits).map(([id, range]) => [
            id,
            { minF: Number(range.minF), maxF: Number(range.maxF) },
          ]),
        ),
        smartPowerOffEnabled: smartEnabled,
        shutoffLevels: levels,
        allowAll: false,
        allowFutureDevices: future,
        allowedEntityIds: Object.keys(levels).filter(
          (id) => levels[id] !== "Never",
        ),
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
            <Select
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
            </Select>
          </label>
        </div>
      </section>
      <section className="smart-shutoff-settings" aria-label="Smart Shutoff">
        <div className="smart-shutoff-heading">
          <h3>Smart Shutoff</h3>
          <button
            type="button"
            className="info-button"
            aria-label="About Smart Shutoff"
            aria-expanded={showSmartInfo}
            aria-controls="smart-shutoff-help"
            onClick={() => setShowSmartInfo(!showSmartInfo)}
          >
            <Info size={18} aria-hidden="true" />
          </button>
        </div>
        {showSmartInfo && (
          <p className="muted" id="smart-shutoff-help">
            Smart Shutoff acts at medium or high grid outage risk to keep usage
            below 11 kW. Low risk allows usage above 11 kW. Never devices are
            left alone. When Anomaly savings detection is on, Sometimes and
            Anytime devices can also turn off for excess usage. Sometimes
            devices need your approval at medium risk and can turn off
            automatically at high risk. Anytime devices can turn off
            automatically at either elevated level. Paired Anytime EV chargers
            reduce their current first when they can keep charging below 11 kW.
            Devices turned off by Base Layer join a restore queue, using their
            power draw before shutoff as an estimate. When there is enough spare
            capacity or grid risk returns to low, they turn back on one at a
            time. Demo timing: 15 seconds off, 5 seconds of stable headroom, and
            5 seconds between restorations. Reduced EVs gradually return to
            their original current limit as capacity opens up. At elevated risk
            we leave at least 0.5 kW of spare capacity. Unknown usage pauses
            restoration.
          </p>
        )}
        <label className="meter-toggle">
          <input
            type="checkbox"
            role="switch"
            checked={smartEnabled}
            onChange={(e) => setSmartEnabled(e.target.checked)}
          />
          Enable Smart Shutoff and automatic restore
        </label>
      </section>
      <h3>Devices</h3>
      <p className="muted">
        Recognized appliances have a category selected automatically. Review or
        change it before saving, and choose a category for unrecognized devices
        to monitor their estimated standard power draw. A reading at least 50%
        above standard triggers an alert and, when Anomaly savings and device
        permissions allow, automatic shutoff. Set watts from the appliance label
        or its normal operating draw to refine the estimate. Watts measure
        power; 1,200 W used continuously for one hour is 1.2 kWh.
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
      <table className="device-settings-table" role="table">
        <caption className="visually-hidden">
          Device sensors and shutoff permissions
        </caption>
        <thead role="rowgroup">
          <tr role="row">
            <th scope="col" role="columnheader">
              Device
            </th>
            <th scope="col" role="columnheader">
              Power sensor
            </th>
            <th scope="col" role="columnheader" className="shutoff-cell">
              <span>Smart Shutoff</span>
            </th>
          </tr>
        </thead>
        <tbody role="rowgroup">
          {home.devices
            .filter((d) =>
              `${d.name} ${d.entityId}`
                .toLowerCase()
                .includes(search.toLowerCase()),
            )
            .map((d) => (
              <tr key={d.entityId} role="row">
                <th scope="row" role="rowheader">
                  <span>{d.name}</span>
                  <fieldset className="temperature-limits">
                    <legend>Anomaly detection</legend>
                    <label className="field">
                      Device category
                      <Select
                        aria-label={`${d.name} category`}
                        value={standards[d.entityId].category}
                        onChange={(e) =>
                          setStandards((current) => ({
                            ...current,
                            [d.entityId]: {
                              category: e.target.value,
                              watts: "",
                            },
                          }))
                        }
                      >
                        <option value="">
                          Unassigned — no standard monitoring
                        </option>
                        {categories.map((category) => (
                          <option key={category.id} value={category.id}>
                            {category.name}
                          </option>
                        ))}
                      </Select>
                    </label>
                    {detectedCategories[d.entityId] &&
                      standards[d.entityId].category ===
                        detectedCategories[d.entityId] && (
                        <small className="muted">
                          Auto-detected category — review before saving
                        </small>
                      )}
                    {standards[d.entityId].category && (
                      <>
                        <label className="field">
                          Standard power (W)
                          <input
                            type="number"
                            min="1"
                            max="1000000"
                            step="any"
                            aria-label={`${d.name} standard power (W)`}
                            placeholder={String(
                              categories.find(
                                (c) => c.id === standards[d.entityId].category,
                              )?.standardWatts ?? "Enter watts",
                            )}
                            value={standards[d.entityId].watts}
                            onChange={(e) =>
                              setStandards((current) => ({
                                ...current,
                                [d.entityId]: {
                                  ...current[d.entityId],
                                  watts: e.target.value,
                                },
                              }))
                            }
                          />
                        </label>
                        <small className="muted">
                          {(() => {
                            const config = standards[d.entityId];
                            const watts =
                              config.watts === ""
                                ? categories.find(
                                    (c) => c.id === config.category,
                                  )?.standardWatts
                                : Number(config.watts);
                            return watts && Number.isFinite(watts) && watts > 0
                              ? `${config.watts === "" ? "Category estimate" : "Custom standard"}: ${watts.toLocaleString()} W. Action at ${(watts * 1.5).toLocaleString()} W or more.`
                              : "Enter standard power to enable monitoring.";
                          })()}
                        </small>
                      </>
                    )}
                  </fieldset>
                </th>
                <td role="cell" data-label="Power sensor">
                  <Select
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
                  </Select>
                  {!d.powerSensorId &&
                    mapping[d.entityId] &&
                    mapping[d.entityId] === suggestions[d.entityId] && (
                      <small className="muted">Suggested match</small>
                    )}
                  {d.entityId.startsWith("switch.") && !d.isCircuit && (
                    <fieldset className="temperature-limits">
                      <legend>EV charging</legend>
                      <label className="field">
                        Charging current control
                        <Select
                          aria-label={`${d.name} EV current control`}
                          value={evCharging[d.entityId]?.currentEntityId ?? ""}
                          onChange={(e) =>
                            setEvCharging((current) => ({
                              ...current,
                              [d.entityId]: e.target.value
                                ? {
                                    currentEntityId: e.target.value,
                                    wattsPerAmp:
                                      current[d.entityId]?.wattsPerAmp ?? 240,
                                  }
                                : null,
                            }))
                          }
                        >
                          <option value="">
                            Not an EV / no current control
                          </option>
                          {evCharging[d.entityId] &&
                            !(home.currentControls ?? []).some(
                              (c) =>
                                c.entityId ===
                                evCharging[d.entityId]?.currentEntityId,
                            ) && (
                              <option
                                value={evCharging[d.entityId]!.currentEntityId}
                              >
                                Saved control (unavailable)
                              </option>
                            )}
                          {(home.currentControls ?? []).map((c) => (
                            <option
                              key={c.entityId}
                              value={c.entityId}
                              disabled={Object.entries(evCharging).some(
                                ([id, config]) =>
                                  id !== d.entityId &&
                                  config?.currentEntityId === c.entityId,
                              )}
                            >
                              {c.name} ({c.min}–{c.max} A)
                            </option>
                          ))}
                        </Select>
                      </label>
                      {evCharging[d.entityId] && (
                        <label className="field">
                          Charger watts per amp
                          <input
                            type="number"
                            min="100"
                            max="1000"
                            step="any"
                            aria-label={`${d.name} charger watts per amp`}
                            value={
                              Number.isNaN(evCharging[d.entityId]!.wattsPerAmp)
                                ? ""
                                : evCharging[d.entityId]!.wattsPerAmp
                            }
                            onChange={(e) =>
                              setEvCharging((current) => ({
                                ...current,
                                [d.entityId]: {
                                  ...current[d.entityId]!,
                                  wattsPerAmp: e.target.valueAsNumber,
                                },
                              }))
                            }
                          />
                          <small className="muted">
                            240 for 240 V single-phase; 690 for 230 V
                            three-phase. Anytime EVs reduce current before
                            shutoff. Pair a power sensor above.
                          </small>
                        </label>
                      )}
                    </fieldset>
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
                <td
                  role="cell"
                  className="shutoff-cell"
                  data-label="Smart Shutoff"
                >
                  <Select
                    aria-label={`${d.name} Smart Shutoff`}
                    value={levels[d.entityId]}
                    onChange={(e) =>
                      setLevels((current) => ({
                        ...current,
                        [d.entityId]: e.target.value as ShutoffLevel,
                      }))
                    }
                  >
                    <option value="Never">Never</option>
                    <option value="Sometimes">Sometimes</option>
                    <option
                      value="Anytime"
                      disabled={d.entityId.startsWith("climate.")}
                    >
                      Anytime
                    </option>
                  </Select>
                  {d.entityId.startsWith("climate.") && (
                    <small className="muted">Temperature control only</small>
                  )}
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
        Set new devices to Sometimes (automatic shutoff at high grid risk)
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
      {invalidStandards && (
        <p role="alert">
          Enter standard power from 1 to 1,000,000 W. Other / custom requires a
          value.
        </p>
      )}
      {invalidEv && (
        <p role="alert">
          Enter a charger rating from 100 to 1000 watts per amp.
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
        <Button
          disabled={busy || invalidLimits || invalidEv || invalidStandards}
          onClick={() => void save()}
        >
          {busy ? "Saving…" : "Save settings"}
        </Button>
      </div>
    </Modal>
  );
}
