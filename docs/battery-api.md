# Battery telemetry API

`GET /api/homes/{homeId}/battery` returns the signed-in owner's current battery
telemetry. Use the same bearer token and home ID as the existing homes API.
Unknown homes and homes belonging to another account both return 404;
unauthenticated requests return 401. Responses are marked `Cache-Control: no-store`.

Example response (values are illustrative):

```json
{
  "homeId": "2ea09c42-7c8a-4573-bc96-9c05da020833",
  "capacityKwh": 13.5,
  "storedEnergyKwh": 10.8,
  "stateOfChargePercent": 80,
  "observedAtUtc": "2026-09-26T12:00:00+00:00",
  "isSimulated": true
}
```

Capacity and stored energy are in **kWh**, distinct from the existing household
power limit in watts. Charge percentage is derived from stored energy / capacity.
This models one aggregate battery per home. It is independent of Home Assistant's
connection state and does not change Smart Shutoff or automatic restore decisions.

## Simulation configuration

The API currently registers `SimulatedBatteryProvider`. Override these settings
in `appsettings.Local.json` or using environment variables such as
`Battery__Simulation__CapacityKwh`:

```json
{
  "Battery": {
    "Simulation": {
      "CapacityKwh": 13.5,
      "InitialStateOfChargePercent": 80,
      "NetPowerWatts": 1000
    }
  }
}
```

These are demonstration defaults, not hardware specifications. All values must
be finite, capacity must be positive, and starting charge must be between 0 and
100. Invalid configuration fails startup.

Each home's simulation starts on its first battery read, uses the same configured
profile, and advances with elapsed time. Positive net power discharges; negative
net power charges; zero holds the starting charge. For example, a 1,000 W load
consumes 1 kWh per hour. Energy stays between zero and capacity. Repeated reads do
not consume additional energy. The simulation uses a constant configured load,
not actual household usage. State lives in memory and resets on API restart;
configuration changes require a restart.

## Connecting a future Core API

The controller depends on `IBatteryService`. `BatteryService` checks home ownership,
calls `IBatteryProvider`, validates telemetry, and calculates the percentage. The
simulation is an adapter in the API host, alongside the Home Assistant adapter.

Implement `IBatteryProvider.ReadAsync(homeId, cancellationToken)` for the real
dependency and replace the provider registration in `BatteryRegistration` with
the adapter's registration (for example, a typed HTTP client). The adapter owns
mapping internal home IDs to external battery IDs, credentials, HTTP timeouts,
and vendor response parsing. Return `BatterySnapshot` in kWh with the source's
observation timestamp and `IsSimulated = false`. No controller or service changes
are required. Remove simulation options registration when replacing simulation.

Represent missing/unavailable telemetry with `BatteryProviderException`, not a
zero reading. HTTP failures and provider timeouts are translated to a battery
failure by the service. Failures and invalid readings return 502 with a generic
battery-specific problem response; they never silently fall back to simulation.
Preserve request cancellation. Any retry or caching policy belongs in the external
adapter and must preserve the actual observation timestamp.

## Local PanelBench battery

The outage lab can opt into the native PanelBench battery instead of the
constant-load demonstration:

```json
{
  "Battery": {
    "PanelBench": {
      "Enabled": true,
      "HomeAssistantOrigin": "http://localhost:8123"
    }
  }
}
```

Restart the API after changing this setting. The adapter reads the loopback
PanelBench health endpoint on port 18081 and only serves homes paired to the
configured HA origin. The returned telemetry still has `isSimulated: true`, but
charge now follows the native simulated battery supplying actual lab loads.
Unavailable or malformed readings produce an error, never a fallback. The
constant-load provider remains the default when this option is absent.
