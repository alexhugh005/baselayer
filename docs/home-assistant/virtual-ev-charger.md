# Virtual EV charger

Installed in the local Johnson Household Home Assistant lab on 2026-09-25.
Open http://localhost:8123/energy-lab/devices and use the **Virtual EV Charger** card.

- Enable charging resumes/pauses charging through `switch.virtual_ev_charger`.
- Vehicle plugged in simulates cable connection.
- Current limit ranges from 6–48 A at a fixed 240 V: 32 A draws 7,680 W.
- `sensor.virtual_ev_charger_power` reports instantaneous W; unplugged or paused is 0 W.
- `sensor.virtual_ev_charger_energy` integrates consumption in kWh.
- Status reports Disconnected, Paused, or Charging. Battery capacity, taper, and completion are not modeled.

The switch represents permission to charge; it can remain enabled while the vehicle is unplugged. Reconnecting then resumes charging. Startup is plugged in, paused, 32 A.

The local household meter includes charger power; demo reset and overload scripts pause the charger to keep their existing scenarios deterministic. Both local dashboards include the charger card. Configuration backups use `.before-ev` suffixes.

## Base Layer mapping

In Device settings, map **Virtual EV Charger** (`switch.virtual_ev_charger`) to **Virtual EV Charger Power** (`sensor.virtual_ev_charger_power`). Permit control if you want it included in approved shutoffs. Whole-house mode already includes the load in the local household meter. Device-sum mode requires the power mapping. The existing provider supports native switch controls and power sensors, so no application code changes are needed.

## Reuse

Copy `virtual_ev_charger.yaml` into an HA packages directory included by `homeassistant.packages`, check configuration, then restart HA. The package uses built-in template and integration entities, without a physical device-registry entry. Dashboard cards and whole-house aggregation must be added separately on another instance.

## Verification

Home Assistant 2026.9.3 configuration validation passed. Live API checks confirmed 32 A = 7,680 W, 48 A = 11,520 W, unplugging = 0 W, native `switch.turn_off` = Paused/0 W, household total changes by exactly the charger load, and the energy sensor reports kWh. Restored to plugged in, paused, 32 A. Dashboard verified with the new card and live states.

Real charger entities vary by integration. [Wallbox](https://www.home-assistant.io/integrations/wallbox/) exposes charging power, energy, status, a current limit, and a pause/resume switch.
