# Realistic household lab

Installed and verified in the local Johnson Household Home Assistant instance on
2026-09-26. Open <http://localhost:8123/energy-lab/devices>.

This is an illustrative, real-time electrical and thermal simulation. It does not
measure physical hardware or claim calibration to a particular household.
Existing switch, light, fan, climate, and power-sensor entity IDs are preserved.

The installed lab now uses the [virtual SPAN smart panel](../span-panel/README.md).
Open <http://localhost:8123/energy-lab/panel> to inject area faults or a whole-house
outage. Circuit loss turns assigned devices off, removes standby power, and
blocks restart attempts until supply returns. PanelBench must be connected for
the circuit supply interlocks to enable these virtual appliances.

## Models

| Load | Behavior |
| --- | --- |
| Space heater | 750 or 1,500 W; separate target temperature and ±0.5°F hysteresis; 0.5 W electronics standby. |
| Electric dryer | 45 minutes of active run time. First 40 minutes alternate 4 minutes at 5,000 W with 2 minutes at 300 W; last 5 minutes cool down at 300 W. Finishes automatically; 1 W standby. |
| Desk lamp | LED, default 12 W, configurable 4–15 W rating; 0 W at its mechanical switch. |
| Living-room LED | Up to 12 W; illustrative nonlinear dimming curve and 0.4 W smart-control standby. |
| Ceiling fan | Three speeds at 18/35/60 W; 0.5 W remote-control standby. |
| Central heat pump | Heating mode: 3,000 W electrical input / 9,000 W thermal output (assumed COP 3); 8 W idle. Native thermostat with 3-minute minimum run and cooldown periods. |
| EV charger | 240 V, 6–48 A limit; default 32 A / 7,680 W. Default 75 kWh usable battery, 40% initial charge, 80% target. 90% AC-to-battery efficiency after 3 W charger electronics; illustrative taper above 80%; stops at target. EVSE consumes 3 W when paused, complete, or unplugged from the car. |
| Refrigerator | Power switch; 150 W compressor for 10 of every 30 minutes; 3 W idle, 0 W switched off. Existing meter remains included in background power. |
| Dishwasher | 90-minute run/pause cycle: 10-minute pre-rinse at 30 W, 50-minute wash alternating 3 minutes at 1,200 W and 7 minutes at 100 W, 10-minute rinse at 100 W, then 20-minute dry at 600 W. Stops automatically; 1 W standby. |
| Toaster | 1,200 W for a 3-minute cycle, automatic shutoff, 0 W idle. Turning off cancels; turning on starts a fresh cycle. |
| Water heater | 50-gallon mixed tank, 4,500 W element, 2 W enabled standby, 0 W off. Adjustable 100–140°F target (default 120°F); reheats 5°F below target. 55°F inlet, adjustable 0–3 gal/min hot-water draw, 220 Wh/K thermal mass and 2 W/K tank heat loss. |
| Other electronics | Default 180 W, bounded to 50–500 W. Represents router, security equipment, and other unmodeled standby/always-on loads. Does not duplicate the modeled appliances. |

The indoor temperature advances every 10 seconds from heat balance: heat pump,
space heater, 250 W internal thermal gains, and outdoor heat loss. Assumed effective
thermal mass is 3 kWh/K and heat-loss coefficient is 180 W/K. Default outdoor
temperature is a 55°F heating-season scenario, not live weather. The room can be
seeded manually on the Standard Devices page. The model clamps its scenario range
to 50–95°F. It does not model cooling, humidity, separate rooms, variable COP,
compressor inrush, voltage sag, dryer moisture sensing, or manufacturer-specific
charging curves. Appliance duty cycles and standby values are illustrative.

The dryer switch models **run/pause** and preserves elapsed time when paused.
Turning it on after completion starts a new load. This does not assert that a
physical dryer will automatically resume after a power interruption.
Battery charge, dishwasher/dryer/toaster progress, water/indoor temperature and refrigerator cycle time
restore after a Home Assistant restart. Controllable large loads start paused/off;
the model does not simulate unattended operation while Home Assistant is stopped.

## Scenarios and controls

- **Reset realistic household** restores the documented quiet-home defaults,
  with heater, dryer, dishwasher, toaster, water heater, HVAC and EV charging off, fridge and lamps on,
  tank at 120°F, hot-water draw at zero, and simulation running.
- **Busy household: laundry and EV** resets the scenario, starts laundry and
  32 A charging, and sets the fan to medium. Initial consumption is approximately
  13.1 kW. There is no artificial 10 kW background load or oversized portable heater.
- **Run real-time simulation** pauses/resumes the modeled clock, dryer progress,
  battery charge, and thermal evolution. Electrical readings still reflect
  enabled loads and their cumulative energy meters continue integrating; this is
  a diagnostic clock control, not a whole-house disconnect.
- Base Layer's **11 kW threshold** remains its demonstration control limit. It is
  not the home's electrical service rating. This update does not change Base
  Layer permissions, shutoff rules, restore policy, or battery API.

Household watts sum all seven original appliance meters, toaster, dishwasher, water heater and the background
meter (which already includes the refrigerator). Do not additionally add the
refrigerator when totaling background power. Watt-hours/kWh accumulate from watts
over real elapsed time. Existing energy counters retain earlier lab history;
resetting the scenario does not erase those counters.

## Install and verify

Back up the existing configuration first. Install all package files, replacing same-named files in
the Home Assistant `packages` directory with this directory's `packages/*.yaml`.
Do not load old and new versions simultaneously. Include packages using
`homeassistant.packages: !include_dir_named packages`. Configure a YAML Lovelace
dashboard using `dashboard.yaml`. For the existing local layout, copy the dashboard
to both `energy-lab.yaml` and `ui-lovelace.yaml`.

Run Home Assistant's configuration check, restart, then run
`script.base_layer_reset` once to initialize the scenario. For the local container:

```sh
docker exec basehack-homeassistant python -m homeassistant --script check_config --config /config
docker restart basehack-homeassistant
HA_URL=http://localhost:8123 HA_TOKEN=... python3 docs/home-assistant/realistic-lab/verify.py
```

The verifier intentionally changes virtual devices and leaves the quiet-home
scenario running. It checks standby, household summation, dryer phase boundaries,
pause/resume and completion, fan speeds, dimming, heater demand, heat loss/heating,
EV charge-rate math, taper, completion, refrigerator switching/cycling, toaster completion,
water heating/draw/thermostat behavior, overload relief and
kWh units. It advances simulated state to test boundaries without waiting 45
minutes or a full battery charge. Live configuration validation and these checks
passed on Home Assistant 2026.9.3.

## Reference points

- [Lasko 1,500 W portable heater](https://lasko.com/products/lasko-ct22850-1500w-ceramic-tower-space-heater-with-tip-switch-and-remote-black)
- [DOE appliance reference: 5,000 W conventional dryer](https://www1.eere.energy.gov/education/pdfs/efficiency_energyinhome.pdf)
- [DOE LED lighting guide](https://www.energy.gov/sites/default/files/2021-08/ES-EE%20Lighting_080921.pdf)
- [Tesla Level 2 charging specifications](https://www.tesla.com/support/charging/wall-connector)
- [Home Assistant generic thermostat](https://www.home-assistant.io/integrations/generic_thermostat/)
- [Home Assistant template entities](https://www.home-assistant.io/integrations/template/)

These references support the approximate ratings and integration behavior, not
every parameter of the illustrative model.

## Added appliance mappings

| Control | Power meter (W) | Energy meter (kWh) |
| --- | --- | --- |
| `switch.virtual_refrigerator` | `sensor.virtual_refrigerator_power` | `sensor.virtual_refrigerator_energy` |
| `switch.virtual_dishwasher` | `sensor.virtual_dishwasher_power` | `sensor.virtual_dishwasher_energy` |
| `switch.virtual_toaster` | `sensor.virtual_toaster_power` | `sensor.virtual_toaster_energy` |
| `switch.virtual_water_heater` | `sensor.virtual_water_heater_power` | `sensor.virtual_water_heater_energy` |

These switches are discoverable through the existing Base Layer switch integration.
Map their meters explicitly when configuring devices. Water heater temperature and
draw controls are on the Home Assistant dashboard; its switch enables the thermostat.
The refrigerator retains its simple duty cycle rather than modeling food temperature.
Toaster and water-heater ratings are illustrative simulation parameters.

The dishwasher is on the simulated Kitchen Outlets circuit. Circuit loss stops it
and freezes progress; restoring supply requires manually resuming the cycle. Its
switch models run/pause, and enabling a completed cycle starts a fresh load.
The dashboard shows phase and remaining minutes; Reset dishwasher cycle clears
progress. These are illustrative values, not a particular appliance specification.

Dishwasher verification: `python3 docs/home-assistant/realistic-lab/verify_dishwasher.py`
uses the saved local SPAN lab token and checks cycle boundaries, pause/resume,
automatic completion, power totals, and circuit interruption. It leaves the
dishwasher ready and restores the prior simulation-clock and kitchen helper states.
