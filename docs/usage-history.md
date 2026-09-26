# Device usage history

The API records device usage automatically after each successful background Home
Assistant poll. No browser needs to stay open. The API server must remain running.
History starts when this feature is enabled; it does not import earlier Home
Assistant data. Shutoff permissions do not affect recording.

## Stored data

- **DeviceMeasurementSeries:** associates a device with its mapped power sensor,
  mapping revision, and start/end timestamps. Changing or removing a mapping
  closes the old series. Renaming a device preserves its history.
- **DeviceUsageBuckets:** one row per series, UTC minute, and resolution, containing
  watt-seconds, covered seconds, observed power extrema, and sample count.
  Hourly summaries use the same table with `resolution = hour`.
- **DeviceStateEvents:** observed state changes and the first state after startup
  or an observation gap. Unmetered devices still have state history.
- **DeviceUsageCursors:** internal checkpoints saved in the same transaction as
  the buckets/events, preventing replayed observations from adding energy twice.

Minute rows are updated as observations arrive, not appended for every poll.
The server also commits the cursor each poll; this design limits retained rows,
not the frequency of database writes. History is queried separately from the
live home response and is never loaded as a Home navigation collection.

All timestamps are UTC. Sensor entity IDs are scoped to their home; device IDs
are stable within Base Layer. Deleting a home cascades through its usage history.
Revocation stops collection but keeps already-recorded history.

## Calculation and gaps

Energy is **estimated from power**, not billing-grade cumulative meter telemetry.
We use a left-rectangle integral: the previous power reading multiplied by the
actual elapsed seconds. Intervals are split at minute boundaries. The provider
normalizes W/kW to watts before recording.

Both endpoint readings must be valid, the mapping must be unchanged, and the
interval must be at most 20 seconds. Failed polls, missing devices, invalid
readings, and longer intervals leave gaps. A new server process always starts a
new observation segment. A repeated snapshot timestamp is ignored.

- `averageWatts = wattSeconds / coveredSeconds`
- `estimatedEnergyWh = wattSeconds / 3600`
- `coveragePercent = coveredSeconds / bucketDurationSeconds * 100`

If coverage is zero, average power and energy are null. A fully missing bucket is
absent from the response. Neither case means zero consumption. Measured zero is
valid, and off devices can contribute standby power. Unchanged valid observations
continue accumulating energy. Min/max are sampled extrema, not guaranteed true
peaks between polls. SampleCount counts observations at their observation minute;
a carried interval may contribute energy to a minute with no new sample.

The first and current minute may be partial. Never present a partial energy sum
as a complete day's consumption or extrapolate over gaps without labeling it.
State events describe observed states, not exact physical switching times; a
new observation segment must not be joined to the previous one as continuous
runtime. REST observations reflect Home Assistant's reported state; source-level
sensor freshness and hardware failures beyond HA's availability reporting are
not independently verified by this recorder.

## Aggregation and retention

A maintenance worker runs at startup and every minute, processing up to 24 pending
hours per pass so it can catch up without one unlimited write transaction.
Only completed hours, plus a 20-second grace period, are eligible. The open hour
is available through minute queries; it is not returned as a partial hourly row.

For each series/hour, the worker recomputes totals from source minutes and saves
the hourly row and source completion markers in one transaction. Retries replace
the total rather than adding to it. Energy and coverage are summed, averages
are weighted by covered time, and extrema are preserved.

Minute data is retained for at least **90 days**, rounding the cutoff down to an
hour. Only minutes successfully aggregated are deleted. Hourly summaries and
state events are retained for **two calendar years**. Expired closed series with
no remaining buckets are removed. Failures are logged, and unaggregated minutes
remain available for retry. Retention dates are currently code constants, not
user-configurable settings.

At 20 continuously monitored devices, 90 days of minute data is roughly 2.6
million rows, plus hourly history. SQLite is intended for this project's current
single-instance deployment; monitor storage and write latency before scaling.

## API

Both endpoints require the normal bearer authentication and ownership of the home.
They return 404 for another account's device and disable response caching.
`entityId` is the existing Home Assistant ID, e.g. `switch.heater` (URL-encode it).

```http
GET /api/homes/{homeId}/devices/{entityId}/usage?from=2026-09-26T12:00:00Z&to=2026-09-26T13:00:00Z&resolution=minute
```

- `resolution`: `minute` (default) or `hour`.
- `from` is inclusive; `to` is exclusive. Both must align with the requested UTC
  minute/hour boundaries.
- Maximum range: 7 days for minutes, 93 days for hours. Fetch longer history in
  consecutive ranges. Responses exceeding 20,000 buckets or series require a
  smaller range and return 400 rather than silently truncating.
- The response includes device identity, range, `energyMethod:
  "estimatedFromPower"`, sensor-series metadata, and buckets with power, energy,
  coverage, and sample count. Each bucket identifies its measurement series.
- A sensor change within a minute/hour can produce multiple series buckets for
  that time; keep series distinct when learning baselines. To display a combined
  energy total, sum their energy and covered time rather than their average power.

```http
GET /api/homes/{homeId}/devices/{entityId}/state-history?from=2026-09-26T12:00:00Z&to=2026-09-27T12:00:00Z
```

State history supports ranges up to seven days and 10,000 events. It returns
`observedAtUtc`, `state`, and `startsObservationSegment`. It does not invent an
initial state at the requested range boundary.

## Upgrade and verification

Startup creates the new tables/indexes and adds a mapping revision column to
existing databases inside the existing explicit schema-upgrade transaction.
Homes, credentials, permissions, and command history are preserved. Repeating
the upgrade is safe. No additional package or database service is required.

`UsageHistoryTests` covers measured energy, irregular timing and boundary splits,
coverage gaps, standby/zero power, retries, restarts, sensor remapping, ownership,
range limits, hourly aggregation, retention, transactional failure recovery,
cascade deletion, and an existing-database upgrade.

```sh
dotnet test backend/BaseLayer.slnx --filter FullyQualifiedName~UsageHistoryTests
python3 scripts/verify-usage-history.py
```

The smoke script starts the built Debug API and a fake Home Assistant on local
loopback ports with a temporary database and random development credential. It
checks automatic recording, W/kW conversion, HTTP authentication and validation,
startup compaction/retention, and deletion. It stops its processes and removes its
temporary database afterward; no real Home Assistant connection is used.
