"""Exercise the installed local simulation; finishes in the quiet-home scenario.

HA_URL=http://localhost:8123 HA_TOKEN=... python3 verify.py
Alternatively pass --credentials PATH for the existing local lab login file.
Never run against a physical household: this intentionally changes virtual states.
"""
import argparse
import json
import math
import os
import time
import urllib.parse
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--credentials', type=Path)
args = parser.parse_args()
base = os.environ.get('HA_URL', 'http://localhost:8123').rstrip('/')
token = os.environ.get('HA_TOKEN')

def req(path, data=None):
    request = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(),
        headers={'Content-Type': 'application/json', **({'Authorization': 'Bearer ' + token} if token else {})})
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)

if not token:
    if not args.credentials:
        parser.error('Set HA_TOKEN or provide the existing lab --credentials file')
    client = 'http://localhost:5173/'
    password = args.credentials.read_text().split('Password: ')[1].strip()
    flow = req('/auth/login_flow', dict(client_id=client, handler=['homeassistant', None], redirect_uri=client))
    auth = req('/auth/login_flow/' + flow['flow_id'], dict(client_id=client, username='lab', password=password))
    request = urllib.request.Request(base + '/auth/token', data=urllib.parse.urlencode(dict(
        grant_type='authorization_code', code=auth['result'], client_id=client)).encode())
    with urllib.request.urlopen(request, timeout=20) as response:
        token = json.load(response)['access_token']

def state(entity): return req('/api/states/' + entity)['state']
def value(entity): return float(state(entity))
def service(domain, action, entity=None, **data):
    req('/api/services/' + domain + '/' + action, {**({'entity_id': entity} if entity else {}), **data})
    time.sleep(.3)
def set_number(name, amount): service('input_number', 'set_value', 'input_number.' + name, value=amount)
def expect(entity, expected, tolerance=.02):
    actual = state(entity)
    assert (abs(float(actual) - expected) <= tolerance if isinstance(expected, (float, int)) else actual == expected), (entity, actual, expected)
def tick(seconds=10):
    set_number('lab_last_tick', time.time() - seconds)
    service('script', 'lab_simulation_tick')
def check_sum():
    states = {s['entity_id']: s['state'] for s in req('/api/states')}
    names = ['heater', 'dryer', 'desk_lamp', 'living_room_light', 'ceiling_fan', 'hvac', 'ev_charger', 'background', 'toaster', 'water_heater', 'dishwasher']
    total = sum(float(states['sensor.virtual_' + name + '_power']) for name in names)
    assert abs(float(states['sensor.virtual_household_power']) - total) < .02, (total, states['sensor.virtual_household_power'])

assert 'Illustrative' in req('/api/states/sensor.virtual_household_power')['attributes'].get('model', ''), 'Wrong lab configuration'
try:
    service('automation', 'turn_off', 'automation.real_time_household_physics')
    service('script', 'base_layer_reset')
    # The periodic trigger remains disabled; individual ticks are invoked deliberately below.
    expect('sensor.virtual_heater_power', .5)
    expect('sensor.virtual_dryer_power', 1)
    expect('sensor.virtual_ev_charger_power', 3)
    expect('sensor.virtual_hvac_power', 8)
    check_sum()
    expect('switch.virtual_refrigerator', 'on')
    expect('sensor.virtual_toaster_power', 0)
    expect('sensor.virtual_water_heater_power', 0)
    print('PASS quiet-home standby readings and household aggregation', flush=True)

    service('switch', 'turn_off', 'switch.virtual_refrigerator')
    expect('sensor.virtual_refrigerator_power', 0)
    expect('sensor.virtual_background_power', 180)
    check_sum()
    service('switch', 'turn_on', 'switch.virtual_refrigerator')
    expect('sensor.virtual_refrigerator_power', 150)

    service('switch', 'turn_on', 'switch.virtual_toaster')
    expect('sensor.virtual_toaster_power', 1200)
    expect('sensor.virtual_toaster_status', 'Toasting')
    check_sum()
    set_number('lab_toaster_elapsed', 90)
    service('switch', 'turn_on', 'switch.virtual_toaster')
    expect('input_number.lab_toaster_elapsed', 90)  # repeated turn-on does not restart
    service('switch', 'turn_off', 'switch.virtual_toaster')
    tick()
    expect('input_number.lab_toaster_elapsed', 90)
    expect('sensor.virtual_toaster_power', 0)
    service('switch', 'turn_on', 'switch.virtual_toaster')
    expect('input_number.lab_toaster_elapsed', 0)  # canceled toast starts fresh
    set_number('lab_toaster_elapsed', 179)
    tick()
    expect('switch.virtual_toaster', 'off')
    expect('sensor.virtual_toaster_status', 'Complete')
    expect('sensor.virtual_toaster_power', 0)
    service('switch', 'turn_on', 'switch.virtual_toaster')
    expect('input_number.lab_toaster_elapsed', 0)
    service('switch', 'turn_off', 'switch.virtual_toaster')

    set_number('lab_water_temperature', 110)
    service('switch', 'turn_on', 'switch.virtual_water_heater')
    expect('sensor.virtual_water_heater_power', 4500)
    check_sum()
    before = value('input_number.lab_water_temperature')
    tick(20)
    assert value('input_number.lab_water_temperature') > before
    set_number('lab_water_temperature', 120.1)
    tick()
    expect('sensor.virtual_water_heater_power', 2)
    expect('sensor.virtual_water_heater_status', 'At temperature')
    set_number('lab_water_temperature', 118)
    tick()
    expect('sensor.virtual_water_heater_power', 2)  # thermostat deadband
    set_number('lab_water_temperature', 114)
    tick()
    expect('sensor.virtual_water_heater_power', 4500)
    set_number('lab_hot_water_draw', 3)
    before = value('input_number.lab_water_temperature')
    tick(20)
    assert value('input_number.lab_water_temperature') < before
    service('switch', 'turn_off', 'switch.virtual_water_heater')
    expect('sensor.virtual_water_heater_power', 0)
    set_number('lab_hot_water_draw', 0)
    before = value('input_number.lab_water_temperature')
    tick(20)
    assert value('input_number.lab_water_temperature') < before  # passive tank heat loss
    check_sum()
    print('PASS fridge control, toaster cancellation/completion, water heating/draw/hysteresis and totals', flush=True)

    service('switch', 'turn_on', 'switch.virtual_dryer')
    expect('sensor.virtual_dryer_power', 5000)
    set_number('lab_dryer_elapsed', 250)
    expect('sensor.virtual_dryer_power', 300)
    expect('sensor.virtual_dryer_phase', 'Tumbling')
    service('switch', 'turn_off', 'switch.virtual_dryer')
    tick()
    expect('input_number.lab_dryer_elapsed', 250)
    expect('sensor.virtual_dryer_power', 1)
    service('switch', 'turn_on', 'switch.virtual_dryer')
    set_number('lab_dryer_elapsed', 2450)
    expect('sensor.virtual_dryer_phase', 'Cool down')
    expect('sensor.virtual_dryer_power', 300)
    set_number('lab_dryer_elapsed', 2699)
    tick()
    expect('switch.virtual_dryer', 'off')
    expect('sensor.virtual_dryer_phase', 'Complete')
    service('switch', 'turn_on', 'switch.virtual_dryer')
    expect('input_number.lab_dryer_elapsed', 0)
    service('switch', 'turn_off', 'switch.virtual_dryer')
    print('PASS dryer heating, tumbling, pause/resume, cooldown, completion, new cycle', flush=True)

    for speed, watts in [(33, 18), (66, 35), (100, 60), (0, .5)]:
        service('fan', 'set_percentage', 'fan.virtual_ceiling_fan', percentage=speed)
        expect('sensor.virtual_ceiling_fan_power', watts)
    service('fan', 'turn_on', 'fan.virtual_ceiling_fan')
    expect('sensor.virtual_ceiling_fan_power', 35)
    service('fan', 'turn_off', 'fan.virtual_ceiling_fan')
    service('light', 'turn_on', 'light.virtual_living_room_light', brightness=255)
    expect('sensor.virtual_living_room_light_power', 12)
    service('light', 'turn_on', 'light.virtual_living_room_light', brightness=64)
    assert .4 < value('sensor.virtual_living_room_light_power') < 12
    service('light', 'turn_off', 'light.virtual_living_room_light')
    expect('sensor.virtual_living_room_light_power', .4)
    print('PASS fan speeds and LED dimming/standby', flush=True)

    set_number('lab_room_temperature', 68)
    service('switch', 'turn_on', 'switch.virtual_heater')
    tick()
    expect('sensor.virtual_heater_power', 1500)
    set_number('heater_watts', 750)
    expect('sensor.virtual_heater_power', 750)
    set_number('lab_room_temperature', 72)
    tick()
    expect('sensor.virtual_heater_power', .5)
    service('switch', 'turn_off', 'switch.virtual_heater')
    set_number('lab_room_temperature', 68)
    before = value('input_number.lab_room_temperature')
    tick(20)
    assert value('input_number.lab_room_temperature') < before
    service('climate', 'set_hvac_mode', 'climate.virtual_room_thermostat', hvac_mode='heat')
    deadline = time.monotonic() + 210
    while value('sensor.virtual_hvac_power') != 3000 and time.monotonic() < deadline:
        time.sleep(2)
    expect('sensor.virtual_hvac_power', 3000)
    before = value('input_number.lab_room_temperature')
    tick(20)
    assert value('input_number.lab_room_temperature') > before
    service('climate', 'set_hvac_mode', 'climate.virtual_room_thermostat', hvac_mode='off')
    expect('sensor.virtual_hvac_power', 8)
    print('PASS heater thermostat/rating, native HVAC control and heat-loss/heating temperature response', flush=True)

    set_number('lab_ev_soc', 40)
    service('switch', 'turn_on', 'switch.virtual_ev_charger')
    expect('sensor.virtual_ev_charger_power', 7680)
    before = value('input_number.lab_ev_soc')
    tick(10)
    gained = value('input_number.lab_ev_soc') - before
    assert .024 < gained < .030, gained  # ~0.0256 percentage points / 10 seconds, 75 kWh, 90% efficient
    set_number('virtual_ev_current_limit', 48)
    expect('sensor.virtual_ev_charger_power', 11520)
    set_number('lab_ev_target', 100)
    set_number('lab_ev_soc', 90)
    expect('sensor.virtual_ev_charger_power', 5760)
    expect('sensor.virtual_ev_charger_status', 'Tapering')
    set_number('lab_ev_soc', 99.9999)
    tick()
    expect('input_number.lab_ev_soc', 100, tolerance=.000001)
    expect('sensor.virtual_ev_charger_power', 3)
    expect('sensor.virtual_ev_charger_status', 'Complete')
    service('input_boolean', 'turn_off', 'input_boolean.virtual_ev_plugged_in')
    expect('sensor.virtual_ev_charger_status', 'Disconnected')
    expect('sensor.virtual_ev_charger_power', 3)
    print('PASS EV current limit, energy-to-SOC conversion, taper, completion, EVSE standby', flush=True)

    set_number('lab_elapsed_seconds', 650)
    expect('sensor.virtual_refrigerator_power', 3)
    set_number('lab_elapsed_seconds', 1800)
    expect('sensor.virtual_refrigerator_power', 150)
    service('script', 'base_layer_overload')
    check_sum()
    assert 13000 < value('sensor.virtual_household_power') < 13200
    service('switch', 'turn_off', 'switch.virtual_ev_charger')
    assert value('sensor.virtual_household_power') < 11000
    check_sum()
    for name in ['heater','dryer','desk_lamp','ev_charger','refrigerator','toaster','water_heater']:
        meter = req('/api/states/sensor.virtual_' + name + '_energy')
        assert meter['attributes']['unit_of_measurement'] == 'kWh'
        assert math.isfinite(float(meter['state']))
    print('PASS refrigerator cycle, realistic overload, shutoff relief, totals and kWh meters', flush=True)
finally:
    service('script', 'base_layer_reset')
    service('automation', 'turn_on', 'automation.real_time_household_physics')
    print('Restored quiet-home scenario and real-time simulation.', flush=True)

started = value('input_number.lab_elapsed_seconds')
time.sleep(12)
assert value('input_number.lab_elapsed_seconds') > started + 5
check_sum()
print('PASS automatic real-time clock advances after reset', flush=True)
