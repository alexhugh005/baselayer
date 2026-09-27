# Smart Usage

Open **Smart Usage** using the lightbulb in the sidebar. Choose a home, move the
runtime slider, review the proposed switches and estimated local end time, then
select **Confirm plan**. Moving the slider only previews changes. Progress reports
queued, confirmed, failed, and cancelled commands separately.

The estimates use remaining battery energy divided by household power:
`hours = storedEnergyKwh / (watts / 1000)`. Current runtime uses measured household
usage. The shortest endpoint adds the saved running load of every eligible off
device; the longest removes every eligible active load. Other household usage
stays in the baseline. When the all-on endpoint exceeds the battery power limit,
it can be previewed but cannot be confirmed. Zero measured load has no finite
estimated end; an empty battery has zero runtime and cannot confirm a plan.

Only present, permitted Anytime/Sometimes switches, lights, and fans with usable
power readings are adjustable. Never devices and thermostats retain their states.
Devices with unknown readings or missing running estimates are listed separately;
the full all-on/all-off range cannot be calculated until those measurements exist.
For off devices, the API saves their most recent positive on-state power reading
across polls and restarts. Changing a sensor mapping clears the saved estimate.
Standby readings are included when estimating the extra power from turning on.

To extend runtime, largest active loads are selected first. This minimizes the
number of shutoffs needed to meet the requested load reduction; ties use the
entity ID for consistent results. For shorter runtimes, off devices are considered
largest first and added when they fit the requested budget. Device sizes are
discrete, so the page shows both requested and achievable runtime. Turn-on selection
is a greedy policy, not a globally optimal packing solver.

## API and responsibilities

- `GET /api/homes/{homeId}/smart-usage?targetWatts=5000` previews the selected
  power budget. Omit `targetWatts` to start at the current usage. The response
  includes battery telemetry, runtime bounds, projected usage, changes, exclusions,
  whether confirmation is allowed, and a revision.
- `POST /api/homes/{homeId}/smart-usage` accepts
  `{ "targetWatts": 5000, "revision": "<preview revision>", "idempotencyKey": "<unique key>" }`
  and returns queued commands. Both routes require authentication and home ownership.

`ISmartPowerUsageService` separates the API from orchestration. `SmartUsagePolicy`
handles calculations and selection. `IBatteryService` supplies telemetry through
the existing replaceable battery provider. `SmartPowerUsageService` revalidates the
preview and persists approved commands through the repository. Home Assistant
communication, retries, and observed confirmation reuse the existing polling flow.

Confirmations require home telemetry no older than 20 seconds and battery
telemetry no older than 30 seconds. Changed power readings, device states, or
permissions invalidate the reviewed revision. Pending commands block new plans.
Retries of the same confirmed request reuse the same commands; reusing a key for
a different plan is rejected.

The current provider models **one battery per home**, with an **11,000 W** maximum.
Every Smart Usage turn-on is checked again against fresh household power, its
running estimate, the confirmed plan budget, and the battery limit. Turn-ons are
sent one at a time and subsequent switches wait for observed confirmation and a
fresh household reading. Lost headroom or removed permissions cancels an unsafe
switch. Failed switches stay visible instead of being reported as success.

Devices left off by a confirmed plan are held out of automatic restore. A shorter
Smart Usage plan can bring them back. This hold persists across API restarts and
is cleared if an off device is observed turning on externally. Smart Shutoff can
still shed Anytime loads at medium/high grid risk, and Sometimes loads at high risk,
if actual household power reaches its existing threshold.
While Smart Shutoff is enabled and grid risk is medium/high, a plan landing exactly at 11 kW is also blocked
so that automation will not immediately undo an approved turn-on.

## Estimate limits

Runtime assumes constant load, no charging, and all reported stored energy usable.
Saved running readings are estimates, not appliance ratings or startup-surge
guarantees. Battery losses, reserve margins, solar, and changing appliance load are
not modeled. The battery provider currently simulates stored energy at a configured
constant rate independently of device switches; the page labels this clearly.
End times are formatted in the browser's local time zone, including date and zone.

## Verification

Backend tests cover runtime bounds, minimum shutoff counts against every subset of
a sample device set, unavailable telemetry, standby accounting, ownership,
revision/idempotency checks, persistence, schema upgrade, restore holds, and
serialized turn-ons with fresh power/permission checks. Frontend tests cover the
sidebar, slider preview/confirmation, stale request ordering, over-limit and failed
refresh states, pending command feedback, and local end-time formatting.
