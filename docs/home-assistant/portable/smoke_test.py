"""Acceptance test for a disposable portable lab; creates its first owner.

Run via stdin inside the test HA container (never a personal installation):
  docker exec -i TEST_HA_CONTAINER python3 - fresh < smoke_test.py
  # Recreate both containers, keeping their volumes, then:
  docker exec -i TEST_HA_CONTAINER python3 - restarted < smoke_test.py
"""
import json
from pathlib import Path
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:8123'
CONFIG = Path('/config')
TEST = CONFIG / '.energy-lab-smoke-test.json'
assert (CONFIG / '.energy-lab-seeded').exists(), 'Not a portable lab'
assert (CONFIG / '.energy-lab-ready').exists(), 'Lab bootstrap is not ready'
phase = sys.argv[1]


def request(path, data=None, token=None, form=False):
    body = None if data is None else (urllib.parse.urlencode(data).encode() if form else json.dumps(data).encode())
    headers = {'Content-Type': 'application/x-www-form-urlencoded' if form else 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    with urllib.request.urlopen(urllib.request.Request(BASE + path, data=body, headers=headers), timeout=30) as response:
        return json.load(response)


def wait_state(entity, expected, timeout=40):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = request('/api/states/' + entity, token=token)['state']
        if value == expected:
            return
        time.sleep(1)
    raise AssertionError(f'{entity}: expected {expected}, got {value}')


def service(domain, action, entity=None, **kwargs):
    return request(f'/api/services/{domain}/{action}',
                   {**({'entity_id': entity} if entity else {}), **kwargs}, token)


if phase == 'fresh':
    assert not TEST.exists(), 'Fresh test already ran'
    onboarding = request('/api/onboarding')
    assert any(step['step'] == 'user' and not step['done'] for step in onboarding)
    bridge_token = Path('/run/lab-credentials/ha-token').read_text()
    assert request('/api/states', token=bridge_token)
    try:
        request('/api/services/switch/turn_off', {'entity_id': 'switch.virtual_desk_lamp'}, bridge_token)
    except urllib.error.HTTPError as exc:
        assert exc.code in (401, 403), exc.code
    else:
        raise AssertionError('Meter bridge token must not control devices')
    code = request('/api/onboarding/users', {
        'name': 'Portable Lab Test Owner', 'username': 'portable_lab_test',
        'password': secrets.token_urlsafe(32), 'client_id': BASE + '/', 'language': 'en',
    })['auth_code']
    token = request('/auth/token', {'grant_type': 'authorization_code', 'code': code,
                                   'client_id': BASE + '/'}, form=True)['access_token']
    assert request('/api/config', token=token)['location_name'] == 'Energy Lab'
    service('input_number', 'set_value', 'input_number.lab_ev_soc', value=67)
    TEST.write_text(json.dumps({'token': token, 'initialized_mtime': (CONFIG / '.energy-lab-initialized').stat().st_mtime}))
    TEST.chmod(0o600)
    print('PASS: fresh onboarding, private read-only meter credential, owner login')
elif phase in ('prepare-restart', 'restarted'):
    saved = json.loads(TEST.read_text())
    token = saved['token']
    if phase == 'prepare-restart':
        service('input_number', 'set_value', 'input_number.lab_ev_soc', value=67)
        print('PASS: saved EV battery progress for recreation test')
        sys.exit(0)
    assert (CONFIG / '.energy-lab-initialized').stat().st_mtime == saved['initialized_mtime']
    assert float(request('/api/states/input_number.lab_ev_soc', token=token)['state']) == 67
    print('PASS: account, simulation progress and one-time initialization survive container recreation')
else:
    raise AssertionError('Use fresh or restarted')

entries = json.loads((CONFIG / '.storage/core.config_entries').read_text())['data']['entries']
panels = [e for e in entries if e['domain'] == 'span_panel']
assert len(panels) == 1
assert panels[0]['data']['host'] == 'basehack-panelbench'
assert panels[0]['data']['ebus_broker_host'] == 'basehack-panelbench'
assert panels[0]['data'].get('panel_ca_pem'), 'TLS anchor must remain enabled'
wait_state('binary_sensor.lab_span_bridge_connected', 'on')
for circuit in ('living_room', 'office', 'kitchen', 'essentials', 'laundry', 'garage', 'hvac', 'water_heater'):
    wait_state(f'binary_sensor.lab_span_{circuit}_supply', 'on')
service('switch', 'turn_on', 'switch.virtual_desk_lamp')
wait_state('switch.virtual_desk_lamp', 'on')
time.sleep(12)  # Cross the interlock's ten-second periodic check.
wait_state('switch.virtual_desk_lamp', 'on')
service('switch', 'turn_off', 'switch.span_panel_office_outlets_breaker')
wait_state('binary_sensor.lab_span_office_supply', 'off')
wait_state('switch.virtual_desk_lamp', 'off')
service('switch', 'turn_on', 'switch.virtual_desk_lamp')
wait_state('switch.virtual_desk_lamp', 'off')
time.sleep(3)  # Respect native SPAN relay debounce.
service('switch', 'turn_on', 'switch.span_panel_office_outlets_breaker')
wait_state('binary_sensor.lab_span_office_supply', 'on')
service('switch', 'turn_on', 'switch.virtual_desk_lamp')
wait_state('switch.virtual_desk_lamp', 'on')
states = {s['entity_id']: s['state'] for s in request('/api/states', token=token)}
assert float(states['sensor.virtual_household_power']) > 0
users = json.loads((CONFIG / '.storage/auth').read_text())['data']['users']
owners = [u for u in users if u['is_owner']]
assert len(owners) == 1 and owners[0]['name'] == 'Portable Lab Test Owner'
print('PASS: eight circuits, stable device state, breaker shutoff/interlock and manual restart')
