"""Provision only the virtual panel shipped on this Compose project's network."""
import asyncio
from datetime import timedelta
import json
import logging
from pathlib import Path

import voluptuous as vol
from homeassistant.helpers import area_registry as ar, entity_registry as er
from homeassistant.helpers.start import async_at_started
from homeassistant.helpers.storage import Store

DOMAIN = 'energy_lab_bootstrap'
CONFIG_SCHEMA = vol.Schema({DOMAIN: vol.Schema({})}, extra=vol.ALLOW_EXTRA)
LOGGER = logging.getLogger(__name__)
HOST = 'basehack-panelbench'
HERE = Path(__file__).parent


def write_token(token):
    path = Path('/run/lab-credentials/ha-token')
    temporary = path.with_suffix('.tmp')
    temporary.write_text(token)
    temporary.chmod(0o600)
    temporary.replace(path)


async def provision_token(hass):
    store = Store(hass, 1, DOMAIN)
    saved = await store.async_load() or {}
    user = await hass.auth.async_get_user(saved.get('user_id', ''))
    if user is None:
        user = await hass.auth.async_create_system_user(
            'Energy Lab meter bridge', group_ids=['system-read-only'], local_only=True)
        await store.async_save({'user_id': user.id})
    refresh = next(iter(user.refresh_tokens.values()), None)
    if refresh is None:
        refresh = await hass.auth.async_create_refresh_token(
            user, access_token_expiration=timedelta(days=3650))
    token = hass.auth.async_create_access_token(refresh)
    await hass.async_add_executor_job(write_token, token)


async def pair_panel(hass):
    entries = hass.config_entries.async_entries('span_panel')
    entry = next((e for e in entries if e.unique_id == 'sim-energy-lab'), None)
    if entry is None:
        result = await hass.config_entries.flow.async_init(
            'span_panel', context={'source': 'user'},
            data={'host': HOST, 'http_port': 8081})
        inputs = {
            'panel_https_port': {'https_port': 9081},
            'choose_v2_auth': {'next_step_id': 'auth_passphrase'},
            'auth_passphrase': {'hop_passphrase': 'sim-passphrase'},
            'choose_entity_naming_initial': {'entity_naming_pattern': 'friendly_names'},
        }
        for _ in range(8):
            if result['type'] == 'create_entry':
                entry = result['result']
                break
            step = result.get('step_id')
            if result.get('errors') or step not in inputs:
                if flow_id := result.get('flow_id'):
                    hass.config_entries.flow.async_abort(flow_id)
                # Log no flow data: successful flow data contains credentials.
                raise RuntimeError(f'Panel pairing waiting at {step}: {result.get("errors", result.get("reason", "unexpected step"))}')
            result = await hass.config_entries.flow.async_configure(result['flow_id'], inputs[step])
        if entry is None:
            raise RuntimeError('Panel pairing did not finish')
    # Use Docker DNS instead of the ephemeral IP returned by simulator pairing.
    # Its certificate includes the fixed container hostname; TLS stays verified.
    data = {**entry.data, 'host': HOST, 'ebus_broker_host': HOST}
    if data != entry.data:
        hass.config_entries.async_update_entry(entry, data=data)
        await hass.config_entries.async_reload(entry.entry_id)
    return entry


async def configure_areas(hass, circuits, relays):
    areas = ar.async_get(hass)
    registry = er.async_get(hass)
    for key, item in circuits.items():
        area = areas.async_get_area_by_name(item['area']) or areas.async_create(item['area'])
        entities = [relays[key], *item['devices'],
                    *['sensor.virtual_' + m + '_power' for m in item['meters']],
                    f'binary_sensor.lab_span_{key}_supply', f'sensor.lab_span_{key}_power',
                    f'input_boolean.lab_span_{key}_fault']
        for entity in entities:
            if registry.async_get(entity):
                registry.async_update_entity(entity, area_id=area.id)


async def initialize(hass):
    circuits = await hass.async_add_executor_job(lambda: json.loads((HERE / 'circuits.json').read_text()))
    relays = await hass.async_add_executor_job(lambda: json.loads((HERE / 'relays.json').read_text()))
    initialized = Path(hass.config.path('.energy-lab-initialized'))
    ready = Path(hass.config.path('.energy-lab-ready'))
    token_ready = False
    # Startup and image downloads can take time. Retry without blocking HA login.
    attempt = 0
    while True:
        try:
            if not token_ready:
                await provision_token(hass)
                token_ready = True
            await pair_panel(hass)
            if not hass.states.is_state('binary_sensor.lab_span_bridge_connected', 'on'):
                raise RuntimeError('Waiting for live meter bridge')
            if not all(hass.states.get(e) and hass.states.get(e).state in ('on', 'off') for e in relays.values()):
                raise RuntimeError('Waiting for all eight SPAN breaker entities')
            if not await hass.async_add_executor_job(initialized.exists):
                await hass.services.async_call('input_boolean', 'turn_off',
                    {'entity_id': 'input_boolean.lab_span_grid_outage'}, blocking=True)
                await hass.services.async_call('script', 'lab_span_restore_supply', blocking=True)
                for _ in range(30):
                    if all(hass.states.is_state(f'binary_sensor.lab_span_{key}_supply', 'on') for key in circuits):
                        break
                    await asyncio.sleep(2)
                else:
                    raise RuntimeError('Waiting for all eight circuit supplies')
                await hass.services.async_call('script', 'base_layer_reset', blocking=True)
                await configure_areas(hass, circuits, relays)
                await hass.async_add_executor_job(initialized.write_text, '1\n')
            await hass.async_add_executor_job(ready.write_text, 'ready\n')
            LOGGER.info('Energy Lab ready: panel paired and live meters connected')
            return
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            attempt += 1
            if attempt == 1 or attempt % 6 == 0:
                LOGGER.warning('Energy Lab initialization will retry (%s): %s', type(exc).__name__, exc)
            await asyncio.sleep(10)


async def async_setup(hass, config):
    async def started(_hass):
        hass.async_create_background_task(initialize(hass), 'energy_lab_initialize')
    async_at_started(hass, started)
    return True
