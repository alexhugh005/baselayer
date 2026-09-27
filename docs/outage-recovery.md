# Circuit rollout after an outage

The API detects a transition from SPAN's reported grid supply to battery backup
and automatically restores circuits that were on in the last grid-connected
snapshot. It does not require the browser to be open or Smart Shutoff to be enabled.
This circuit recovery policy is separate from predicted grid-outage risk and the
appliance restoration queue.

While grid-connected, each successful poll remembers circuit positions and the
highest positive power observed for each circuit. Circuit meters use the installed
SPAN naming convention (`switch.span_panel_*_breaker` and its matching
`sensor.span_panel_*_power`). A numeric `restoration_estimate_watts` attribute on
that power sensor can supply a configured load estimate. The policy uses the
greater of that value and its observed peak. The virtual lab publishes known model
peaks through these attributes, so idle appliance readings do not understate demand.

On battery:

1. Freeze the set of circuits that were on before the transition. Previously-off
   circuits remain off. A fresh grid baseline (within 20 seconds) is required; an
   API that first connects during an outage cannot invent earlier circuit positions.
2. Wait at least five seconds and, in the virtual lab, wait for `Backup ready`.
3. Select the off circuit with the lowest usable estimated watts. Unknown estimates
   wait rather than counting as zero. Equal estimates are ordered by entity ID.
4. Add its estimate to the latest measured household/circuit load. Admit the
   restore only at or below **11,000 W**.
5. Send one `switch.turn_on` through Home Assistant. Wait for an observed `on`
   state, then wait at least five more seconds before the next circuit.
6. Continue until every eligible circuit is on or no remaining estimate fits.
   If capacity is freed later, the same outage event can continue.

The five-second delay is deliberately short for the demo. The code comments call
out that real installations need longer startup and measurement-settling delays
appropriate to their loads. With the current three-second polling cycle, actual
gaps can be longer than five seconds. There is no blocking five-second sleep in a
request or worker thread.

Budget accounting uses all fresh native circuit measurements, including circuits
switched on manually. If the configured household measurement is higher, that
larger value is used too. After confirmation and the five-second cooldown, powered
circuits contribute only their actual usage. A circuit estimated at 5 kW but drawing
10 W therefore leaves the rest of that capacity available for the next start. Missing circuit measurements or
unknown grid status pause admission. Estimates and polling cannot guarantee against
instantaneous inrush or future demand beyond the estimate; inverter/panel protection
continues to enforce physical limits.

The pre-outage baseline, frozen queue, estimates, event ID, timing, and command intent
are persisted. A lost HTTP response does not trigger a second circuit. An unconfirmed
restore blocks the rollout for review after 15 seconds. Manual circuit commands and
command cancellation override the affected queued restore. Cancelled requests already
sent retain a capacity reservation for the event because cancellation cannot undo a
late write. A successfully restored circuit that is later switched off stays off.
Grid return ends the rollout and preserves current breaker positions. It does not
switch all breakers off or turn previously-off breakers on.

The API and Home Assistant outage scripts preserve **When grid goes down**
priorities throughout the event. They do not set **Stay on**, force **Turn off**,
or restore saved priority values on grid return. If the panel's configured behavior
prevents a requested circuit from remaining on, normal confirmation/held-state
handling applies instead of overriding that preference.

Each circuit also stores the operating states of its explicitly mapped devices at
that last grid-connected snapshot. After its circuit is confirmed on, recovery waits
for the optional `circuit_supply_entity` to report power, then restarts only those
devices that were previously running. Switches, lights, and fans use their domain's
`turn_on`; thermostats use `climate.set_hvac_mode` with the saved heat/cool/etc. mode.
Previously-off devices stay off. Appliance settings already retained by HA are not
replaced, and appliance cycles restart rather than resume at an exact elapsed time.

Device starts are serialized, observed, and separated by the same five-second
minimum. Before restarting an appliance, admission checks measured household load
plus the positive difference between its parent circuit's estimate and that circuit's
current draw. This avoids double counting current draw while allowing room for the
start. Other settled circuits are counted at actual usage, not their peak estimates.
A changed/ambiguous device mapping, unavailable supply or device,
over-budget load, or unconfirmed start pauses the rollout. Successful device starts,
manual overrides, and target states survive API restarts in the outage event.

Mappings are explicit: add a `restoration_devices` list of HA entity IDs to each
native circuit power sensor's customization. Optionally add `circuit_supply_entity`
to require a separate binary power-supply confirmation. The virtual lab publishes
both. No mapping is inferred from room names. Recovery of these configured members
is part of circuit restoration; appliance Smart Shutoff classifications determine
which loads may be shed, not which previously-running mapped devices are recovered.
New devices or mappings added during an outage are not adopted into its frozen list.

## API visibility and backend integration

`GET /api/homes` includes `powerSupply` and `outageRecovery`. Recovery fields include:

- `status`: `monitoring`, `restoring`, `confirming`, `complete`, `capacityLimited`,
  `waitingForBaseline`, `waitingForBackup`, `waitingForTelemetry`, `waitingForEstimate`,
  `paused`, `blocked`, `restoringDevices`, `waitingForDeviceMapping`,
  `waitingForCircuitPower`, or `waitingForDevices`.
- `eventId`, `startedUtc`, and `nextRestoreAtUtc` (earliest admission time, not a promise).
- `budgetWatts`, `measuredWatts`, and `reservedWatts`. The last field is retained for
  compatibility: during admission it is measured demand plus any uncertain cancelled
  circuit requests, not a permanent peak reservation for powered circuits.
- `circuits`: the frozen pre-outage-on set, with names, estimates, rollout status, and
  `devices` containing each saved target state and its restoration status.

Commands carry `outageEventId`, `automatic: true`, and `isRestoration: true`.
`PlatformService.ProcessOutageRecovery` runs after each fresh provider snapshot;
`OutageRecoveryPolicy.Update` owns event detection, ordering, and admission.
Existing authorization and per-home operation locking apply to circuit overrides.
No predicted-risk value is required to trigger this recovery sequence.

## Local demo

1. Run the API and the existing Home Assistant / PanelBench lab.
2. On grid power, restore circuit supplies, set the desired starting positions,
   and allow a few seconds for the API to capture them.
3. Enable **Grid outage · battery backup** on the Home Assistant Smart Panel tab.
4. Watch circuit states or `outageRecovery` in the API. Circuits are tried in ascending estimate
   order. With the default quiet household, all eight can return because measured
   usage stays low; idle circuits do not permanently consume their peak estimate.
5. To reset, turn grid outage off, wait for grid recovery, run **Restore all circuit
   supplies**, then **Reset realistic household**.

The example assumes all eight circuits were on beforehand, existing panel settings
permit the restores, and no additional loads or overrides change the budget. A
different starting set or panel setting can produce a different outcome.

`docs/home-assistant/span-panel/verify_api_outage.py` runs this complete localhost-only
scenario and resets the lab to grid power afterward. It verifies confirmed command
order, gaps of at least five seconds, native circuit supply, and the final budget;
evidence is written to `artifacts/outage-recovery/live-verification.json`.
