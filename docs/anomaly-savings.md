# Anomaly savings detection

Each home has an independent, default-off **Anomaly savings detection** switch in Settings. Live excess readings show the same inline warning and **Device Recommendations** flow as the household usage alert. The dialog lists affected devices and preselects eligible shutoffs; the Turn Off action submits an explicit anomaly approval (`POST /commands` with `forAnomaly: true`). These commands link to an anomaly, run through its worker, and stay out of automatic restoration. Explicit approval works independently of the automatic savings toggle and its cooldown; fresh power and device permissions are still rechecked. The warning stays while readings are at least 50% above standard and clears when they normalize or become unavailable. Never devices, thermostats, and panel circuits cannot be selected for this action. Historical anomalies do not keep a warning visible, and shutoff actions remain in Recent activity. There is no separate anomaly-history card or floating popup. Browser notifications use the existing opt-in preference and work while Base Layer is open.

When enabled, an `Off` recommendation for usage at least 50% above standard can shut off a permitted Sometimes or Anytime device, regardless of grid risk. Never devices, thermostats, panel circuits, lower usage, and `Inspect` recommendations remain notification-only. Existing thermostat and panel protections are preserved. Fresh readings must still be at least 150% of standard consumption; the device must be on and its power mapping unchanged. Anomaly shutoffs remain off for user review rather than entering automatic restoration.

## Device categories and automatic detection

Device settings lets the owner assign a category and optionally override its standard operating watts. Existing and newly discovered devices start unassigned; choose a category to enable this detector. The backend supplies the category catalog in `home.deviceCategories`, so the UI and detector share one set of estimates.

| Category | Standard W | Trigger W (150%) |
| --- | ---: | ---: |
| Toaster | 1,200 | 1,800 |
| Microwave | 1,100 | 1,650 |
| Coffee maker | 1,200 | 1,800 |
| Refrigerator / freezer | 725 | 1,087.5 |
| Dishwasher | 2,400 | 3,600 |
| Clothes washer | 500 | 750 |
| Electric clothes dryer | 5,000 | 7,500 |
| Space heater | 1,500 | 2,250 |
| Electric water heater | 5,500 | 8,250 |
| Television | 120 | 180 |
| Desktop computer and monitor | 270 | 405 |
| Ceiling fan | 175 | 262.5 |
| Other / custom | Required override | Override × 1.5 |

These are editable starting estimates selected from the typical nameplate values/ranges in [Virginia Cooperative Extension’s appliance energy guide](https://www.pubs.ext.vt.edu/2901/2901-9014/2901-9014.html), not universal limits. Use the particular appliance’s nameplate or normal operating measurement when available. Cycling and startup peaks vary by model; the requested rule reacts to the first qualifying poll without a sustained-duration filter.

Power is measured in **W**, not watts per hour. A toaster drawing 1,200 W continuously for an hour consumes 1,200 Wh (1.2 kWh); this detector compares live power, not rolling hourly energy.

Each successful provider poll checks finite readings from present devices against `observedWatts >= standardWatts × 1.5`. Missing/unavailable readings and unassigned devices are skipped. Events feed the existing notification/shutoff flow, even at low grid risk. With automatic savings disabled, events still notify. A fresh rise above the threshold creates a new alert even if the device was shut off within the last hour. Sustained excess is deduplicated with at most one reminder per hour. A valid reading below the threshold rearms detection; missing telemetry does not. Episode state survives restart. The separate one-hour automatic-action cooldown still prevents repeated shutoff attempts, with an explanatory notification. Retries stop if power drops below the 150% threshold. Changing the category or override invalidates pending observations through the same revision check as a power-sensor remap.

Add this optional field to the existing home settings PUT payload:

```json
{
  "devicePowerStandards": {
    "switch.toaster": { "category": "toaster", "standardWatts": null },
    "switch.other": { "category": "custom", "standardWatts": 850 }
  }
}
```

`null` watts uses the category estimate; a `null` device entry clears its category. Omitted entries preserve saved settings. Overrides must be finite and between 1 and 1,000,000 W; custom requires an override. Device responses expose `category`, effective `standardWatts`, `standardWattsOverride`, and `anomalyThresholdWatts`. Schema initialization upgrades existing databases without assigning categories or enabling automatic shutoff.

## Durable queue contract

The existing home polling worker consumes a SQLite inbox after each successful Home Assistant poll (normally every three seconds). Queue entries, settings, command attempts, and savings survive process restarts. No broker is required. An external detector publishes to:

`POST /api/homes/{homeId}/anomalies`

Use the existing bearer authentication for the home's owner. Other tenants cannot enqueue or read events. The endpoint remains available for external detectors alongside the built-in category detector. External events also require positive usual watts and at least 50% excess in both the event and fresh reading before automatic action.

```json
{
  "eventId": "dryer-2026-09-26T20:00:00Z",
  "entityId": "switch.dryer",
  "usualWatts": 1000,
  "observedWatts": 5000,
  "recommendedAction": "Off",
  "detectedUtc": "2026-09-26T20:00:00Z"
}
```

- `eventId`: stable detector event ID, at most 100 characters. Replays return the existing event; conflicting payloads are rejected.
- `entityId`: a discovered device belonging to this home.
- Power values: finite watts, 0–1,000,000, and different from each other. The API returns signed `differenceWatts` (observed minus usual).
- `recommendedAction`: `Off` or `Inspect`. Arbitrary provider commands are never accepted.
- `detectedUtc`: ISO UTC timestamp ending in `Z` (also preserved in responses after database reload). More than 30 seconds in the future is rejected. Events older than five minutes notify without automatic action.
- Queue capacity: 1,000 unprocessed events per home. Processing waits for fresh telemetry. Turning on the setting does not replay events already processed as notification-only.
- Different event IDs for one device cannot trigger another anomaly action within one hour of an attempted event, or while a command is pending. This bounds repeated shutoffs and overlapping savings.

`PUT /api/homes/{homeId}/anomaly-savings` with `{"enabled": true}` changes only this setting. Disabling cancels pending anomaly retries; commands already sent cannot be undone. The existing command-cancel API also applies. Home responses include the newest 30 anomalies, per-command `anomalyId`, and all-time savings totals. The ledger retains all events; a production deployment with sustained high volume should add paginated history and a retention policy.

## Savings estimate

The initial product assumption is that excess usage would have continued unnoticed for **one hour**. This is a deliberately bounded heuristic, not a learned appliance runtime or a measured utility saving. A later version can use device-specific historical cycle durations when those estimates are reliable.

`avoided watts = max(0, min(event observed watts, fresh watts at each dispatch) − usual watts)`

`estimated kWh = avoided watts / 1000 × observed-off seconds / 3600`

Credit begins only after an attempted shutoff is confirmed off. It accumulates for consecutive off observations at most 20 seconds apart, for at most one hour after confirmation. Monitoring gaps earn nothing. Restarting the device or changing its power mapping closes that event's estimate. Notification-only, failed, expired, cancelled, and already-off events earn no savings. Turning the feature off does not erase earlier confirmed savings.

The UI multiplies estimated kWh by **$0.16/kWh** and labels both the dollar value and energy as estimates. This rounds Texas's July 2026 residential average of 15.88 cents/kWh from [EIA Table 5.6.A](https://www.eia.gov/electricity/monthly/epm_table_grapher.php?t=epmt_5_6_a), checked September 26, 2026. It is not the user's tariff. For 4 kW of excess usage observed off for a full hour, the widget shows 4 kWh and $0.64.

## Verification

Backend tests cover the settings/permissions matrix, signed anomalies, tenant isolation, event replay/conflict checks, stale and normalized events, per-device deduplication, retry cancellation, persisted queues, schema upgrades, the one-hour cap, telemetry gaps, and device restarts. UI tests cover save failures, estimate conversion, Never-device recommendations, and browser notification deduplication.
