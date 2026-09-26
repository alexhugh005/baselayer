"""Live acceptance tests for the explicitly virtual Energy Lab, using its saved token.

Exercises real HA services -> SPAN MQTT relays -> appliance interlocks -> meters.
Leaves all circuits healthy and the original quiet-home scenario running.
"""
import json
import time
import urllib.request
from pathlib import Path

from lab_client import request, service

HERE = Path(__file__).resolve().parent
CIRCUITS = json.loads((HERE/'circuits.json').read_text())
RELAYS = json.loads((HERE/'relays.json').read_text())


def states():
    return {s['entity_id']: s['state'] for s in request('/api/states')}


def panel(path='/health', data=None):
    port = 18081 if path == '/health' else 18080
    req = urllib.request.Request(f'http://localhost:{port}'+path,
        data=None if data is None else json.dumps(data).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)


def wait(test, label, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        current = states()
        if test(current):
            return current
        time.sleep(.4)
    raise AssertionError(label + ': ' + json.dumps({k:v for k,v in current.items() if 'span_' in k or k.startswith(('switch.virtual_', 'climate.virtual_', 'fan.virtual_', 'light.virtual_'))}))


def start(key):
    for entity in CIRCUITS[key]['devices']:
        domain = entity.split('.')[0]
        if domain == 'climate':
            service(domain, 'set_hvac_mode', entity, hvac_mode='heat')
        else:
            service(domain, 'turn_on', entity)


def stopped(key, current):
    return (all(current[e] == 'off' for e in CIRCUITS[key]['devices']) and
            all(float(current['sensor.virtual_'+m+'_power']) == 0 for m in CIRCUITS[key]['meters']) and
            current[f'binary_sensor.lab_span_{key}_supply'] == 'off')


def totals(current):
    house = float(current['sensor.virtual_household_power'])
    circuit_sum = sum(float(current[f'sensor.lab_span_{key}_power']) for key in CIRCUITS)
    return abs(house - circuit_sum) < .05 and abs(float(current['sensor.span_panel_current_power']) - house) < .1


def main():
    config = request('/api/config')
    assert config['location_name'] == 'Johnson Household', 'Wrong HA instance'
    assert 'Illustrative' in request('/api/states/sensor.virtual_household_power')['attributes']['model'], 'Not the virtual lab'
    wait(lambda s:s.get('binary_sensor.lab_span_bridge_connected') == 'on', 'Live bridge ready', 45)
    try:
        service('script', 'lab_span_restore_supply')
        wait(lambda s:all(s[f'binary_sensor.lab_span_{k}_supply']=='on' for k in CIRCUITS), 'Supplies ready')
        service('script', 'base_layer_reset')
        wait(totals, 'Panel watts equal appliance sum')
        print('PASS live SPAN panel total equals the existing Energy Lab total', flush=True)
        for key,item in CIRCUITS.items():
            # A separate room is our isolation witness.
            other = 'switch.virtual_desk_lamp' if key != 'office' else 'light.virtual_living_room_light'
            service(other.split('.')[0], 'turn_on', other)
            start(key)
            fault = f'input_boolean.lab_span_{key}_fault'
            service('input_boolean', 'turn_on', fault)
            wait(lambda s:stopped(key,s) and s[RELAYS[key]]=='off', key+' outage')
            assert panel()['relays'][key] == 'OPEN', key+' emitter did not open'
            start(key)  # API controls cannot bypass a dead circuit.
            wait(lambda s:stopped(key,s), key+' restart interlock')
            time.sleep(1)
            current = states()
            assert stopped(key,current), key+' restarted on a dead circuit'
            assert current[other]=='on', key+' cut an unrelated area'
            wait(totals, key+' zero power propagates to panel')
            # Reclosing the native breaker during an active fault is rejected.
            time.sleep(2.2)  # Respect the integration's physical relay debounce.
            service('switch', 'turn_on', RELAYS[key])
            wait(lambda s:s[RELAYS[key]]=='off' and stopped(key,s), key+' fault enforcement')
            service('input_boolean', 'turn_off', fault)
            wait(lambda s:s[f'binary_sensor.lab_span_{key}_supply']=='on', key+' restored')
            assert all(states()[e]=='off' for e in item['devices']), key+' unexpectedly restarted loads'
            print('PASS '+key+': isolated fault, zero watts, blocked restart, manual recovery', flush=True)

        # Direct SPAN commands also control the household, without fault helpers.
        start('living_room')
        service('switch', 'turn_off', RELAYS['living_room'])
        wait(lambda s:stopped('living_room',s), 'Native SPAN relay outage')
        time.sleep(2.2)
        service('switch', 'turn_on', RELAYS['living_room'])
        wait(lambda s:s['binary_sensor.lab_span_living_room_supply']=='on', 'Native relay recovery')
        print('PASS native SPAN breaker independently cuts the living-room light and fan', flush=True)

        start('living_room')
        panel('/entities/living_room/relay', {'relay_state': 'OPEN'})
        wait(lambda s:stopped('living_room',s), 'PanelBench dashboard outage')
        assert panel()['relays']['living_room'] == 'OPEN'
        panel('/entities/living_room/relay', {'relay_state': 'CLOSED'})
        wait(lambda s:s['binary_sensor.lab_span_living_room_supply']=='on', 'PanelBench dashboard recovery')
        print('PASS PanelBench dashboard controls the same confirmed relay and appliances', flush=True)

        service('script', 'base_layer_overload')
        wait(lambda s:float(s['sensor.virtual_household_power']) > 13000, 'Busy household')
        wait(totals, 'Busy household panel metering')
        service('input_boolean', 'turn_on', 'input_boolean.lab_span_main_outage')
        wait(lambda s:all(stopped(k,s) for k in CIRCUITS) and float(s['sensor.virtual_household_power'])==0, 'Whole-house outage')
        wait(totals, 'Panel total during whole-house outage')
        print('PASS 13 kW busy household -> whole-house outage -> zero watts everywhere', flush=True)
    finally:
        service('script', 'lab_span_restore_supply')
        wait(lambda s:all(s[f'binary_sensor.lab_span_{k}_supply']=='on' for k in CIRCUITS), 'Final supplies restored')
        service('script', 'base_layer_reset')
    wait(totals, 'Final meter consistency')
    print('Restored healthy circuits and quiet-home scenario.', flush=True)


if __name__ == '__main__':
    main()
