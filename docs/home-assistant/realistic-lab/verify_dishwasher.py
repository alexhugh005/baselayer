"""Verify the virtual dishwasher; restore the clock/circuit and leave it ready.

Uses the existing local lab token without displaying credentials.
"""
import sys
import time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'span-panel'))
from lab_client import request, service

def state(entity): return request('/api/states/' + entity)['state']
def act(domain, action, entity=None, **data):
    # SPAN rejects relay operations within its two-second debounce window.
    if entity == 'switch.span_panel_kitchen_outlets_breaker': time.sleep(2.1)
    service(domain, action, entity, **data)
    time.sleep(.4)
def expect(entity, expected):
    for _ in range(30):
        actual = state(entity)
        if (abs(float(actual) - expected) < .02 if isinstance(expected, (int, float)) else actual == expected): return
        time.sleep(.2)
    raise AssertionError((entity, actual, expected))
def elapsed(value): act('input_number', 'set_value', 'input_number.lab_dishwasher_elapsed', value=value)
def sums():
    all_states = {s['entity_id']: s['state'] for s in request('/api/states')}
    names = ['heater','dryer','desk_lamp','living_room_light','ceiling_fan','hvac','ev_charger','background','toaster','water_heater','dishwasher']
    assert abs(sum(float(all_states['sensor.virtual_'+n+'_power']) for n in names) - float(all_states['sensor.virtual_household_power'])) < .02
    expect('sensor.lab_span_kitchen_power', sum(float(all_states['sensor.virtual_'+n+'_power']) for n in ['refrigerator','toaster','dishwasher']))

clock = state('input_boolean.lab_simulation_running')
relay = 'switch.span_panel_kitchen_outlets_breaker'
assert state('binary_sensor.lab_span_kitchen_supply') == 'on', 'Kitchen supply must be available'
helpers = ['input_boolean.lab_refrigerator_enabled','input_boolean.lab_toaster_enabled']
saved = {h:state(h) for h in helpers}
try:
    act('input_boolean','turn_off','input_boolean.lab_simulation_running')
    act('script','lab_dishwasher_reset')
    expect('sensor.virtual_dishwasher_phase','Ready')
    expect('sensor.virtual_dishwasher_power',1)
    act('switch','turn_on','switch.virtual_dishwasher')
    for seconds, phase, watts in [(0,'Pre-rinse',30),(600,'Heating wash',1200),(780,'Washing',100),(1200,'Heating wash',1200),(3600,'Rinsing',100),(4200,'Drying',600)]:
        elapsed(seconds)
        expect('sensor.virtual_dishwasher_phase',phase)
        expect('sensor.virtual_dishwasher_power',watts)
        sums()
    act('switch','turn_off','switch.virtual_dishwasher')
    act('script','lab_dishwasher_tick',seconds=10)
    expect('input_number.lab_dishwasher_elapsed',4200)
    expect('sensor.virtual_dishwasher_phase','Paused')
    act('switch','turn_on','switch.virtual_dishwasher')
    act('script','lab_dishwasher_tick',seconds=10)
    expect('input_number.lab_dishwasher_elapsed',4210)
    elapsed(5395)
    act('script','lab_dishwasher_tick',seconds=10)
    expect('switch.virtual_dishwasher','off')
    expect('sensor.virtual_dishwasher_phase','Complete')
    expect('sensor.virtual_dishwasher_remaining',0)
    act('switch','turn_on','switch.virtual_dishwasher')
    expect('input_number.lab_dishwasher_elapsed',0)
    elapsed(650)
    act('switch','turn_off',relay)
    expect('input_boolean.lab_dishwasher_enabled','off')
    expect('sensor.virtual_dishwasher_power',0)
    expect('sensor.virtual_dishwasher_phase','No circuit power')
    act('switch','turn_on','switch.virtual_dishwasher')
    act('script','lab_dishwasher_tick',seconds=10)
    expect('input_number.lab_dishwasher_elapsed',650)
    expect('switch.virtual_dishwasher','off')
    act('switch','turn_on',relay)
    expect('binary_sensor.lab_span_kitchen_supply','on')
    expect('switch.virtual_dishwasher','off')
    expect('sensor.virtual_dishwasher_phase','Paused')
    assert request('/api/states/sensor.virtual_dishwasher_energy')['attributes']['unit_of_measurement']=='kWh'
    print('PASS: cycle phases, watts, household/circuit totals, pause/resume, completion, restart, circuit interruption, kWh meter')
finally:
    if state(relay) != 'on': act('switch','turn_on',relay)
    for helper, value in saved.items(): act('input_boolean','turn_'+value,helper)
    act('script','lab_dishwasher_reset')
    act('input_boolean','turn_'+clock,'input_boolean.lab_simulation_running')
