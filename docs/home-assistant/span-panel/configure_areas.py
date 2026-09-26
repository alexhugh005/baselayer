"""Place the virtual circuits and their appliances in Home Assistant areas."""
import asyncio
import json
from pathlib import Path
import aiohttp
from lab_client import BASE, TOKEN_PATH


async def main():
    here = Path(__file__).resolve().parent
    circuits = json.loads((here/'circuits.json').read_text())
    relays = json.loads((here/'relays.json').read_text())
    async with aiohttp.ClientSession() as session:
        async with session.ws_connect(BASE+'/api/websocket') as ws:
            await ws.receive_json()
            await ws.send_json({'type':'auth', 'access_token':TOKEN_PATH.read_text().strip()})
            assert (await ws.receive_json())['type']=='auth_ok'
            sequence = 0
            async def call(kind, **kwargs):
                nonlocal sequence
                sequence += 1
                await ws.send_json({'id':sequence, 'type':kind, **kwargs})
                response=await ws.receive_json()
                assert response.get('success'), response
                return response['result']
            areas = {a['name']:a['area_id'] for a in await call('config/area_registry/list')}
            registry = {e['entity_id']:e for e in await call('config/entity_registry/list')}
            for key,item in circuits.items():
                area=item['area']
                if area not in areas:
                    areas[area]=(await call('config/area_registry/create',name=area))['area_id']
                entities = [relays[key], *item['devices'], *['sensor.virtual_'+m+'_power' for m in item['meters']],
                            f'binary_sensor.lab_span_{key}_supply', f'sensor.lab_span_{key}_power', f'input_boolean.lab_span_{key}_fault']
                for entity in entities:
                    if entity in registry:
                        await call('config/entity_registry/update',entity_id=entity,area_id=areas[area])
            count = len({item['area'] for item in circuits.values()})
            print(f'Assigned circuit relays, virtual appliances, and meters to {count} household areas.')


asyncio.run(main())
