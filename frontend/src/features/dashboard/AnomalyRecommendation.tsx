import { ArrowUpRight, RefreshCw, TriangleAlert } from "lucide-react";
import type { Home } from "../../lib/types";
import { isDeviceRunning } from "../../lib/deviceState";
import { Button } from "../../components/ui/Button";
import { CollapsingNotice } from "../../components/ui/CollapsingNotice";
import { formatPower } from "./power";

// Live readings keep this an actionable warning, never an anomaly-history list.
export function anomalyDevices(home: Home) {
  if (!home.connected || home.revoked) return [];
  return home.devices.filter((device) => {
    const threshold =
      device.anomalyThresholdWatts ??
      (device.standardWatts == null ? null : device.standardWatts * 1.5);
    return (
      isDeviceRunning(device.state) &&
      threshold != null &&
      Number.isFinite(threshold) &&
      threshold > 0 &&
      device.powerWatts != null &&
      Number.isFinite(device.powerWatts) &&
      device.powerWatts >= threshold
    );
  });
}

export function AnomalyRecommendation({
  home,
  stale,
  busy,
  onReview,
}: {
  home: Home;
  stale: boolean;
  busy: boolean;
  onReview: () => void;
}) {
  const devices = stale ? [] : anomalyDevices(home);
  const actions =
    !stale && home.connected && !home.revoked
      ? home.commands.filter(
          (command) =>
            (command.action ?? "Off") === "Off" &&
            (command.anomalyId ||
              devices.some((device) => device.entityId === command.entityId)) &&
            ["Pending", "AwaitingConfirmation", "Retrying"].includes(
              command.status,
            ),
        )
      : [];
  const pending = busy || actions.length > 0;
  const confirming =
    actions.length > 0 &&
    actions.every((action) => action.status === "AwaitingConfirmation");
  const retrying = actions.some((action) => action.status === "Retrying");
  const title = confirming
    ? "Anomaly detection is confirming shutoff"
    : retrying
      ? "Anomaly detection is retrying shutoff"
      : "Anomaly detection is turning off devices";
  const deviceName = (entityId: string) =>
    home.devices.find((device) => device.entityId === entityId)?.name ??
    entityId;
  return (
    <CollapsingNotice visible={devices.length > 0 || pending}>
      <section
        className="recommendation"
        role="alert"
        aria-label="Unusual device usage"
        aria-busy={pending}
      >
        <div className="alert-icon">
          {pending ? (
            <RefreshCw size={23} className="spin" aria-hidden="true" />
          ) : (
            <TriangleAlert size={23} aria-hidden="true" />
          )}
        </div>
        <div>
          <h2>{pending ? title : "Unusual device usage"}</h2>
          {pending ? (
            <div role="status" aria-live="polite" aria-atomic="true">
              {actions.length ? (
                actions.map((action) => (
                  <p key={action.id}>
                    {deviceName(action.entityId)}:{" "}
                    {action.status === "AwaitingConfirmation"
                      ? "shutoff sent; waiting for the device to confirm it is off."
                      : action.status === "Retrying"
                        ? "shutoff has not been confirmed; retrying."
                        : "sending shutoff command…"}
                  </p>
                ))
              ) : (
                <p>
                  Sending shutoff for{" "}
                  {devices.map((device) => device.name).join(", ") ||
                    "the selected devices"}
                  …
                </p>
              )}
            </div>
          ) : (
            <p>
              {`${devices.map((device) => `${device.name} (${formatPower(device.powerWatts)})`).join(", ")} ${devices.length === 1 ? "is" : "are"} drawing at least 50% above standard. Review the recommended devices to turn off.`}
            </p>
          )}
        </div>
        {!pending && (
          <Button variant="secondary" onClick={onReview}>
            Device Recommendations <ArrowUpRight size={16} />
          </Button>
        )}
      </section>
    </CollapsingNotice>
  );
}
