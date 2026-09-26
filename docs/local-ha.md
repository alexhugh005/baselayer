# Historical Home Assistant fixture

These notes describe the virtual house used during local verification. The former
`smart-home-plugins` folder, including its fixture and archived integrations, has
been removed from the repository. The commands below apply only to a separately
preserved copy of that fixture. New checkouts should connect an existing Home
Assistant instance through native OAuth.

The platform API must be able to reach the Home Assistant URL; a cloud deployment
cannot access a user's localhost without a separate private connectivity solution.

## Entity mappings

Select `sensor.virtual_household_power` as the whole-house **W** meter. It sums
all six device meters plus `input_number.base_layer_background_watts`.

| Control | Power meter (W) |
| --- | --- |
| `switch.virtual_heater` | `sensor.virtual_heater_power` |
| `switch.virtual_dryer` | `sensor.virtual_dryer_power` |
| `switch.virtual_desk_lamp` | `sensor.virtual_desk_lamp_power` |
| `light.virtual_living_room_light` | `sensor.virtual_living_room_light_power` |
| `fan.virtual_ceiling_fan` | `sensor.virtual_ceiling_fan_power` |
| `climate.virtual_room_thermostat` | `sensor.virtual_hvac_power` |

These template entities have no physical device-registry association; explicitly
map each power meter in the platform instead of inferring a mapping from a name.
The three switches also have `sensor.virtual_*_energy` meters in kWh. Do not use
accumulated energy meters as live power readings.

## Approval test

Run `script.base_layer_overload` from Home Assistant. Background reaches 10 kW,
dryer 5 kW, heater 2.5 kW, plus lighting. No local automation shuts devices off.
Approve the platform's recommended dryer shutoff, then heater if still over
11 kW. The actual switch state and power readings should confirm each command.
Use `script.base_layer_reset` afterward. Standard light/fan/climate controls can
be exercised on the dashboard's Standard Devices page.

Run `HA_TOKEN=<access token> python3 verify.py` from the fixture directory for
native switch, light, fan and climate shutoff/state tests and an 18-second
no-auto-shedding overload check. Credentials must remain local.
