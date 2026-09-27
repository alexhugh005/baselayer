"""Generate the local battery-outage scenario; the emitter owns battery physics."""
import json
from pathlib import Path
import yaml

# Upper bounds of every modeled load on a circuit, including idle electronics.
# EV power is capped at 24 A in battery mode in the original meter expression.
PEAKS = dict(living_room=73, office=1516, kitchen=2553, essentials=500,
             laundry=5001, garage=5760, hvac=3008, water_heater=4502)
OUTAGE = 'input_boolean.lab_span_grid_outage'
READY = 'binary_sensor.lab_span_backup_ready'
# Keep this entity ID for existing dashboards; its value is now measured load.
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
    # Publish the model's known circuit peaks for API outage admission. Live watts
    # can be only standby demand and would underestimate the next appliance cycle.
    customize = package.setdefault('homeassistant', {}).setdefault('customize', {})
    circuit_model = json.loads((Path(__file__).parent / 'circuits.json').read_text())
    for key, relay in relays.items():
        meter = relay.replace('switch.', 'sensor.').removesuffix('_breaker') + '_power'
        customize.setdefault(meter, {})['restoration_estimate_watts'] = PEAKS[key]
        customize[meter]['restoration_devices'] = circuit_model[key]['devices']
        customize[meter]['circuit_supply_entity'] = f'binary_sensor.lab_span_{key}_supply'
    flags = [admitted(key) for key in PEAKS]
    package['input_boolean']['lab_span_grid_outage'] = {'name': 'Grid outage · battery backup', 'icon': 'mdi:transmission-tower-off'}
    for key in PEAKS:
        package['input_boolean'][admitted(key).split('.')[1]] = {'name': f'Backup reservation {key}', 'initial': False}
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
        'value_template': "{{ value_json.connected and value_json.grid_online == false and value_json.battery is not none and value_json.battery.stored_energy_kwh > 0.001 and state_attr('automation.grid_outage_starts_with_all_circuits_off', 'current') == 0 and state_attr('automation.empty_or_disconnected_battery_stops_backup_loads', 'current') == 0 }}"})
    # Supply is gated before native relay commands finish. Unadmitted raw ON commands
    # cannot power appliances; all restoration passes through the serial queue below.
    for sensor in package['template'][0]['binary_sensor']:
        key = sensor['unique_id'].removeprefix('lab_span_').removesuffix('_supply')
        if key in PEAKS:
            sensor['state'] = sensor['state'].removesuffix(' }}') + f" and (is_state('{OUTAGE}', 'off') or (is_state('{OUTAGE}', 'on') and is_state('{admitted(key)}', 'on') and is_state('{READY}', 'on')))" + ' }}'
    measured_expr = "states('sensor.virtual_household_power') | float(99999)"
    package['template'][1]['sensor'].extend([
        {'name': 'Lab SPAN Backup Reserved Power', 'unique_id': 'lab_span_backup_reserved_power', 'unit_of_measurement': 'W', 'device_class': 'power', 'state': '{{ ' + measured_expr + ' }}', 'availability': "{{ is_number(states('sensor.virtual_household_power')) }}"},
        {'name': 'Lab SPAN Effective EV Current', 'unique_id': 'lab_span_effective_ev_current', 'unit_of_measurement': 'A',
         'state': "{% set amps = states('input_number.virtual_ev_current_limit') | float(32) %}{{ [amps, 24] | min if is_state('" + OUTAGE + "', 'on') else amps }}"}])
    for automation in package['automation']:
        if automation['id'].endswith('_fault'):
            branch = automation['actions'][1]['choose'][1]
            branch['conditions'] = branch['conditions'].removesuffix(' }}') + " and is_state('input_boolean.lab_span_grid_outage', 'off') }}"
    # Serialize relay restoration and confirm supply; Home Assistant applies no watt budget.
    package['script']['lab_span_backup_restore'] = {'alias': 'Restore circuit on battery', 'mode': 'queued', 'max': 20,
        'fields': {'circuit': {'required': True, 'selector': {'select': {'options': list(PEAKS)}}}},
        'sequence': [
            {'variables': {'relays': relays}},
            {'condition': 'template', 'value_template': '{{ circuit in relays }}'},
            {'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'},
            {'if': [{'condition': 'template', 'value_template': "{{ is_state('" + OUTAGE + "', 'on') }}"}],
             'then': [
                {'if': [{'condition': 'template', 'value_template': "{{ not is_state('" + READY + "', 'on') or is_state('input_boolean.lab_span_main_outage', 'on') or not is_state('input_boolean.lab_span_' ~ circuit ~ '_fault', 'off') }}"}],
                 'then': [action('persistent_notification.create', title='Battery circuit restore blocked', message="{{ circuit ~ ': battery unavailable or an active supply fault. Restore supply and try again.' }}", notification_id='lab_span_backup_budget'),
                          {'delay': {'seconds': 2.5}}, action('switch.turn_off', '{{ relays[circuit] }}'), {'stop': 'Backup supply unavailable'}]},
                action('input_boolean.turn_on', "{{ 'input_boolean.lab_span_backup_' ~ circuit }}") ]},
            {'delay': {'seconds': 2.5}},
            {'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'},
            {'if': [{'condition': 'template', 'value_template': "{{ not is_state(relays[circuit], 'on') }}"}],
             'then': [action('switch.turn_on', '{{ relays[circuit] }}')]},
            {'delay': {'seconds': 3}},
            {'if': [{'condition': 'template', 'value_template': "{{ not is_state(relays[circuit], 'on') }}"}],
             'then': [action('input_boolean.turn_off', "{{ 'input_boolean.lab_span_backup_' ~ circuit }}"),
                      action('persistent_notification.create', title='Circuit did not restore', message='The panel did not confirm closure. Try Restore on battery again.', notification_id='lab_span_backup_budget'), {'stop': 'Panel did not confirm closure'}]},
            # Demo only: real installations need longer startup and meter-settling delays.
            {'delay': {'seconds': 5}}]}
    for key in PEAKS:
        package['script'][f'lab_span_backup_restore_{key}'] = {'alias': f'Restore {key.replace("_", " ")} on battery',
            'sequence': [action('script.lab_span_backup_restore', circuit=key)]}
        # Turning off a circuit releases its admission flag. Raw SPAN ON commands
        # request the same supply check as the explicit restore button.
        package['automation'].append({'id': f'lab_span_backup_native_{key}', 'alias': f'Backup supply for {key}', 'mode': 'queued',
            'triggers': [{'trigger': 'state', 'entity_id': relays[key], 'to': 'off', 'id': 'off'}, {'trigger': 'state', 'entity_id': relays[key], 'to': 'on', 'id': 'on'}],
            'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}],
            'actions': [{'choose': [
                {'conditions': "{{ trigger.id == 'off' }}", 'sequence': [action('input_boolean.turn_off', admitted(key))]},
                {'conditions': "{{ trigger.id == 'on' and not is_state('" + admitted(key) + "', 'on') }}", 'sequence': [action('script.lab_span_backup_restore', circuit=key)]}]}]})
    entry = [action('input_boolean.turn_off', flags), {'delay': {'seconds': 2.5}},
             action('switch.turn_off', list(relays.values()))]
    # Circuit priorities are user preferences. Outage start, rollout, and grid
    # return operate relays only; never rewrite "When grid goes down" settings.
    entry.append(action('homeassistant.save_persistent_states'))
    package['automation'].append({'id': 'lab_span_backup_begin', 'alias': 'Grid outage starts with all circuits off', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': OUTAGE, 'from': 'off', 'to': 'on', 'id': 'begin'}, {'trigger': 'homeassistant', 'event': 'start', 'id': 'restart'}],
        'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}], 'actions': entry})
    package['automation'].append({'id': 'lab_span_backup_empty', 'alias': 'Empty or disconnected battery stops backup loads', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': READY, 'from': 'on', 'to': 'off'}],
        'conditions': [{'condition': 'state', 'entity_id': OUTAGE, 'state': 'on'}],
        'actions': [action('input_boolean.turn_off', flags), {'delay': {'seconds': 2.5}}, action('switch.turn_off', list(relays.values()))]})
    package['automation'].append({'id': 'lab_span_backup_end', 'alias': 'Grid restored preserves circuit states', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': OUTAGE, 'from': 'on', 'to': 'off'}],
        # Grid return removes backup admission only; leave relay positions alone.
        'actions': [action('input_boolean.turn_off', flags), action('homeassistant.save_persistent_states')]})
    # Existing restore-all is explicitly grid-only, so it cannot bypass battery admission.
    package['script']['lab_span_restore_supply']['sequence'].insert(0, {'condition': 'state', 'entity_id': OUTAGE, 'state': 'off'})
    # Serialize native commands and retry the integration's relay debounce. Fault
    # recovery, grid transitions, and manual restores can otherwise race each other.
    def queue_relays(value):
        if isinstance(value, list):
            return [queue_relays(v) for v in value]
        if isinstance(value, dict):
            if value.get('action') in ('switch.turn_on', 'switch.turn_off'):
                return action('script.lab_span_command_relays', entities=value['target']['entity_id'],
                              target_state='on' if value['action'].endswith('turn_on') else 'off')
            return {k: queue_relays(v) for k,v in value.items()}
        return value
    package = queue_relays(package)
    package['script']['lab_span_command_relays'] = {'alias': 'Confirm virtual SPAN relay commands', 'mode': 'queued', 'max': 50,
        'sequence': [
            {'repeat': {'count': 3, 'sequence': [
                {'variables': {'pending': "{{ expand(entities) | rejectattr('state', 'eq', target_state) | map(attribute='entity_id') | list }}"}},
                {'if': [{'condition': 'template', 'value_template': '{{ pending | length > 0 }}'}], 'then': [
                    {'delay': {'seconds': 2.5}},
                    {'action': "{{ 'switch.turn_' ~ target_state }}", 'target': {'entity_id': '{{ pending }}'}, 'continue_on_error': True},
                    {'delay': {'seconds': 1}}]}]}},
            {'if': [{'condition': 'template', 'value_template': "{{ expand(entities) | rejectattr('state', 'eq', target_state) | list | length > 0 }}"}],
             'then': [action('persistent_notification.create', title='Panel command not confirmed', message='A virtual circuit did not reach its requested state. Check the panel connection and retry.', notification_id='lab_span_relay_error')]}]}
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
    view['cards'].insert(0, {'type': 'markdown', 'content': '# Battery backup outage\n1. Turn on **Grid outage · battery backup**. All circuits start off. Wait for **Backup ready: On**.\n2. Click **Run** beside **Restore** on the circuits you want; then start their appliances.\n3. After each restore, wait five seconds for relay confirmation.\n4. Turn the grid outage off and wait for grid recovery. Existing breaker positions are kept. Use **Restore all circuit supplies** if you want every breaker on, and **Reset realistic household** to reset appliances.\n\nThe simulated battery is 13.5 kWh, initially 80%, with 11 kW output. Home Assistant does not reject circuit restores or disconnect circuits based on a watt limit. Base Layer manages its own power budget. EV charging is capped at **24 A / 5.76 kW** on backup. A blocked restore appears in Notifications. The old whole-house fault cuts all power, including backup; leave it off.'})
    view['cards'].insert(1, {'type': 'entities', 'title': 'Battery backup controls', 'show_header_toggle': False, 'entities': [OUTAGE,
        *[{'entity': entity, 'name': name} for entity, name in [
            (READY, 'Backup ready'), ('sensor.lab_span_battery_charge', 'Battery charge'),
            ('sensor.lab_span_battery_energy', 'Stored battery energy'), ('sensor.lab_span_battery_power', 'Battery output'),
            ('sensor.lab_span_grid_power', 'Grid import'), ('sensor.virtual_household_power', 'Household load'),
            (RESERVED, 'Measured backup load'),
            ('sensor.lab_span_effective_ev_current', 'Effective EV charging current')]]]})
    for card in view['cards']:
        for entity in card.get('entities', []):
            if isinstance(entity, dict) and entity.get('entity') == 'sensor.span_panel_current_power':
                entity.update(entity='sensor.span_panel_site_power', name='SPAN household load')
    for key, relay in relays.items():
        for card in view['cards']:
            entities = card.get('entities', [])
            if any(isinstance(e, dict) and e.get('entity') == relay for e in entities):
                entities.insert(0, {'entity': f'script.lab_span_backup_restore_{key}', 'name': f'Restore · {PEAKS[key]:,} W'})
    view['cards'][0], view['cards'][1] = view['cards'][1], view['cards'][0]
    save(dashpath, dashboard)
