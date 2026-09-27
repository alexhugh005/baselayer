# Grid outage detection

This document describes predicted risk. Confirmed SPAN battery backup is detected
through the [power-supply API](power-supply-api.md) and triggers the separate
[circuit recovery rollout](outage-recovery.md), regardless of this risk stub.

`IGridOutageDetectionService.GetRiskAsync(homeId)` returns `low`, `medium`, or
`high`. The registered `GridOutageDetectionService` is a stub that always returns
`low`; no external grid feed is called. Replace its dependency-injection
registration to supply a real detector. Each successful household poll reads the
risk before planning or dispatching device actions. The latest risk is persisted
on the home and exposed as `gridOutageRisk` in the home API response. Existing
SQLite databases gain this field with a `low` default.

| Risk | Behavior with Smart Shutoff enabled and usage at or above 11 kW |
| --- | --- |
| low | Allow usage above 11 kW. No automatic reductions or usage alerts. |
| medium | Try Anytime EV current reduction first, then shut off the necessary Anytime loads. After confirmation, recommend all Sometimes loads needed to get below 11 kW and notify the user to approve them. |
| high | Apply the same EV/Anytime controls, then automatically shut off the necessary Sometimes loads. |

Loads are selected largest first within each category, stopping strictly below
11 kW. Never devices, panel circuits, thermostats, and unavailable/unmetered loads
remain excluded from automatic selection. If permitted loads cannot cover the
shortfall, the UI asks the user for additional manual reductions. Smart Shutoff
remains opt-in; disabling it prevents automatic reductions and pauses restoration.
The separate, user-approved Smart Usage battery-runtime planner retains its
physical per-battery power budget.

All dispatched appliance shutoffs save a restoration entry before sending,
including Sometimes devices and commands with an ambiguous network response.
EV reductions preserve the original current target before dispatch. Entries
persist across restarts. Observed device state confirms completion. Already-off
devices do not acquire a restoration entry merely from an off request.

At medium/high risk the queue restores only when fresh capacity, including the
existing buffer, is available. At low risk it can restart devices or restore
original EV current above 11 kW. Existing demo delays, serialized restoration,
manual override handling, unknown-reading checks, and Keep off remain in effect.
High risk is a reason to reduce usage; it does not bypass the capacity check for
restoration. A higher allowed capacity would permit more queued devices to fit.
Risk downgrades cancel future ineligible shutoff/current-reduction attempts;
already-applied commands are still confirmed and kept in the restore queue.

Browser notifications use the existing opt-in while the app is open. Action
notifications wait for automatic attempts to finish and name every recommended
device. Changes to the risk, status, or recommended list can notify again; repeated
polls of the same state do not. In-app guidance works without browser permission.
