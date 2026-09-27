# Detecting battery backup through the API

`GET /api/homes` includes `powerSupply` on each authorized home. This is observed
backup mode from Home Assistant's SPAN grid-forming entity, independent of the
predicted `gridOutageRisk` and the meter-selection setting named `powerSource`.

Example fragment:

```json
{
  "powerSupply": {
    "state": "battery",
    "isOnBattery": true,
    "observedAtUtc": "2026-09-26T20:00:00Z",
    "sources": [
      { "entityId": "sensor.span_panel_grid_forming_entity", "state": "battery" }
    ]
  }
}
```

Backend consumers using `IPlatformService.HomesAsync(ownerId)` can check:

```csharp
if (home.PowerSupply?.IsOnBattery == true)
{
    // The home reports battery backup. Apply your application's response here.
}
```

Backend code with the persisted `Home` can call
`PowerSupplyDetection.Current(home, clock.GetUtcNow().UtcDateTime)` for the same
freshness-checked result. The HA provider also returns typed readings in
`ProviderSnapshot.PowerSupplies`, available during `PlatformService.ApplySnapshot`.

`isOnBattery` is nullable: true means battery, false means a confirmed other source,
and null means unknown. Check `state == "grid"` specifically when reacting to grid
restoration; false also covers solar, generator, or no supply. Missing sensors,
unavailable/unrecognized values, conflicting panel sources, revoked connections,
and snapshots older than 20 seconds produce unknown. Per-source states are also
masked as unknown for stale/revoked connections. `observedAtUtc` is the last
successful HA poll time, not the time the electrical transition occurred.

The existing background worker polls homes and waits three seconds between cycles;
this is polling, not a real-time webhook. If implementing one alert per outage,
persist the last confirmed mode and deduplicate confirmed transitions. Unknown
must not be interpreted as grid recovery. On startup, report an already-active
battery state separately from a newly observed grid-to-battery transition.

Detection reads `sensor.*_grid_forming_entity` entities, normalizing SPAN's
`GRID`, `BATTERY`, `PV`, `GENERATOR`, and `NONE` values case-insensitively. Keep
that suffix when renaming these entities. No inference is made from battery
discharge watts or zero grid consumption: both can occur while grid-connected.
This indicates reported islanded backup mode; it does not prove the reason for
islanding was a utility failure. SPAN can also retain a stale GFE if its own battery
communication fails, so this API cannot provide stronger guarantees than the
upstream signal. See the [integration's GFE documentation](https://github.com/SpanPanel/span#grid-forming-entity).

Restart the API to load the updated contract. Database initialization adds the
`PowerSupplyJson` column to existing installations without replacing home data.
The next successful home poll populates it. Detected battery transitions now feed
the separate [circuit outage recovery policy](outage-recovery.md), which restores
previously-on circuits within the battery budget. Predicted outage-risk policy and
notification delivery remain separate.
