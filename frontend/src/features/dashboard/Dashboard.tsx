import { useState } from "react";
import {
  ArrowUpRight,
  Plus,
  ShieldCheck,
  TriangleAlert,
  Power,
  Unplug,
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
import { DeviceSettings } from "../devices/DeviceSettings";
import { DeviceList } from "../devices/DeviceList";
import { useShutoffApproval } from "../commands/useShutoffApproval";
import { ShutoffConfirmation } from "../commands/ShutoffConfirmation";
import { CommandHistory } from "../commands/CommandHistory";
import { PairHome } from "../connections/PairHome";
import { Button } from "../../components/ui/Button";
import { Badge } from "../../components/ui/Badge";
import { Modal } from "../../components/ui/Modal";
export function Dashboard({ api }: { api: Api }) {
  const [deletingHome, setDeletingHome] = useState<Home | null>(null);
  const { homes, loading, error, refresh, removeHome } = useHomes(api);
  const [active, setActive] = useState(
      new URLSearchParams(window.location.search).get("connected") ?? "",
    ),
    [settings, setSettings] = useState(
      !!new URLSearchParams(window.location.search).get("connected"),
    ),
    [pairing, setPairing] = useState(false),
    [revoke, setRevoke] = useState(false),
    [busy, setBusy] = useState(false),
    [actionError, setActionError] = useState("");
  const home =
    homes.find((h) => h.id === active) ??
    homes.find((h) => !h.revoked) ??
    homes[0];
  const commands = useShutoffApproval(api, home, !!error, refresh);
  const { selectedDevices, setSelected } = commands;
  const high =
    home?.connected &&
    home.householdWatts !== null &&
    home.householdWatts >= home.limitWatts;
  async function disconnect() {
    if (!home) return;
    setBusy(true);
    try {
      await api.revoke(home.id);
      setRevoke(false);
      await refresh();
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <header className="page-heading">
        <div>
          <div className="breadcrumb">
            WORKSPACE <span>/</span> HOME ENERGY
          </div>
          <h1>Your home. In balance.</h1>
          <p>A little visibility. Better decisions. You’re in control.</p>
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
      {loading ? (
        <div className="card empty">
          <RefreshCw className="spin" /> Loading your homes…
        </div>
      ) : !home ? (
        <section className="card onboarding">
          <House size={42} />
          <h2>Give your home a Base Layer.</h2>
          <p>
            Connect Home Assistant to bring device usage, clear recommendations,
            and approved controls into one dashboard.
          </p>
          <Button onClick={() => setPairing(true)}>
            Connect your first home <ArrowUpRight size={18} />
          </Button>
          <small>No device switches off without your approval.</small>
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
                    {h.revoked ? " (revoked)" : ""}
                  </option>
                ))}
              </select>
              <Badge tone={home.connected ? "green" : "neutral"}>
                {home.connected
                  ? "Connected"
                  : home.revoked
                    ? "Revoked"
                    : "Offline"}
              </Badge>
            </div>
            <span className="muted">
              {home.lastSeenUtc
                ? `Last seen ${new Date(home.lastSeenUtc).toLocaleTimeString()}`
                : "Waiting for Home Assistant"}
            </span>
          </div>
          <UsageOverview home={home} />
          {high ? (
            <section className="recommendation" role="alert">
              <div className="alert-icon">
                <TriangleAlert size={23} />
              </div>
              <div>
                <span className="eyebrow">LET’S BRING THAT DOWN</span>
                <h2>Your home is above its comfort limit.</h2>
                <p>
                  Currently {formatPower(home.householdWatts)}.{" "}
                  {home.devices.some((d) => d.recommended)
                    ? `Review the suggested devices to bring usage toward ${formatPower(home.projectedWatts)}.`
                    : "No controllable device has a measured load. Check your other appliances."}
                </p>
              </div>
              <Button
                variant="secondary"
                onClick={() => setSelected(recommendationIds(home.devices))}
              >
                Select suggestions <ArrowUpRight size={16} />
              </Button>
            </section>
          ) : (
            <div className="calm-note">
              <ShieldCheck size={19} />
              {home.connected
                ? "You decide what turns off. Base Layer only sends commands you approve."
                : "Controls are paused until a fresh connection is established."}
            </div>
          )}
          <section className="card">
            <div className="section-heading">
              <div>
                <span className="eyebrow">KNOW WHAT’S RUNNING</span>
                <h2>
                  Your devices{" "}
                  <span className="count">{home.devices.length}</span>
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
              onToggle={(id) =>
                setSelected((v) =>
                  v.includes(id) ? v.filter((x) => x !== id) : [...v, id],
                )
              }
            />
            <div className="selection-bar">
              <span>
                {selectedDevices.length} selected{" "}
                <small>· Missing power readings are shown as unknown</small>
              </span>
              <Button
                disabled={!selectedDevices.length || !home.connected || !!error}
                onClick={() => {
                  setActionError("");
                  commands.review();
                }}
              >
                <Power size={17} /> Review shutoff
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
            <span>
              <ShieldCheck size={16} /> Your Home Assistant tokens are encrypted
              on the server.
            </span>
            <Button
              variant="ghost"
              disabled={home.revoked}
              onClick={() => {
                setActionError("");
                setRevoke(true);
              }}
            >
              <Unplug size={15} /> Revoke connection
            </Button>
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
          onSaved={() => void refresh()}
        />
      )}
      {commands.approval && (
        <ShutoffConfirmation
          approval={commands.approval}
          busy={commands.busy}
          error={commands.error}
          onClose={commands.close}
          onApprove={() => void commands.send()}
        />
      )}
      {revoke && (
        <Modal title="Disconnect this home?" onClose={() => setRevoke(false)}>
          <p>
            This revokes the Home Assistant authorization and cancels pending
            commands. Already executed actions cannot be undone. A new Home
            Assistant authorization will be required to reconnect.
          </p>
          {actionError && <p role="alert">{actionError}</p>}
          <Button
            variant="danger"
            disabled={busy}
            onClick={() => void disconnect()}
          >
            Revoke access
          </Button>
        </Modal>
      )}
    </>
  );
}
