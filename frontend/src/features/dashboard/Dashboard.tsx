import { Select } from "../../components/ui/Select";
import { useRef, useState } from "react";
import {
  ArrowUpRight,
  Plus,
  ShieldCheck,
  TriangleAlert,
  Power,
  House,
  RefreshCw,
  Settings2,
  Trash2,
} from "lucide-react";
import type { Api } from "../../lib/api";
import type { Home, ShutoffLevel } from "../../lib/types";
import { DeleteHomeDialog } from "../connections/DeleteHomeDialog";
import { useHomes } from "./useHomes";
import { recommendationIds, formatPower } from "./power";
import { smartPowerOffMessage } from "./smartPowerOffMessage";
import { AnomalyRecommendation, anomalyDevices } from "./AnomalyRecommendation";
import { AnomalySavingsSetting } from "./AnomalySavings";
import { SmartShutoffSetting } from "./SmartShutoffSetting";
import { RestoreQueue } from "../devices/RestoreQueue";
import { UsageOverview } from "./UsageOverview";
import { ProjectedUsage } from "./ProjectedUsage";
import { UsageAlerts, isUsageHigh } from "./UsageAlerts";
import { DeviceSettings } from "../devices/DeviceSettings";
import { DeviceList } from "../devices/DeviceList";
import { smartMatchPowerSensors } from "../devices/powerSensorMatching";
import { useShutoffApproval } from "../commands/useShutoffApproval";
import { ShutoffConfirmation } from "../commands/ShutoffConfirmation";
import { CommandHistory } from "../commands/CommandHistory";
import { PairHome } from "../connections/PairHome";
import { Button } from "../../components/ui/Button";
import { Modal } from "../../components/ui/Modal";
import { LoadingStatus } from "../../components/ui/LoadingStatus";
import { CollapsingNotice } from "../../components/ui/CollapsingNotice";
export function Dashboard({
  api,
  userId = "local",
}: {
  api: Api;
  userId?: string;
}) {
  const [deletingHome, setDeletingHome] = useState<Home | null>(null);
  const [recommendationKind, setRecommendationKind] = useState<
    "usage" | "anomaly"
  >("usage");
  const [recommendationsHome, setRecommendationsHome] = useState<string | null>(
    null,
  );
  const { homes, loading, error, refresh, updateHome, removeHome } =
    useHomes(api);
  const [active, setActive] = useState(
      new URLSearchParams(window.location.search).get("home") ??
        new URLSearchParams(window.location.search).get("connected") ??
        "",
    ),
    [settings, setSettings] = useState(
      !!new URLSearchParams(window.location.search).get("connected"),
    ),
    [pairing, setPairing] = useState(false);
  const home =
    homes.find((h) => h.id === active) ??
    homes.find((h) => !h.revoked) ??
    homes[0];
  const unusualDevices = home ? anomalyDevices(home) : [];
  const reviewDevices =
    recommendationKind === "anomaly"
      ? unusualDevices
      : (home?.devices.filter((d) => d.recommended) ?? []);
  const devices = home?.devices.filter((device) => !device.isCircuit) ?? [];
  const savingDeviceSettings = useRef(false);
  const [connectingDevice, setConnectingDevice] = useState("");
  const [changingShutoffDevice, setChangingShutoffDevice] = useState("");
  const [shutoffLevelError, setShutoffLevelError] = useState({
    homeId: "",
    message: "",
  });
  const [powerSourceError, setPowerSourceError] = useState({
    homeId: "",
    message: "",
  });
  const powerSuggestions = home
    ? smartMatchPowerSensors(
        home.devices,
        home.powerSensors,
        home.householdPowerSensorId,
      )
    : {};
  async function connectSuggestedPowerSource(entityId: string) {
    const sensorId = powerSuggestions[entityId];
    if (
      !home ||
      !home.connected ||
      home.revoked ||
      error ||
      !sensorId ||
      home.devices.find((device) => device.entityId === entityId)
        ?.powerSensorId ||
      savingDeviceSettings.current
    )
      return;
    savingDeviceSettings.current = true;
    setConnectingDevice(entityId);
    setPowerSourceError({ homeId: home.id, message: "" });
    try {
      const updated = await api.settings(home.id, {
        allowAll: false,
        allowFutureDevices: home.allowFutureDevices,
        allowedEntityIds: home.devices
          .filter((device) => device.allowed)
          .map((device) => device.entityId),
        powerSource: home.powerSource,
        householdPowerSensorId: home.householdPowerSensorId,
        devicePowerSensors: {
          ...Object.fromEntries(
            home.devices
              .filter((device) => device.powerSensorId)
              .map((device) => [device.entityId, device.powerSensorId!]),
          ),
          [entityId]: sensorId,
        },
      });
      updateHome(updated);
    } catch (e) {
      setPowerSourceError({ homeId: home.id, message: (e as Error).message });
    } finally {
      savingDeviceSettings.current = false;
      setConnectingDevice("");
    }
  }
  async function cycleShutoffLevel(entityId: string) {
    const device = home?.devices.find((d) => d.entityId === entityId);
    if (
      !home ||
      !device ||
      !home.connected ||
      home.revoked ||
      error ||
      savingDeviceSettings.current
    )
      return;
    const current = device.allowed
      ? (device.shutoffLevel ?? "Sometimes")
      : "Never";
    const next: ShutoffLevel =
      current === "Sometimes"
        ? "Never"
        : current === "Never" && !entityId.startsWith("climate.")
          ? "Anytime"
          : "Sometimes";
    savingDeviceSettings.current = true;
    setChangingShutoffDevice(entityId);
    setShutoffLevelError({ homeId: home.id, message: "" });
    try {
      const updated = await api.settings(home.id, {
        allowAll: false,
        allowFutureDevices: home.allowFutureDevices,
        allowedEntityIds: home.devices
          .filter((d) =>
            d.entityId === entityId ? next !== "Never" : d.allowed,
          )
          .map((d) => d.entityId),
        shutoffLevels: { [entityId]: next },
        powerSource: home.powerSource,
        householdPowerSensorId: home.householdPowerSensorId,
        devicePowerSensors: Object.fromEntries(
          home.devices
            .filter((d) => d.powerSensorId)
            .map((d) => [d.entityId, d.powerSensorId!]),
        ),
      });
      updateHome(updated);
    } catch (e) {
      setShutoffLevelError({ homeId: home.id, message: (e as Error).message });
    } finally {
      savingDeviceSettings.current = false;
      setChangingShutoffDevice("");
    }
  }
  const commands = useShutoffApproval(api, home, !!error, refresh);
  const { selectedDevices, setSelected } = commands;
  const high = home && !error && isUsageHigh(home);
  const anomalySubmission =
    recommendationsHome === home?.id && recommendationKind === "anomaly";
  const syncing =
    home &&
    !error &&
    (home.smartPowerOffStatus === "reducing" ||
      (commands.busyHomeId === home.id && !anomalySubmission) ||
      (home.connected &&
        home.commands.some(
          (command) =>
            !command.anomalyId &&
            ["Pending", "AwaitingConfirmation", "Retrying"].includes(
              command.status,
            ),
        )));
  const usageProgress = syncing
    ? commands.busyHomeId === home?.id && commands.syncingHomeId !== home?.id
      ? "Applying device changes…"
      : "Re-syncing devices and checking usage…"
    : undefined;
  if (window.location.pathname === "/settings") {
    return (
      <div className="settings-page">
        <header className="page-heading">
          <h1>Settings</h1>
        </header>
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <UsageAlerts
          homes={homes}
          stale={!!error || loading}
          settingsPage
          preferenceKey={`base-layer-notifications:${userId}`}
          onSelect={(id) => {
            window.location.href = `/?home=${encodeURIComponent(id)}`;
          }}
        />
        <section className="settings-section" aria-label="Smart Shutoff">
          <h2>Smart Shutoff and automatic restore</h2>
          {loading ? (
            <p role="status">Loading your homes…</p>
          ) : homes.length === 0 && !error ? (
            <p className="muted">Connect a home to enable Smart Shutoff.</p>
          ) : (
            homes.map((home) => (
              <SmartShutoffSetting
                key={home.id}
                home={home}
                api={api}
                stale={!!error}
                onSaved={updateHome}
              />
            ))
          )}
        </section>
        <section
          className="settings-section"
          aria-label="Anomaly savings detection"
        >
          <h2>Anomaly savings detection</h2>
          {loading ? (
            <p role="status">Loading your homes…</p>
          ) : homes.length === 0 && !error ? (
            <p className="muted">Connect a home to enable anomaly savings.</p>
          ) : (
            homes.map((home) => (
              <AnomalySavingsSetting
                key={home.id}
                home={home}
                api={api}
                stale={!!error}
                onSaved={updateHome}
              />
            ))
          )}
        </section>
      </div>
    );
  }
  return (
    <>
      <header className="page-heading">
        <div>
          <h1>Home energy</h1>
        </div>
        <Button variant="secondary" onClick={() => setPairing(true)}>
          <Plus size={17} /> Connect a home
        </Button>
      </header>
      {error && (
        <div role="alert" className="error banner">
          {error} <button onClick={() => void refresh()}>Retry</button>
        </div>
      )}
      {!loading && homes.length > 0 && (
        <UsageAlerts
          homes={homes}
          stale={!!error}
          onSelect={setActive}
          preferenceKey={`base-layer-notifications:${userId}`}
        />
      )}
      {loading ? (
        <div className="card empty" role="status">
          <RefreshCw className="spin" /> Loading your homes…
        </div>
      ) : !home ? (
        <section className="card home-welcome">
          <span className="home-welcome-icon" aria-hidden="true">
            <House size={24} />
          </span>
          <h2>Connect your home</h2>
          <p>
            Connect Home Assistant to monitor energy use and control devices.
          </p>
          <Button onClick={() => setPairing(true)}>
            Connect Home Assistant <ArrowUpRight size={18} />
          </Button>
          <small>
            <ShieldCheck size={16} aria-hidden="true" />
            Choose which devices can turn off automatically.
          </small>
        </section>
      ) : (
        <>
          <div className="home-bar">
            <div className="home-picker">
              <House size={19} />
              <Select
                aria-label="Selected home"
                value={home.id}
                onChange={(e) => setActive(e.target.value)}
              >
                {homes.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name}
                    {h.revoked ? " (disconnected)" : ""}
                  </option>
                ))}
              </Select>
            </div>
            <span className="muted">
              {home.lastSeenUtc
                ? `Last seen ${new Date(home.lastSeenUtc).toLocaleTimeString()}`
                : "Waiting for Home Assistant"}
            </span>
          </div>
          <UsageOverview home={home} />
          {home.smartPowerOffEnabled && (
            <p className="muted">
              Smart Shutoff is on · Grid outage risk:{" "}
              {home.gridOutageRisk ?? "low"}.
              {home.alwaysKeepBelowBatteryLimit &&
                " Battery limit applies at every grid risk level."}
              {home.gridOutageRisk === "high"
                ? " Anytime and Sometimes devices can turn off automatically."
                : home.gridOutageRisk === "medium" ||
                    home.alwaysKeepBelowBatteryLimit
                  ? " Anytime devices can reduce charging or turn off automatically."
                  : " Usage above 11 kW is allowed."}
            </p>
          )}
          <CollapsingNotice
            key={`usage-${home.id}`}
            visible={!!(high || syncing)}
          >
            <section
              className="recommendation"
              aria-label="Usage limit notification"
              aria-busy={!!syncing}
            >
              <div className="alert-icon">
                {syncing ? (
                  <RefreshCw size={23} className="spin" aria-hidden="true" />
                ) : (
                  <TriangleAlert size={23} />
                )}
              </div>
              <div>
                <h2>
                  {syncing
                    ? home.smartPowerOffStatus === "reducing"
                      ? "Smart Shutoff is checking usage"
                      : "Updating home usage"
                    : "Reduce usage"}
                </h2>
                {usageProgress ? (
                  <>
                    <p role="status">{usageProgress}</p>
                    <p>
                      Checking updated usage against your{" "}
                      {formatPower(home.limitWatts)} limit.
                    </p>
                  </>
                ) : (
                  <p>
                    {smartPowerOffMessage(home) ??
                      (home.devices.some((d) => d.recommended)
                        ? `Suggested shutoffs could reduce usage to ${formatPower(home.projectedWatts)}.`
                        : "No measured devices available to turn off. Check other appliances.")}
                  </p>
                )}
              </div>
              <Button
                variant="secondary"
                disabled={!!syncing}
                onClick={() => {
                  setRecommendationKind("usage");
                  setSelected(
                    recommendationIds(home.devices).filter(
                      (id) => !id.startsWith("climate."),
                    ),
                  );
                  setRecommendationsHome(home.id);
                }}
              >
                Device Recommendations <ArrowUpRight size={16} />
              </Button>
            </section>
          </CollapsingNotice>
          <AnomalyRecommendation
            key={`anomaly-${home.id}`}
            home={home}
            stale={!!error}
            busy={
              anomalySubmission &&
              (commands.busyHomeId === home.id ||
                commands.syncingHomeId === home.id)
            }
            onReview={() => {
              setRecommendationKind("anomaly");
              setSelected(
                unusualDevices
                  .filter(
                    (d) =>
                      d.allowed &&
                      d.shutoffLevel !== "Never" &&
                      !d.isCircuit &&
                      !d.entityId.startsWith("climate."),
                  )
                  .map((d) => d.entityId),
              );
              setRecommendationsHome(home.id);
            }}
          />
          {!high && !syncing && !home.connected ? (
            <div className="calm-note">
              <ShieldCheck size={19} />
              Controls are unavailable while disconnected.
            </div>
          ) : null}
          <section className="card">
            <div className="section-heading">
              <div>
                <h2>
                  Devices <span className="count">{devices.length}</span>
                </h2>
              </div>
              <Button
                variant="secondary"
                disabled={
                  home.revoked || !!connectingDevice || !!changingShutoffDevice
                }
                onClick={() => setSettings(true)}
              >
                <Settings2 size={16} /> Device settings
              </Button>
            </div>
            <DeviceList
              devices={devices}
              pendingCommands={home.commands.some((c) =>
                ["Pending", "Retrying", "AwaitingConfirmation"].includes(
                  c.status,
                ),
              )}
              onSetEvCurrent={async (id, amps) => {
                await api.evCurrent(home.id, id, amps, crypto.randomUUID());
                await refresh();
              }}
              powerSuggestions={powerSuggestions}
              connectingDevice={connectingDevice}
              changingShutoffDevice={changingShutoffDevice}
              onCycleShutoffLevel={(id) => void cycleShutoffLevel(id)}
              onConnectPowerSource={(id) =>
                void connectSuggestedPowerSource(id)
              }
              selected={selectedDevices.map((d) => d.entityId)}
              disabled={!home.connected || home.revoked || !!error}
              loading={!home.revoked && !home.lastSeenUtc && !error}
              onToggle={(id) =>
                setSelected((v) =>
                  v.includes(id) ? v.filter((x) => x !== id) : [...v, id],
                )
              }
            />
            {shutoffLevelError.homeId === home.id &&
              shutoffLevelError.message && (
                <p className="error device-connection-error" role="alert">
                  {shutoffLevelError.message}
                </p>
              )}
            {powerSourceError.homeId === home.id &&
              powerSourceError.message && (
                <p className="error device-connection-error" role="alert">
                  {powerSourceError.message}
                </p>
              )}
            <ProjectedUsage
              home={home}
              devices={selectedDevices}
              stale={!!error}
            />
            <div className="selection-bar">
              <span>{selectedDevices.length} selected</span>
              <Button
                disabled={!selectedDevices.length || !home.connected || !!error}
                onClick={() => {
                  commands.review();
                }}
              >
                <Power size={17} /> Shutoff
              </Button>
            </div>
          </section>
          <RestoreQueue
            home={home}
            api={api}
            onSaved={updateHome}
            stale={!!error}
          />
          <CommandHistory
            key={home.id}
            commands={home.commands}
            devices={home.devices}
            onCancel={async (commandId) => {
              await api.cancelCommand(home.id, commandId);
              await refresh();
            }}
          />
          <div className="connection-footer">
            <Button variant="ghost" onClick={() => setDeletingHome(home)}>
              <Trash2 size={15} /> Delete home
            </Button>
          </div>
        </>
      )}
      {pairing && <PairHome api={api} onClose={() => setPairing(false)} />}
      {deletingHome && (
        <DeleteHomeDialog
          home={deletingHome}
          onClose={() => setDeletingHome(null)}
          onDelete={async (id) => {
            await api.deleteHome(id);
            removeHome(id);
            setActive("");
            setSettings(false);
            await refresh();
          }}
        />
      )}
      {settings && home && (
        <DeviceSettings
          key={home.id}
          home={home}
          api={api}
          onClose={() => {
            setSettings(false);
            window.history.replaceState({}, "", "/");
          }}
          onSaved={updateHome}
        />
      )}
      {home && recommendationsHome === home.id && (
        <Modal
          title="Device Recommendations"
          className="device-recommendations"
          onClose={() => {
            if (commands.busy) return;
            commands.close();
            setRecommendationsHome(null);
          }}
        >
          {recommendationKind === "usage" && smartPowerOffMessage(home) && (
            <p role="status">{smartPowerOffMessage(home)}</p>
          )}
          <DeviceList
            devices={reviewDevices.map((d) =>
              recommendationKind === "anomaly"
                ? {
                    ...d,
                    recommended: true,
                    allowed: d.allowed && !d.isCircuit,
                  }
                : d,
            )}
            selected={selectedDevices.map((d) => d.entityId)}
            disabled={
              !home.connected ||
              home.revoked ||
              !!error ||
              commands.busy ||
              !!commands.approval ||
              (recommendationKind === "anomaly" &&
                home.commands.some(
                  (c) =>
                    reviewDevices.some((d) => d.entityId === c.entityId) &&
                    ["Pending", "AwaitingConfirmation", "Retrying"].includes(
                      c.status,
                    ),
                ))
            }
            onToggle={(id) =>
              setSelected((ids) =>
                ids.includes(id)
                  ? ids.filter((value) => value !== id)
                  : [...ids, id],
              )
            }
          />
          {recommendationKind === "usage" && (
            <ProjectedUsage
              home={home}
              devices={selectedDevices}
              stale={!!error}
            />
          )}
          {commands.error && (
            <p className="error" role="alert">
              {commands.error}
            </p>
          )}
          {commands.syncingHomeId === home.id && (
            <LoadingStatus>
              Re-syncing devices and checking usage…
            </LoadingStatus>
          )}
          <div className="modal-actions">
            <Button
              disabled={
                !selectedDevices.length ||
                (recommendationKind === "anomaly" &&
                  (selectedDevices.some(
                    (d) =>
                      !reviewDevices.some(
                        (current) => current.entityId === d.entityId,
                      ),
                  ) ||
                    home.commands.some(
                      (c) =>
                        reviewDevices.some((d) => d.entityId === c.entityId) &&
                        [
                          "Pending",
                          "AwaitingConfirmation",
                          "Retrying",
                        ].includes(c.status),
                    ))) ||
                !home.connected ||
                !!error ||
                commands.busy
              }
              onClick={async () => {
                if (
                  await commands.sendSelected(recommendationKind === "anomaly")
                )
                  setRecommendationsHome(null);
              }}
            >
              <Power size={17} />{" "}
              {commands.syncingHomeId === home.id
                ? "Re-syncing…"
                : commands.busy
                  ? "Sending…"
                  : "Turn Off"}
            </Button>
          </div>
        </Modal>
      )}
      {commands.approval && recommendationsHome !== home?.id && (
        <ShutoffConfirmation
          approval={commands.approval}
          busy={commands.busy}
          error={commands.error}
          onClose={commands.close}
          onApprove={() => void commands.send()}
        />
      )}
    </>
  );
}
