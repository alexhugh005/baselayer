"""Live backup acceptance test. Only the named illustrative lab; restores grid afterward."""
import time
from concurrent.futures import ThreadPoolExecutor
from verify import states, panel, wait, CIRCUITS, RELAYS, stopped
from lab_client import request, service
from backup_lab import PEAKS, BUDGET, OUTAGE, admitted


def restore(key):
    service('script', 'lab_span_backup_restore', circuit=key)
    wait(lambda s:s[f'binary_sensor.lab_span_{key}_supply']=='on', f'{key} battery supply', 20)


def run():
    assert request('/api/config')['location_name']=='Johnson Household'
    assert 'Illustrative' in request('/api/states/sensor.virtual_household_power')['attributes']['model']
    wait(lambda s:s.get('binary_sensor.lab_span_bridge_connected')=='on', 'Bridge ready', 60)
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
        restore('laundry')
        service('switch','turn_on','switch.virtual_dryer')
        wait(lambda s:float(s['sensor.virtual_dryer_power'])>=4999, 'Dryer running')
        restore('hvac')
        # Dryer + HVAC reserves 8009 W; restoring the EV would exceed 10500 W.
        service('script','lab_span_backup_restore',circuit='garage')
        assert states()[admitted('garage')]=='off'
        assert states()['binary_sensor.lab_span_garage_supply']=='off'
        # Raw breaker controls must go through the same admission check.
        service('select','select_option',RELAYS['garage'].replace('switch.','select.').replace('_breaker','_circuit_priority'),option='never')
        time.sleep(3)
        service('switch','turn_on',RELAYS['garage'])
        wait(lambda s:s[RELAYS['garage']]=='off' and s[admitted('garage')]=='off','Raw breaker cannot bypass budget',20)
        assert float(states()['sensor.virtual_household_power'])<11000
        print('PASS unsafe circuit restore and raw breaker bypass are blocked',flush=True)
        before=panel()['battery']['stored_energy_kwh']
        time.sleep(8)
        after=panel()['battery']['stored_energy_kwh']
        assert 0<before-after<.03,(before,after)
        assert panel()['battery']['discharge_watts']>4900
        assert abs(panel()['grid_watts'])<1
        print('PASS battery energy falls with the live 5 kW load; utility power stays zero',flush=True)
        service('switch','turn_off',[RELAYS['laundry'],RELAYS['hvac']])
        wait(lambda s:float(s['sensor.lab_span_backup_reserved_power'])==0,'Budget released')
        restore('garage')
        service('input_number','set_value','input_number.virtual_ev_current_limit',value=48)
        service('switch','turn_on','switch.virtual_ev_charger')
        wait(lambda s:float(s['sensor.virtual_ev_charger_power'])==5760,'Outage caps EV at 24 A')
        restore('water_heater')
        restore('living_room')
        # 5760 + 4502 + 73 = 10335; the 500 W essentials reservation must fail.
        service('script','lab_span_backup_restore',circuit='essentials')
        assert states()[admitted('essentials')]=='off'
        assert float(states()['sensor.lab_span_backup_reserved_power'])==10335
        print('PASS EV cap and peak-load reservation leave 500 W headroom',flush=True)
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
    print('Restored grid and quiet household.',flush=True)


if __name__=='__main__':
    run()
