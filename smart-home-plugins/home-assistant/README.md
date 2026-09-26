# Local Home Assistant energy lab

Open http://localhost:8123/energy-lab/devices (or the Overview dashboard).

The username is `lab`; the generated password is in the local, git-ignored
`credentials.txt` file (readable only by your macOS user).

This isolated Docker instance binds only to localhost. It contains virtual appliances,
not physical devices. Docker Desktop must be running. No hardware or subscriptions
are needed. Automatic discovery is not enabled.

## Try it

- Heater: 1,500 W initially, adjustable to 2,500 W.
- Dryer: 2,400 W initially, adjustable to 5,000 W.
- Desk lamp: 12 W initially, adjustable to 100 W.
- Each appliance has a switch, measured power in W, and accumulated energy in kWh.
- Turning a switch off makes its measured power zero, retaining the slider setting.
- Use **Test overload** to exercise Base Layer's household 11 kW limit.
- **Reset demo** restores power settings and household background load. It does
  not erase historical energy totals.

These are template entities simulating appliances, rather than discovered hardware
entries. Future plugins can read the sensors and call switch.turn_off with the same
entity API used for supported real switches. Energy meters can also be selected in
Home Assistant's Energy dashboard under individual devices.

## Manage

Run from this directory:

```sh
docker compose up -d
docker compose stop
docker compose logs --tail=100 homeassistant
docker compose exec homeassistant python -m homeassistant --script check_config --config /config
```

Configuration lives in `config/packages/virtual_devices.yaml`; dashboard layout
lives in `config/energy-lab.yaml` and `config/ui-lovelace.yaml`. After changing device
configuration, restart with `docker compose restart homeassistant`.

Power settings and switches return to their configured initial values on restart.
Energy history and login data persist in `config/`. Do not commit `.storage`,
databases, logs, or credentials.

## Base Layer — household power limit

Base Layer replaces Energy Guard. It is installed and configured with an **11 kW
(11,000 W) live household power limit**, not a cumulative kWh budget.

The whole-house simulator adds the heater, dryer, desk lamp, living room light,
ceiling fan, HVAC and adjustable other household load. Only the heater, dryer and
desk lamp are eligible for automatic shutoff. The remaining loads count toward
the household total but are never selected for shutoff.

At or above 11 kW, Base Layer chooses the eligible running device with the highest
current wattage. After sending turn_off, it requires the off state, a reduced
device-power reading, and a fresh household reading before choosing another.
It stops once total load is below 11 kW. It never automatically turns devices on.

### Test it

Open http://localhost:8123/energy-lab/devices and run **Test overload**. This sets
the dryer to 5 kW, heater to 2.5 kW and background load to 10 kW. The dryer is shut
off first, then the heater; other small loads remain on. **Reset demo** restores
the ordinary appliance settings and 1 kW of background load.

### Configuration

Settings → Devices & services → Add integration → Base Layer. Select a total
household consumption sensor in W or kW (not a net-grid or cumulative energy
meter), then add switch/power-sensor pairs for devices allowed to be switched off.
The configured test instance already has this entry. Remove the entry and add it
again to change the device selection. One controller is allowed per instance.

Source: `config/custom_components/base_layer/`. Previous Energy Guard source and
test configuration are archived under `archive/` and are not loaded.

### Limits and failure behavior

Invalid household readings pause control; invalid individual readings exclude
that candidate. A failed command or unconfirmed reading stops the current sequence
and creates an alert; the controller retries after a 60-second cooldown. If no
eligible device can reduce the load further, it alerts rather than controlling
unapproved devices. Meter reporting delays affect how quickly load is reduced.
This is software load management, not an electrical protection device.

### Validation

`python3 test_base_layer.py` checks W/kW conversion, invalid readings, eligibility
and highest-usage selection. Live checks verified 10.9 kW does not trigger, exactly
11 kW triggers, dryer-before-heater ordering, stopping below 11 kW with the lamp
still on, and leaving shed devices off. Missing-meter and no-eligible-device
checks also passed. The lab was reset after testing.
