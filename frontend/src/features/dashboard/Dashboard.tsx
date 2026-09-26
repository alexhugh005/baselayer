import { useState } from "react";
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
import type { Home } from "../../lib/types";
import { DeleteHomeDialog } from "../connections/DeleteHomeDialog";
import { useHomes } from "./useHomes";
import { recommendationIds, formatPower } from "./power";
import { UsageOverview } from "./UsageOverview";
import { ProjectedUsage } from "./ProjectedUsage";
import { UsageAlerts, isUsageHigh } from "./UsageAlerts";
import { DeviceSettings } from "../devices/DeviceSettings";
import { DeviceList } from "../devices/DeviceList";
import { useShutoffApproval } from "../commands/useShutoffApproval";
import { ShutoffConfirmation } from "../commands/ShutoffConfirmation";
import { CommandHistory } from "../commands/CommandHistory";
import { PairHome } from "../connections/PairHome";
import { Button } from "../../components/ui/Button";
import { Modal } from "../../components/ui/Modal";
import { LoadingStatus } from "../../components/ui/LoadingStatus";
export function Dashboard({
  api,
  userId = "local",
}: {
  api: Api;
  userId?: string;
}) {
  const [deletingHome, setDeletingHome] = useState<Home | null>(null);
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
  const commands = useShutoffApproval(api, home, !!error, refresh);
  const { selectedDevices, setSelected } = commands;
  const high = home && !error && isUsageHigh(home);
  const syncing =
    home &&
    !error &&
    (commands.busyHomeId === home.id ||
      (home.connected &&
        home.commands.some((command) =>
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
        <UsageAlerts
          homes={homes}
          stale={!!error || loading}
          settingsPage
          preferenceKey={`base-layer-notifications:${userId}`}
          onSelect={(id) => {
            window.location.href = `/?home=${encodeURIComponent(id)}`;
          }}
        />
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
            Every shutoff needs your approval.
          </small>
        </section>
      ) : (
        <>
          <div className="home-bar">
            <div className="home-picker">
              <House size={19} />
              <select
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
              </select>
            </div>
            <span className="muted">
              {home.lastSeenUtc
                ? `Last seen ${new Date(home.lastSeenUtc).toLocaleTimeString()}`
                : "Waiting for Home Assistant"}
            </span>
          </div>
          <UsageOverview home={home} progress={usageProgress} />
          {high || syncing ? (
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
                <h2>{syncing ? "Updating home usage" : "Reduce usage"}</h2>
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
                    {home.devices.some((d) => d.recommended)
                      ? `Suggested shutoffs could reduce usage to ${formatPower(home.projectedWatts)}.`
                      : "No measured devices available to turn off. Check other appliances."}
                  </p>
                )}
              </div>
              <Button
                variant="secondary"
                disabled={!!syncing}
                onClick={() => {
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
          ) : !home.connected ? (
            <div className="calm-note">
              <ShieldCheck size={19} />
              Controls are unavailable while disconnected.
            </div>
          ) : null}
          <section className="card">
            <div className="section-heading">
              <div>
                <h2>
                  Devices <span className="count">{home.devices.length}</span>
                </h2>
              </div>
              <Button
                variant="secondary"
                disabled={home.revoked}
                onClick={() => setSettings(true)}
              >
                <Settings2 size={16} /> Device settings
              </Button>
            </div>
            <DeviceList
              devices={home.devices}
              selected={selectedDevices.map((d) => d.entityId)}
              disabled={!home.connected || !!error}
              loading={!home.revoked && !home.lastSeenUtc && !error}
              onToggle={(id) =>
                setSelected((v) =>
                  v.includes(id) ? v.filter((x) => x !== id) : [...v, id],
                )
              }
            />
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
          <DeviceList
            devices={home.devices.filter((d) => d.recommended)}
            selected={selectedDevices.map((d) => d.entityId)}
            disabled={
              !home.connected || !!error || commands.busy || !!commands.approval
            }
            onToggle={(id) =>
              setSelected((ids) =>
                ids.includes(id)
                  ? ids.filter((value) => value !== id)
                  : [...ids, id],
              )
            }
          />
          <ProjectedUsage
            home={home}
            devices={selectedDevices}
            stale={!!error}
          />
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
                !home.connected ||
                !!error ||
                commands.busy
              }
              onClick={async () => {
                if (await commands.sendSelected()) setRecommendationsHome(null);
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
