import { isDeviceRunning } from "../../lib/deviceState";
import { useEffect, useRef, useState } from "react";
import type { Api } from "../../lib/api";
import type { Device, Home } from "../../lib/types";

export interface Approval {
  homeId: string;
  homeName: string;
  devices: Device[];
  idempotencyKey: string;
}
export function useShutoffApproval(
  api: Api,
  home: Home | undefined,
  stale: boolean,
  refresh: () => Promise<void>,
) {
  const [selected, setSelected] = useState<string[]>([]);
  const [approval, setApproval] = useState<Approval | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  const eligible =
    home?.connected && !stale
      ? home.devices
          .filter((d) => d.allowed && isDeviceRunning(d.state))
          .map((d) => d.entityId)
      : [];
  const eligibility = eligible.join("|");
  useEffect(() => {
    setSelected([]);
    setApproval(null);
    setError("");
  }, [home?.id]);
  useEffect(() => {
    const current = new Set(eligibility.split("|"));
    setSelected((ids) => {
      const next = ids.filter((id) => current.has(id));
      return next.length === ids.length ? ids : next;
    });
    setApproval((prior) => {
      if (prior && prior.devices.some((d) => !current.has(d.entityId)))
        return null;
      return prior;
    });
  }, [eligibility]);
  const selectedDevices =
    home?.devices.filter(
      (d) => selected.includes(d.entityId) && eligible.includes(d.entityId),
    ) ?? [];
  function review() {
    if (!home || !selectedDevices.length) return;
    setError("");
    setApproval({
      homeId: home.id,
      homeName: home.name,
      devices: selectedDevices.map((d) => ({ ...d })),
      idempotencyKey: crypto.randomUUID(),
    });
  }
  async function send() {
    if (
      !approval ||
      submitting.current ||
      approval.devices.some((d) => !eligible.includes(d.entityId))
    )
      return;
    submitting.current = true;
    setBusy(true);
    setError("");
    try {
      // Keep this exact key and payload on ambiguous network failures.
      await api.turnOff(
        approval.homeId,
        approval.devices.map((d) => d.entityId),
        approval.idempotencyKey,
      );
      setApproval(null);
      setSelected([]);
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }
  return {
    selected,
    setSelected,
    selectedDevices,
    approval,
    busy,
    error,
    review,
    send,
    close: () => {
      if (!submitting.current) setApproval(null);
    },
  };
}
