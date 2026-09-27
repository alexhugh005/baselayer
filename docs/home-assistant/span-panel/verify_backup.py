"""Manual backup acceptance test (pause the API worker first). Local virtual lab only; resets grid."""
import time
from concurrent.futures import ThreadPoolExecutor
from verify import states, panel, wait, CIRCUITS, RELAYS, stopped
from lab_client import request, service
from backup_lab import OUTAGE, admitted

FORMER_LIMIT = 11000


def restore(key):
    service('script', 'lab_span_backup_restore', circuit=key)
    wait(lambda s:s[f'binary_sensor.lab_span_{key}_supply']=='on', f'{key} battery supply', 20)


def run():
    assert request('/api/config')['location_name']=='Johnson Household'
    assert 'Illustrative' in request('/api/states/sensor.virtual_household_power')['attributes']['model']
    wait(lambda s:s.get('binary_sensor.lab_span_bridge_connected')=='on', 'Bridge ready', 60)
    priorities = {k:v for k,v in states().items() if k.startswith('select.span_panel_') and k.endswith('_circuit_priority')}
    try:
        service('input_boolean','turn_off',OUTAGE)
        time.sleep(5)
        service('script','lab_span_restore_supply')
        service('script','base_layer_reset')
        service('input_boolean','turn_on',OUTAGE)
        wait(lambda s:all(stopped(k,s) and s[RELAYS[k]]=='off' for k in CIRCUITS), 'All off on outage', 25)
        wait(lambda s:s.get('binary_sensor.lab_span_backup_ready')=='on', 'Native battery ready', 25)
        assert panel()['grid_online'] is False
        assert abs(panel()['grid_watts'])<1
        print('PASS utility outage islands the native battery and cuts all eight circuits',flush=True)
        for _ in range(20):
            assert panel()['connected'], 'Idle outage falsely disconnected MQTT'
            assert states()['binary_sensor.lab_span_backup_ready']=='on'
            time.sleep(1)
        print('PASS zero-load outage remains connected and ready',flush=True)
        restore('laundry')
        service('switch','turn_on','switch.virtual_dryer')
        wait(lambda s:float(s['sensor.virtual_dryer_power'])>=4999, 'Dryer running')
        restore('hvac')
        service('climate','set_hvac_mode','climate.virtual_room_thermostat',hvac_mode='heat')
        wait(lambda s:float(s['sensor.virtual_household_power'])>5240,'Live dryer and HVAC load')
        # HA allows restoration even when load plus the next estimate exceeds 11 kW.
        restore('garage')
        service('switch','turn_off',RELAYS['garage'])
        wait(lambda s:s[RELAYS['garage']]=='off' and s[admitted('garage')]=='off','Garage off')
        time.sleep(3)
        service('switch','turn_on',RELAYS['garage'])
        wait(lambda s:s['binary_sensor.lab_span_garage_supply']=='on','Raw breaker can restore without a watt budget',20)
        print('PASS circuit restore and raw breaker controls have no HA watt budget',flush=True)
        before=panel()['battery']['stored_energy_kwh']
        time.sleep(8)
        after=panel()['battery']['stored_energy_kwh']
        assert 0<before-after<.03,(before,after)
        assert panel()['battery']['discharge_watts']>4900
        assert abs(panel()['grid_watts'])<1
        print('PASS battery energy falls with the live 5 kW load; utility power stays zero',flush=True)
        service('switch','turn_off',[RELAYS['laundry'],RELAYS['hvac'],RELAYS['garage']])
        wait(lambda s:float(s['sensor.lab_span_backup_reserved_power'])==0,'Loads stopped')
        # Concurrent restores remain serialized even without a watt budget.
        restore('essentials')
        with ThreadPoolExecutor(max_workers=2) as pool:
            def timed_restore(key):
                service('script','lab_span_backup_restore',circuit=key)
                return time.monotonic()
            finished=sorted(pool.map(timed_restore, ['laundry','garage']))
        assert finished[1]-finished[0]>=5, finished
        concurrent=states()
        assert sum(concurrent[admitted(k)]=='on' for k in ['laundry','garage'])==2
        assert float(concurrent['sensor.lab_span_backup_reserved_power'])<=FORMER_LIMIT
        service('switch','turn_off',[RELAYS['laundry'],RELAYS['garage'],RELAYS['essentials']])
        wait(lambda s:float(s['sensor.lab_span_backup_reserved_power'])==0,'Concurrent loads stopped')
        print('PASS concurrent circuit requests serialize and confirm supply',flush=True)
        restore('garage')
        service('input_number','set_value','input_number.virtual_ev_current_limit',value=48)
        service('switch','turn_on','switch.virtual_ev_charger')
        wait(lambda s:float(s['sensor.virtual_ev_charger_power'])==5760,'Outage caps EV at 24 A')
        restore('water_heater')
        service('input_number','set_value','input_number.lab_water_temperature',value=100)
        service('switch','turn_on','switch.virtual_water_heater')
        restore('living_room')
        service('light','turn_on','light.virtual_living_room_light')
        service('fan','turn_on','fan.virtual_ceiling_fan')
        wait(lambda s:10200<float(s['sensor.virtual_household_power'])<=FORMER_LIMIT,'Simultaneous EV, water heat, and living-room loads',20)
        # Add background electronics before crossing the former limit with the dryer.
        service('script','lab_span_backup_restore',circuit='essentials')
        wait(lambda s:s[admitted('essentials')]=='on','Essential electronics restored')
        measured=states()
        assert 10200<float(measured['sensor.lab_span_backup_reserved_power'])<=FORMER_LIMIT
        assert abs(float(measured['sensor.lab_span_backup_reserved_power'])-float(measured['sensor.virtual_household_power']))<1
        restore('laundry')
        service('switch','turn_on','switch.virtual_dryer')
        wait(lambda s:float(s['sensor.virtual_household_power'])>FORMER_LIMIT,'Load exceeds former HA watt limit',20)
        for _ in range(15):
            current = states()
            assert float(current['sensor.virtual_household_power']) > FORMER_LIMIT
            assert all(current[f'binary_sensor.lab_span_{k}_supply']=='on' for k in ['garage','water_heater','laundry'])
            time.sleep(1)
        print('PASS loads above 11 kW stay powered without HA overload disconnect',flush=True)
        # All-power fault overrides battery mode as well.
        service('input_boolean','turn_on','input_boolean.lab_span_main_outage')
        wait(lambda s:float(s['sensor.virtual_household_power'])==0,'Physical supply fault overrides backup')
        service('input_boolean','turn_off','input_boolean.lab_span_main_outage')
        print('PASS whole-house fault still cuts battery-backed supply',flush=True)
    finally:
        service('input_boolean','turn_off',OUTAGE)
        time.sleep(5)
        service('script','lab_span_restore_supply')
        wait(lambda s:all(s[f'binary_sensor.lab_span_{k}_supply']=='on' for k in CIRCUITS),'Grid supply restored',25)
        service('script','base_layer_reset')
        assert {k:v for k,v in states().items() if k in priorities} == priorities, 'Backup scenario changed circuit priorities'
    print('Restored grid and quiet household.',flush=True)


if __name__=='__main__':
    run()
