"""Generate the panel config and install supply interlocks into the virtual lab.

Run --panel-only before pairing SPAN. Then provide --entities with the discovered
circuit relay IDs. Edits are idempotent; they never touch the physical-device APIs.
"""
import argparse
import json
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
LAB = HERE.parent / 'realistic-lab'
CIRCUITS = json.loads((HERE / 'circuits.json').read_text())


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(yaml.safe_dump(data, sort_keys=False, allow_unicode=True))


def powered(key):
    return f"is_state('binary_sensor.lab_span_{key}_supply', 'on')"


def guard(key):
    return {'condition': 'template', 'value_template': '{{ ' + powered(key) + ' }}'}


def wrap(state, key, off='0'):
    prefix = '{% if not ' + powered(key) + ' %}'
    return state if state.startswith(prefix) else prefix + off + '{% else %}' + state + '{% endif %}'


def panel_config():
    panel = {'panel_config': {'serial_number': 'energy-lab', 'total_tabs': 16,
                             'main_size': 200, 'latitude': 30.2672, 'longitude': -97.7431,
                             'wifi_ssid': 'Virtual Energy Lab'},
             'circuit_templates': {}, 'circuits': [], 'unmapped_tabs': [13,14,15,16],
             'simulation_params': {'update_interval': 1, 'time_acceleration': 1,
                                   'noise_factor': 0, 'enable_realistic_behaviors': False}}
    for key, item in CIRCUITS.items():
        panel['circuit_templates'][key] = {
            'energy_profile': {'mode': 'consumer', 'power_range': [0, item['amps'] * (240 if len(item['tabs']) == 2 else 120)],
                               'typical_power': 0, 'power_variation': 0},
            'recorder_entity': f'sensor.lab_span_{key}_power',
            'relay_behavior': 'controllable', 'priority': 'OFF_GRID', 'breaker_rating': item['amps']}
        panel['circuits'].append({'id': key, 'name': item['name'], 'template': key, 'tabs': item['tabs']})
    panel['bess'] = {'enabled': True, 'vendor': 'Span', 'product_name': 'Energy Lab simulated battery',
                     'serial_number': 'SIM-ENERGY-LAB-BESS', 'firmware_version': 'sim-bess/v0.1.0',
                     'mid_product_name': 'Energy Lab simulated MID', 'mid_firmware_version': 'sim-mid/v0.1.0',
                     'mid_hardware_version': 'virtual', 'nameplate_capacity_kwh': 25,
                     'initial_soe_kwh': 20, 'max_charge_w': 3500.0, 'max_discharge_w': 11000.0,
                     'charge_efficiency': 0.95, 'discharge_efficiency': 0.95,
                     'backup_reserve_pct': 0.0, 'charge_mode': 'backup-only'}
    save(HERE / 'energy-lab.yaml', panel)
    save(ROOT / '.local/span-panel/configs/energy-lab.yaml', panel)


def install_interlocks(relays):
    package = {'input_boolean': {'lab_span_main_outage': {'name': 'Whole-house power outage', 'icon': 'mdi:transmission-tower-off'}},
               'rest': [{'resource': 'http://basehack-panelbench:18081/health', 'scan_interval': 2,
                         'binary_sensor': [{'name': 'Lab SPAN Bridge Connected', 'unique_id': 'lab_span_bridge_connected',
                                            'device_class': 'connectivity', 'value_template': '{{ value_json.connected }}'}]}],
               'template': [{'binary_sensor': []}, {'sensor': []}], 'automation': [], 'script': {}}
    supply_sensors = package['template'][0]['binary_sensor']
    meters = package['template'][1]['sensor']
    for key, item in CIRCUITS.items():
        relay = relays[key]
        fault = f'input_boolean.lab_span_{key}_fault'
        package['input_boolean'][fault.split('.')[1]] = {'name': item['area'] + ' · ' + item['name'] + ' fault', 'icon': 'mdi:flash-alert'}
        supply_sensors.append({'name': f'Lab SPAN {key} Supply', 'unique_id': f'lab_span_{key}_supply',
                               'device_class': 'power', 'state': '{{ ' + f"is_state('{relay}', 'on') and is_state('{fault}', 'off') and is_state('input_boolean.lab_span_main_outage', 'off') and is_state('binary_sensor.lab_span_bridge_connected', 'on')" + ' }}',
                               'attributes': {'circuit': item['name'], 'devices': '{{ ' + repr(item['devices']) + ' }}', 'breaker_amps': str(item['amps'])}})
        total = ' + '.join(f"states('sensor.virtual_{meter}_power') | float(0)" for meter in item['meters'])
        meters.append({'name': f'Lab SPAN {key} Power', 'unique_id': f'lab_span_{key}_power',
                       'device_class': 'power', 'state_class': 'measurement', 'unit_of_measurement': 'W',
                       'state': '{{ (' + total + ') | round(2) }}'})
        # Cut the backing helpers too: clock and thermal scripts cannot keep working
        # behind an off UI switch. Trigger on all attempts, plus startup/reconnect.
        targets = ['input_boolean.' + helper for helper in item['helpers']]
        triggers = [{'trigger': 'state', 'entity_id': [f'binary_sensor.lab_span_{key}_supply', *targets, *item['devices']]},
                    {'trigger': 'homeassistant', 'event': 'start'},
                    {'trigger': 'time_pattern', 'seconds': '/10'}]
        actions = []
        if key == 'hvac':
            actions.append({'if': [{'condition': 'template', 'value_template': "{{ states('climate.virtual_room_thermostat') not in ['off','unknown','unavailable'] }}"}],
                            'then': [{'action': 'climate.set_hvac_mode', 'target': {'entity_id': 'climate.virtual_room_thermostat'}, 'data': {'hvac_mode': 'off'}}]})
        if targets:
            actions.append({'action': 'input_boolean.turn_off', 'target': {'entity_id': targets}})
        if actions:
            package['automation'].append({'id': f'lab_span_{key}_interlock', 'alias': f'Lab SPAN {key} power interlock', 'mode': 'restart',
                'triggers': triggers, 'conditions': [{'condition': 'template', 'value_template': '{{ not ' + powered(key) + ' }}'}], 'actions': actions})
        # Faults persist across restarts. A native breaker can also be opened
        # directly; healthy circuits are not automatically reclosed by a timer.
        faulted = f"is_state('{fault}', 'on') or is_state('input_boolean.lab_span_main_outage', 'on')"
        package['automation'].append({'id': f'lab_span_{key}_fault', 'alias': f'Lab SPAN {key} fault relay', 'mode': 'queued',
            'triggers': [{'trigger': 'state', 'entity_id': [fault, 'input_boolean.lab_span_main_outage'], 'to': ['on', 'off'], 'id': 'command'},
                         {'trigger': 'state', 'entity_id': relay, 'to': 'on', 'id': 'enforce'},
                         {'trigger': 'time_pattern', 'seconds': '/10', 'id': 'enforce'},
                         {'trigger': 'homeassistant', 'event': 'start', 'id': 'enforce'}],
            'actions': [{'delay': {'seconds': 4}}, {'choose': [{'conditions': '{{ (' + faulted + f") and is_state('{relay}', 'on')" + ' }}',
                                      'sequence': [{'action': 'switch.turn_off', 'target': {'entity_id': relay}}]},
                                    {'conditions': '{{ not (' + faulted + f") and trigger.id == 'command' and is_state('{relay}', 'off')" + ' }}',
                                      'sequence': [{'action': 'switch.turn_on', 'target': {'entity_id': relay}}]}]}]})
    meters.append({'name': 'Virtual Electronics Power', 'unique_id': 'lab_electronics_power', 'device_class': 'power',
                   'state_class': 'measurement', 'unit_of_measurement': 'W',
                   'state': wrap("{{ states('input_number.base_layer_background_watts') | float(180) }}", 'essentials')})
    package['automation'].append({'id': 'lab_span_persist_faults', 'alias': 'Lab SPAN persist fault changes', 'mode': 'restart',
        'triggers': [{'trigger': 'state', 'entity_id': ['input_boolean.lab_span_main_outage'] + [f'input_boolean.lab_span_{key}_fault' for key in CIRCUITS], 'to': ['on','off']}],
        'conditions': [{'condition': 'template', 'value_template': '{{ trigger.from_state is not none }}'}],
        'actions': [{'action': 'homeassistant.save_persistent_states'}]})
    package['script']['lab_span_restore_supply'] = {'alias': 'Restore all circuit supplies (loads stay off)', 'sequence': [
        {'action': 'input_boolean.turn_off', 'target': {'entity_id': ['input_boolean.lab_span_main_outage'] + [f'input_boolean.lab_span_{key}_fault' for key in CIRCUITS]}},
        {'delay': {'seconds': 5}},
        *[{'if': [{'condition': 'state', 'entity_id': relay, 'state': 'off'}],
           'then': [{'action': 'switch.turn_on', 'target': {'entity_id': relay}}]} for relay in relays.values()]]}
    save(LAB / 'packages/span_panel.yaml', package)

    device_keys = {entity.split('.')[1].replace('virtual_', ''): key for key, item in CIRCUITS.items() for entity in item['devices']}
    meter_keys = {meter: key for key,item in CIRCUITS.items() for meter in item['meters']}
    for path in (LAB/'packages').glob('*.yaml'):
        if path.name == 'span_panel.yaml':
            continue
        data = yaml.safe_load(path.read_text())
        for group in data.get('template', []):
            for kind in ('switch', 'light', 'fan'):
                for entity in group.get(kind, []):
                    name = entity['name'].lower().replace(' ', '_').replace('virtual_', '')
                    key = device_keys.get(name)
                    if not key:
                        continue
                    entity['state'] = wrap(entity['state'], key, 'false')
                    for action in ('turn_on', 'set_level', 'set_percentage'):
                        if action in entity and (not entity[action] or entity[action][0] != guard(key)):
                            entity[action].insert(0, guard(key))
            for entity in group.get('sensor', []):
                name = entity['name'].lower().replace(' ', '_').removeprefix('virtual_')
                if name.endswith('_power') and name[:-6] in meter_keys:
                    entity['state'] = wrap(entity['state'], meter_keys[name[:-6]])
                if name == 'background_power':
                    entity['state'] = "{{ states('sensor.virtual_electronics_power') | float(0) + states('sensor.virtual_refrigerator_power') | float(0) }}"
                if name in ('dryer_phase', 'ev_charger_status', 'toaster_status', 'water_heater_status'):
                    key = {'dryer_phase': 'laundry', 'ev_charger_status': 'garage', 'toaster_status': 'kitchen', 'water_heater_status': 'water_heater'}[name]
                    entity['state'] = wrap(entity['state'], key, 'No circuit power')
        # Change the template values before YAML wraps/escapes long strings.
        def thermal(value):
            if isinstance(value, dict):
                return {k: thermal(v) for k,v in value.items()}
            if isinstance(value, list):
                return [thermal(v) for v in value]
            if isinstance(value, str):
                import re
                value = re.sub(r"9000 if is_state\('input_boolean.lab_hvac_on',\s*'on'\) else 0", "9000 if states('sensor.virtual_hvac_power') | float(0) >= 3000 else 0", value)
                value = re.sub(r"4500 if is_state\('input_boolean.lab_water_heater_enabled',\s*'on'\) and is_state\('input_boolean.lab_water_heater_demand',\s*'on'\) else 0", "4500 if states('sensor.virtual_water_heater_power') | float(0) >= 4500 else 0", value)
            return value
        save(path, thermal(data))

    dashboard = yaml.safe_load((LAB/'dashboard.yaml').read_text())
    dashboard['views'] = [view for view in dashboard['views'] if view.get('path') != 'panel']
    cards = [{'type': 'markdown', 'content': '# Virtual SPAN smart panel\nTurn on an **area fault** to cut power to that circuit. All assigned devices turn off and draw zero watts. Clear the fault to restore supply, then restart appliances manually. **Breaker** controls operate the real SPAN integration connected to PanelBench.\n\nFault toggles persist across restarts. This is a virtual fault injection, not a simulation of protective breaker trip curves.'},
             {'type': 'entities', 'title': 'Panel connection and whole house', 'show_header_toggle': False,
              'entities': ['binary_sensor.lab_span_bridge_connected', 'sensor.virtual_household_power', {'entity': 'sensor.span_panel_current_power', 'name': 'SPAN metered total'}, 'input_boolean.lab_span_main_outage', 'script.lab_span_restore_supply']},
             {'type': 'button', 'name': 'Open PanelBench', 'icon': 'mdi:electric-switch', 'tap_action': {'action': 'url', 'url_path': 'http://localhost:18080'}}]
    for key,item in CIRCUITS.items():
        cards.append({'type': 'entities', 'title': f"{item['name']} · {item['amps']} A", 'show_header_toggle': False,
                      'entities': [{'entity': f'input_boolean.lab_span_{key}_fault', 'name': 'Area fault · on cuts power'}, {'entity': relays[key], 'name': 'SPAN breaker'},
                                   {'entity': f'binary_sensor.lab_span_{key}_supply', 'name': 'Circuit power'},
                                   {'entity': f'sensor.lab_span_{key}_power', 'name': 'Circuit load'}, *item['devices']]})
    dashboard['views'].append({'title': 'Smart Panel', 'path': 'panel', 'icon': 'mdi:electric-switch', 'cards': cards})
    save(LAB/'dashboard.yaml', dashboard)
    from backup_lab import install_backup
    install_backup(LAB, relays)
    print('Generated circuit interlocks and Smart Panel dashboard')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--panel-only', action='store_true')
    parser.add_argument('--entities', type=Path)
    args = parser.parse_args()
    panel_config()
    if not args.panel_only:
        if not args.entities:
            parser.error('--entities is required to bind the installed SPAN relay IDs')
        install_interlocks(json.loads(args.entities.read_text()))
