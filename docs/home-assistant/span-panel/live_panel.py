"""PanelBench 2.5.3 adapter: drive its measurement input from live lab meters.

Uses the simulator's injectable RecorderDataSource interface, without changing
its electrical calculations or MQTT emitter. Historical modeling is deliberately
unavailable for this live source. Keep the upstream revision pinned on upgrades.
"""
import asyncio
import contextlib
import json
import logging
import math
import os
import time
from pathlib import Path

import aiohttp
from aiohttp import web
from panelbench.app import SimulatorApp
from panelbench.recorder import RecorderDataSource
from panelbench.__main__ import _configure_logging, _run_until_signalled
from mqtt_receiver import install as install_mqtt_receiver


class LivePowerSource(RecorderDataSource):
    def __init__(self):
        super().__init__()
        self.values = {}
        self.updated = 0.0
        self.error = 'Waiting for Home Assistant'
        self.panel_status = lambda: {}
        self.grid_online = True
        self.set_grid_online = lambda value: None

    def get_power(self, entity_id, timestamp):
        # Never silently fall back to a second synthetic appliance model.
        return self.values.get(entity_id, 0.0) if self.connected else 0.0

    @property
    def connected(self):
        return time.monotonic() - self.updated < 5 and not self.error

    async def poll(self):
        names = json.loads(Path('/lab/circuits.json').read_text())
        entities = ['sensor.lab_span_' + key + '_power' for key in names]
        token = Path('/run/secrets/ha-token').read_text().strip()
        async with aiohttp.ClientSession(
            headers={'Authorization': 'Bearer ' + token},
            timeout=aiohttp.ClientTimeout(total=3),
        ) as session:
            while True:
                try:
                    async with session.get(os.environ['HA_URL'] + '/api/states') as response:
                        response.raise_for_status()
                        states = {s['entity_id']: s['state'] for s in await response.json()}
                    # The outage helper is a utility disconnect, distinct from the
                    # existing all-supply fault. Battery dispatch remains native.
                    outage = states.get('input_boolean.lab_span_grid_outage')
                    if outage not in ('on', 'off'):
                        raise ValueError('Grid outage state unavailable')
                    self.grid_online = outage == 'off'
                    self.set_grid_online(self.grid_online)
                    values = {entity: float(states[entity]) for entity in entities}
                    if not all(math.isfinite(value) and value >= 0 for value in values.values()):
                        raise ValueError('Invalid circuit meter')
                    self.values = values
                    self.updated = time.monotonic()
                    self.error = ''
                except (aiohttp.ClientError, asyncio.TimeoutError, ValueError, KeyError) as exc:
                    if self.error != 'Home Assistant circuit meters unavailable':
                        logging.warning('Live meter read failed: %s', type(exc).__name__)
                    self.error = 'Home Assistant circuit meters unavailable'
                await asyncio.sleep(0.5)

    async def health(self, request):
        panel = self.panel_status()
        mqtt_connected = panel.get('mqtt_connected', False)
        return web.json_response({'connected': self.connected and mqtt_connected,
                                  'error': self.error or ('' if mqtt_connected else 'Panel MQTT unavailable'),
                                  'source': 'live Energy Lab meters', 'circuits': len(self.values),
                                  **panel})


class LiveLabApp(SimulatorApp):
    def __init__(self, source, **kwargs):
        self.source = source
        self.mqtt_generations = {}
        self.heartbeat_at = {}
        super().__init__(**kwargs)

    async def _load_recorder_data(self, config_path):
        return self.source

    def confirmed_status(self):
        for panel in self._panels.values():
            runtime = panel.runtime
            snapshot = runtime.emitter.last_snapshot if runtime else None
            if snapshot:
                battery = next(iter(snapshot.battery.values()), None)
                mqtt = runtime.mqtt
                connected = bool(mqtt and mqtt.is_connected()
                                 and time.monotonic() - mqtt.last_publish < 5
                                 and self.mqtt_generations.get(panel.serial_number) == mqtt.generation)
                return {'mqtt_connected': connected,
                        'mqtt_generation': mqtt.generation if mqtt else 0,
                        'relays': {runtime.uuid_to_circuit_id[uuid]: circuit.relay_state
                                   for uuid, circuit in snapshot.circuits.items()
                                   if uuid in runtime.uuid_to_circuit_id},
                        'panel_watts': snapshot.meter.instant_grid_power_w,
                        'grid_watts': snapshot.meter.instant_grid_power_w,
                        'grid_online': self.source.grid_online,
                        'battery': None if battery is None else {
                            'capacity_kwh': battery.nameplate_capacity_kwh,
                            'stored_energy_kwh': battery.soe_kwh,
                            'soc_percent': battery.soe_percentage,
                            'discharge_watts': battery.active_power_w,
                        }}
        return {'mqtt_connected': False, 'relays': {}}

    def _set_circuit_relay(self, circuit_id, relay_state):
        # Upstream dashboard changes only producer overrides. Route its commands
        # into the same emitter setter used by MQTT so both UIs operate one relay.
        self._set_circuit_property(circuit_id, 'switch/relay', relay_state == 'CLOSED')

    def _set_circuit_priority(self, circuit_id, priority):
        self._set_circuit_property(circuit_id, 'load-shed/priority', priority)

    def _set_circuit_property(self, circuit_id, property_path, value):
        for panel in self._panels.values():
            runtime = panel.runtime
            if not runtime:
                continue
            for uuid, key in runtime.uuid_to_circuit_id.items():
                if key == circuit_id:
                    handler = runtime.setters.get('circuit', property_path)
                    if handler:
                        handler('circuit', uuid, property_path, value)
                        return
        raise ValueError('Unknown live circuit: ' + circuit_id)

    async def mirror_relays(self):
        while True:
            for panel in self._panels.values():
                runtime = panel.runtime
                if runtime and runtime.mqtt and runtime.mqtt.is_connected():
                    mqtt = runtime.mqtt
                    if self.mqtt_generations.get(panel.serial_number) != mqtt.generation:
                        generation = mqtt.generation
                        runtime.emitter.republish_tree()
                        await runtime.transport.drain()
                        if mqtt.is_connected() and mqtt.generation == generation:
                            self.mqtt_generations[panel.serial_number] = generation
                    # Diff-only telemetry is silent when every circuit is off.
                    # Exercise the same queue and acknowledged broker publish path
                    # so idle meters do not falsely fail the five-second health check.
                    # This lab-owned topic is outside the emitter's eBus namespace.
                    if time.monotonic() - self.heartbeat_at.get(panel.serial_number, 0) >= 2:
                        self.heartbeat_at[panel.serial_number] = time.monotonic()
                        runtime.transport.publish('energy-lab/bridge/heartbeat',
                                                  str(time.time()), qos=1, retain=False)
                        try:
                            await asyncio.wait_for(runtime.transport.drain(), timeout=2)
                        except asyncio.TimeoutError:
                            logging.warning('Panel MQTT heartbeat queue did not drain')
                snapshot = runtime.emitter.last_snapshot if runtime else None
                if snapshot and panel.engine:
                    panel.engine.set_dynamic_overrides(circuit_overrides={
                        runtime.uuid_to_circuit_id[uuid]: {'relay_state': circuit.relay_state}
                        for uuid, circuit in snapshot.circuits.items()
                        if uuid in runtime.uuid_to_circuit_id})
            await asyncio.sleep(.5)


async def main():
    _configure_logging('INFO')
    install_mqtt_receiver()
    source = LivePowerSource()
    app = LiveLabApp(source, config_dir=Path('/app/configs'),
                     config_filter='energy-lab.yaml', cert_dir=Path('/app/certs'),
                     advertise_address=os.environ['ADVERTISE_ADDRESS'], tick_interval=1.0)
    source.panel_status = app.confirmed_status
    source.set_grid_online = app._set_grid_online
    health_app = web.Application()
    health_app.router.add_get('/health', source.health)
    runner = web.AppRunner(health_app)
    await runner.setup()
    await web.TCPSite(runner, '0.0.0.0', 18081).start()
    poll = asyncio.create_task(source.poll())
    mirror = asyncio.create_task(app.mirror_relays())
    try:
        await _run_until_signalled(app)
    finally:
        poll.cancel()
        mirror.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await poll
        with contextlib.suppress(asyncio.CancelledError):
            await mirror
        await runner.cleanup()


if __name__ == '__main__':
    asyncio.run(main())
