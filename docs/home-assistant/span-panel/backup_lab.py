"""Generate the local battery-outage scenario; the emitter owns battery physics."""
import json
from pathlib import Path
import yaml

# Upper bounds of every modeled load on a circuit, including idle electronics.
# EV power is capped at 24 A in battery mode in the original meter expression.
PEAKS = dict(living_room=73, office=1516, kitchen=2553, essentials=500,
             laundry=5001, garage=5760, hvac=3008, water_heater=4502)
BUDGET = 10500
OUTAGE = 'input_boolean.lab_span_grid_outage'
READY = 'binary_sensor.lab_span_backup_ready'
RESERVED = 'sensor.lab_span_backup_reserved_power'


def admitted(key):
    return f'input_boolean.lab_span_backup_{key}'


def action(name, entity=None, **data):
    return {'action': name, **({'target': {'entity_id': entity}} if entity else {}),
            **({'data': data} if data else {})}


def install_backup(lab, relays):
    def save(path, value):
        path.write_text(yaml.safe_dump(value, sort_keys=False, allow_unicode=True))
    path = lab / 'packages/span_panel.yaml'
    package = yaml.safe_load(path.read_text())
    flags = [admitted(key) for key in PEAKS]
    priorities = {k: v.replace('switch.', 'select.').replace('_breaker', '_circuit_priority') for k,v in relays.items()}
    package['input_boolean']['lab_span_grid_outage'] = {'name': 'Grid outage · battery backup', 'icon': 'mdi:transmission-tower-off'}
    package['input_text'] = {}
    for key in PEAKS:
        package['input_boolean'][admitted(key).split('.')[1]] = {'name': f'Backup reservation {key}', 'initial': False}
        package['input_text'][f'lab_span_saved_priority_{key}'] = {'name': f'Before outage priority {key}', 'max': 40}
    rest = package['rest'][0]
    rest['sensor'] = [
        {'name': 'Lab SPAN Battery Charge', 'unique_id': 'lab_span_battery_charge', 'unit_of_measurement': '%', 'device_class': 'battery',
         'value_template': '{{ value_json.battery.soc_percent | round(3) }}', 'availability': '{{ value_json.connected and value_json.battery is not none }}'},
        {'name': 'Lab SPAN Battery Energy', 'unique_id': 'lab_span_battery_energy', 'unit_of_measurement': 'kWh', 'device_class': 'energy',
         'value_template': '{{ value_json.battery.stored_energy_kwh }}', 'availability': '{{ value_json.connected and value_json.battery is not none }}'},
        {'name': 'Lab SPAN Battery Power', 'unique_id': 'lab_span_battery_power', 'unit_of_measurement': 'W', 'device_class': 'power',
         'value_template': '{{ value_json.battery.discharge_watts | round(1) }}', 'availability': '{{ value_json.connected and value_json.battery is not none }}'},
        {'name': 'Lab SPAN Grid Power', 'unique_id': 'lab_span_grid_power', 'unit_of_measurement': 'W', 'device_class': 'power',
         'value_template': '{{ value_json.grid_watts | round(1) }}', 'availability': '{{ value_json.connected }}'}]
    rest['binary_sensor'].append({'name': 'Lab SPAN Backup Ready', 'unique_id': 'lab_span_backup_ready',
        'value_template': '{{ value_json.connected and value_json.grid_online == false and value_json.battery is not none and value_json.battery.stored_energy_kwh > 0.001 }}'})
    # Supply is gated before native relay commands finish. Unreserved raw ON commands
    # cannot power appliances; all restoration passes through the serial queue below.
    for sensor in package['template'][0]['binary_sensor']:
        key = sensor['unique_id'].removeprefix('lab_span_').removesuffix('_supply')
        if key in PEAKS:
            sensor['state'] = sensor['state'].removesuffix(' }}') + f" and (is_state('{OUTAGE}', 'off') or (is_state('{OUTAGE}', 'on') and is_state('{admitted(key)}', 'on') and is_state('{READY}', 'on') and states('{RESERVED}') | float(99999) <= {BUDGET}))" + ' }}'
    sum_expr = ' + '.join(f"({watts} if is_state('{admitted(k)}', 'on') else 0)" for k,watts in PEAKS.items())
    package['template'][1]['sensor'].extend([
        {'name': 'Lab SPAN Backup Reserved Power', 'unique_id': 'lab_span_backup_reserved_power', 'unit_of_measurement': 'W', 'device_class': 'power', 'state': '{{ ' + sum_expr + ' }}'},
        {'name': 'Lab SPAN Backup Available Power', 'unique_id': 'lab_span_backup_available_power', 'unit_of_measurement': 'W', 'device_class': 'power', 'state': '{{ [0, ' + str(BUDGET) + f" - states('{RESERVED}') | float(0)] | max" + ' }}'},
        {'name': 'Lab SPAN Effective EV Current', 'unique_id': 'lab_span_effective_ev_current', 'unit_of_measurement': 'A',
         'state': "{% set amps = states('input_number.virtual_ev_current_limit') | float(32) %}{{ [amps, 24] | min if is_state('" + OUTAGE + "', 'on') else amps }}"}])
    # Use direct helper reads for serialization; a derived sensor can lag the last action.
    package['script']['lab_span_backup_restore'] = {'alias': 'Restore circuit within battery budget', 'mode': 'queued', 'max': 20,
        'fields': {'circuit': {'required': True, 'selector': {'select': {'options': list(PEAKS)}}}},
        'sequence': [
            {'variables': {'peaks': PEAKS, 'relays': relays, 'priorities': priorities}},
            {'condition': 'template', 'value_template': '{{ circuit in peaks }}'},
            {'if': [{'condition': 'template', 'value_template': "{{ is_state('" + OUTAGE + "', 'on') }}"}],
             'then': [
                {'if': [{'condition': 'template', 'value_template': "{{ not is_state('" + READY + "', 'on') or is_state('input_boolean.lab_span_main_outage', 'on') or not is_state('input_boolean.lab_span_' ~ circuit ~ '_fault', 'off') or ((" + sum_expr + ") + (0 if is_state('input_boolean.lab_span_backup_' ~ circuit, 'on') else peaks[circuit])) > " + str(BUDGET) + ' }}'}],
                 'then': [action('persistent_notification.create', title='Battery circuit restore blocked', message="{{ circuit ~ ': insufficient reserved capacity, battery unavailable, or an active fault. Turn another circuit off and try again. Budget: 10,500 W; EV capped at 24 A.' }}", notification_id='lab_span_backup_budget'),
                          action('switch.turn_off', '{{ relays[circuit] }}'), {'stop': 'Backup capacity or supply unavailable'}]},
                action('input_boolean.turn_on', "{{ 'input_boolean.lab_span_backup_' ~ circuit }}"),
                action('select.select_option', '{{ priorities[circuit] }}', option='never') ]},
            action('switch.turn_on', '{{ relays[circuit] }}'),
            {'delay': {'seconds': 3}},
            {'if': [{'condition': 'template', 'value_template': "{{ not is_state(relays[circuit], 'on') }}"}],
             'then': [action('input_boolean.turn_off', "{{ 'input_boolean.lab_span_backup_' ~ circuit }}"),
                      action('persistent_notification.create', title='Circuit did not restore', message='The panel did not confirm closure. Try Restore on battery again.', notification_id='lab_span_backup_budget')]}]}
    for key in PEAKS:
        package['script'][f'lab_span_backup_restore_{key}'] = {'alias': f'Restore {key.replace("_", " ")} on battery',
            'sequence': [action('script.lab_span_backup_restore', circuit=key)]}
        # Turning off a circuit releases its peak allowance. Raw SPAN ON commands
        # request the same budget check as the explicit restore button.
        package['automation'].append({'id': f'lab_span_backup_native_{key}', 'alias': f'Backup budget for {key}', 'mode': 'queued',
            'triggers': [{'trigger': 'state', 'entity_id': relays[key], 'to': 'off', 'id': 'off'}, {'trigger': 'state', 'entity_id': relays[key], 'to': 'on', 'id': 'on'}],
            'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}],
            'actions': [{'choose': [
                {'conditions': "{{ trigger.id == 'off' }}", 'sequence': [action('input_boolean.turn_off', admitted(key))]},
                {'conditions': "{{ trigger.id == 'on' and not is_state('" + admitted(key) + "', 'on') }}", 'sequence': [action('script.lab_span_backup_restore', circuit=key)]}]}]})
    entry = [action('input_boolean.turn_off', flags),
             action('switch.turn_off', list(relays.values()))]
    for key in PEAKS:
        entry.extend([
            {'if': [{'condition': 'template', 'value_template': "{{ trigger.id == 'begin' }}"}], 'then': [action('input_text.set_value', f'input_text.lab_span_saved_priority_{key}', value="{{ states('" + priorities[key] + "') }}")]},
            action('select.select_option', priorities[key], option='off_grid')])
    entry.append(action('homeassistant.save_persistent_states'))
    package['automation'].append({'id': 'lab_span_backup_begin', 'alias': 'Grid outage starts with all circuits off', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': OUTAGE, 'from': 'off', 'to': 'on', 'id': 'begin'}, {'trigger': 'homeassistant', 'event': 'start', 'id': 'restart'}],
        'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}], 'actions': entry})
    package['automation'].append({'id': 'lab_span_backup_empty', 'alias': 'Empty or disconnected battery stops backup loads', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': READY, 'from': 'on', 'to': 'off'}],
        'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}],
        'actions': [action('input_boolean.turn_off', flags), action('switch.turn_off', list(relays.values()))]})
    package['automation'].append({'id': 'lab_span_backup_end', 'alias': 'Grid restored keeps circuits off until manual restore', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': OUTAGE, 'from': 'on', 'to': 'off'}],
        'actions': [action('switch.turn_off', list(relays.values())), action('input_boolean.turn_off', flags),
            *[action('select.select_option', priorities[k], option="{% set p = states('input_text.lab_span_saved_priority_" + k + "') %}{{ p if p in ['never','off_grid','soc_threshold'] else 'off_grid' }}") for k in PEAKS], action('homeassistant.save_persistent_states')]})
    # Existing restore-all is explicitly grid-only, so it cannot bypass reservations.
    package['script']['lab_span_restore_supply']['sequence'].insert(0, {'condition': 'state', 'entity_id': OUTAGE, 'state': 'off'})
    save(path, package)
    evpath = lab / 'packages/virtual_ev_charger.yaml'
    ev = yaml.safe_load(evpath.read_text())
    for group in ev['template']:
        for sensor in group.get('sensor', []):
            if sensor['name'] == 'Virtual EV Charger Power':
                sensor['state'] = sensor['state'].replace("states('input_number.virtual_ev_current_limit') | float(32)", "([states('input_number.virtual_ev_current_limit') | float(32), 24] | min if is_state('input_boolean.lab_span_grid_outage', 'on') else states('input_number.virtual_ev_current_limit') | float(32))") if 'lab_span_grid_outage' not in sensor['state'] else sensor['state']
    save(evpath, ev)
    dashpath = lab / 'dashboard.yaml'
    dashboard = yaml.safe_load(dashpath.read_text())
    view = next(v for v in dashboard['views'] if v.get('path') == 'panel')
    view['cards'].insert(0, {'type': 'markdown', 'content': '# Battery backup outage\n1. Turn on **Grid outage · battery backup**. All circuits start off.\n2. Use **Restore on battery** on the circuits you want; then start their appliances.\n3. Turn a **SPAN breaker** off to free its reserved capacity.\n4. Turn the grid outage off, wait 5 seconds, then run **Restore all circuit supplies** and **Reset realistic household**.\n\nThe simulated battery is 13.5 kWh, initially 80%, with 11 kW output. Restores reserve the circuit’s maximum modeled load within **10.5 kW**, leaving 500 W headroom. EV charging is capped at **24 A / 5.76 kW** on backup. A blocked restore appears in Notifications. The old whole-house fault cuts all power, including backup; leave it off.'})
    view['cards'].insert(1, {'type': 'entities', 'title': 'Battery backup controls', 'show_header_toggle': False, 'entities': [OUTAGE, READY,
        'sensor.lab_span_battery_charge', 'sensor.lab_span_battery_energy', 'sensor.lab_span_battery_power', 'sensor.lab_span_grid_power',
        'sensor.virtual_household_power', RESERVED, 'sensor.lab_span_backup_available_power', 'sensor.lab_span_effective_ev_current']})
    for key, relay in relays.items():
        for card in view['cards']:
            entities = card.get('entities', [])
            if any(isinstance(e, dict) and e.get('entity') == relay for e in entities):
                entities.insert(0, {'entity': f'script.lab_span_backup_restore_{key}', 'name': f'Restore on battery · reserves {PEAKS[key]:,} W'})
    save(dashpath, dashboard)
