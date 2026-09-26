"""Integration smoke test. Set HA_TOKEN to a local Home Assistant access token."""
import json
import os
import time
from urllib.request import Request, urlopen

BASE = os.environ.get('HA_URL', 'http://127.0.0.1:8123')
TOKEN = os.environ['HA_TOKEN']

def api(path, data=None):
    req = Request(BASE + '/api/' + path,
                  data=None if data is None else json.dumps(data).encode(),
                  headers={'Authorization': 'Bearer ' + TOKEN,
                           'Content-Type': 'application/json'})
    with urlopen(req, timeout=15) as response:
        return json.load(response)

def service(domain, action, **data):
    return api(f'services/{domain}/{action}', data)

def state(entity):
    return api('states/' + entity)['state']

def expect(entity, expected, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        actual = state(entity)
        if actual == str(expected) or (isinstance(expected, (int, float)) and actual not in ('unknown', 'unavailable') and float(actual) == expected):
            return
        time.sleep(.25)
    raise AssertionError(f'{entity}: expected {expected}, got {actual}')

try:
    service('script', 'lab_reset')
    for slug, watts in [('virtual_heater', 1500), ('virtual_dryer', 2400), ('virtual_desk_lamp', 12)]:
        expect('sensor.' + slug + '_power', watts)
        service('switch', 'turn_off', entity_id='switch.' + slug)
        expect('sensor.' + slug + '_power', 0)
        service('switch', 'turn_on', entity_id='switch.' + slug)
        expect('sensor.' + slug + '_power', watts)
    print('PASS: all three switches change power to zero and restore it')
    service('input_number', 'set_value', entity_id='input_number.heater_watts', value=2100)
    expect('sensor.virtual_heater_power', 2100)
    time.sleep(16)
    expect('switch.virtual_heater', 'on')
    print('PASS: high usage does not shut off with demo disabled')
    service('input_number', 'set_value', entity_id='input_number.heater_watts', value=1500)
    service('input_boolean', 'turn_on', entity_id='input_boolean.demo_auto_shutoff')
    service('input_number', 'set_value', entity_id='input_number.heater_watts', value=2100)
    time.sleep(3)
    service('input_number', 'set_value', entity_id='input_number.heater_watts', value=1500)
    time.sleep(13)
    expect('switch.virtual_heater', 'on')
    print('PASS: brief spike does not shut off the heater')
    service('input_number', 'set_value', entity_id='input_number.heater_watts', value=2100)
    expect('switch.virtual_heater', 'off', timeout=20)
    expect('sensor.virtual_heater_power', 0)
    print('PASS: sustained high usage shuts off the heater')
    for slug in ['virtual_heater', 'virtual_dryer', 'virtual_desk_lamp']:
        value = float(state('sensor.' + slug + '_energy'))
        assert value > 0, (slug, value)
    print('PASS: all three energy meters accumulate kWh')
finally:
    service('script', 'lab_reset')
    print('Reset lab to initial test settings')
