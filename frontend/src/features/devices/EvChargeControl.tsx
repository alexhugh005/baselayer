import { useEffect, useRef, useState } from "react";
import { Pencil } from "lucide-react";
import type { Device } from "../../lib/types";
import { Button } from "../../components/ui/Button";

export function EvChargeControl({
  device,
  disabled,
  onSetCurrent,
}: {
  device: Device;
  disabled: boolean;
  onSetCurrent: (amps: number) => Promise<void>;
}) {
  const control = device.evCurrent;
  const [amps, setAmps] = useState(String(control?.amps ?? ""));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [editing, setEditing] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const editRef = useRef<HTMLButtonElement>(null);
  const wasEditing = useRef(false);
  const inFlight = useRef(false);
  useEffect(() => {
    if (!editing) setAmps(String(control?.amps ?? ""));
  }, [control?.amps, control?.entityId, editing]);
  useEffect(() => {
    if (editing) {
      inputRef.current?.focus();
      inputRef.current?.select();
    } else if (wasEditing.current) {
      editRef.current?.focus();
    }
    wasEditing.current = editing;
  }, [editing]);
  const value = Number(amps);
  const valid =
    !!control &&
    amps.trim() !== "" &&
    Number.isFinite(value) &&
    value >= control.min &&
    value <= control.max &&
    Math.abs(
      (value - control.min) / control.step -
        Math.round((value - control.min) / control.step),
    ) < 0.000001;
  const unavailable =
    !control || control.amps === null || !["on", "off"].includes(device.state);
  async function apply() {
    if (
      inFlight.current ||
      !valid ||
      unavailable ||
      disabled ||
      value === control?.amps
    )
      return;
    inFlight.current = true;
    setBusy(true);
    setError("");
    try {
      await onSetCurrent(value);
      setEditing(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }
  function cancel() {
    if (inFlight.current) return;
    setError("");
    setEditing(false);
  }
  return (
    <div className="ev-charge-control">
      <span className="ev-charge-label">Charging current</span>
      {!editing ? (
        <button
          ref={editRef}
          type="button"
          className="button ghost ev-charge-edit"
          aria-label={`Edit ${device.name} charging current`}
          aria-expanded={false}
          disabled={disabled || unavailable}
          onClick={() => {
            setError("");
            setEditing(true);
          }}
        >
          {unavailable ? "Unavailable" : `${control.amps} A`}
          <Pencil size={13} aria-hidden="true" />
        </button>
      ) : (
        <div className="ev-charge-editor">
          <label className="ev-charge-input">
            <input
              ref={inputRef}
              type="number"
              aria-label={`${device.name} charging current (A)`}
              aria-invalid={!valid}
              min={control?.min}
              max={control?.max}
              step={control?.step}
              value={amps}
              disabled={disabled || busy || unavailable}
              onChange={(e) => setAmps(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  void apply();
                } else if (e.key === "Escape") {
                  e.preventDefault();
                  cancel();
                }
              }}
            />
            <span aria-hidden="true">A</span>
          </label>
          <Button
            type="button"
            variant="secondary"
            disabled={
              disabled ||
              busy ||
              unavailable ||
              !valid ||
              value === control?.amps
            }
            onClick={() => void apply()}
          >
            {busy ? "Saving…" : "Save"}
          </Button>
          <Button
            type="button"
            variant="ghost"
            disabled={busy}
            onClick={cancel}
          >
            Cancel
          </Button>
        </div>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
