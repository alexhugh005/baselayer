"""Exercise a real broker interruption and recovery in the local virtual lab."""
import json
import subprocess
import time
import urllib.request

from lab_client import request, service


def health():
    with urllib.request.urlopen('http://localhost:18081/health', timeout=5) as response:
        return json.load(response)


def state(entity):
    return request('/api/states/' + entity)['state']


def wait(predicate, message, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.3)
    raise AssertionError(message)


def docker(*args):
    subprocess.run(['docker', 'exec', 'basehack-panelbench', *args], check=True)


def main():
    assert request('/api/config')['location_name'] == 'Johnson Household'
    assert 'Illustrative' in request('/api/states/sensor.virtual_household_power')['attributes']['model']
    assert health()['connected']
    assert state('input_boolean.lab_span_main_outage') == 'off'
    assert state('input_boolean.lab_span_living_room_fault') == 'off'
    lamp = 'light.virtual_living_room_light'
    breaker = 'switch.span_panel_living_room_breaker'
    old_lamp, old_breaker = state(lamp), state(breaker)
    generation = health()['mqtt_generation']
    broker_stopped = False
    try:
        service('switch', 'turn_on', breaker)
        wait(lambda: state('binary_sensor.lab_span_living_room_supply') == 'on', 'Supply unavailable')
        service('light', 'turn_on', lamp)
        wait(lambda: state(lamp) == 'on', 'Lamp did not start')
        broker_stopped = True
        docker('python', '-c',
               'import os,signal; from pathlib import Path; '
               '[os.kill(int(p.parent.name),signal.SIGTERM) '
               'for p in Path("/proc").glob("[0-9]*/comm") '
               'if p.read_text().strip()=="mosquitto"]')
        wait(lambda: not health()['connected'], 'Health missed MQTT failure')
        wait(lambda: state('binary_sensor.lab_span_bridge_connected') == 'off'
             and state(lamp) == 'off', 'Disconnected bridge failed to cut virtual supply')
        print('PASS broker loss is reported and virtual appliances turn off', flush=True)
        # Leave the broker down long enough to exercise multiple failed retries.
        time.sleep(4)
        docker('mosquitto', '-c', '/app/mosquitto/mosquitto.conf', '-d')
        broker_stopped = False
        wait(lambda: health()['connected'] and health()['mqtt_generation'] > generation,
             'Simulator did not reconnect')
        wait(lambda: state('binary_sensor.lab_span_bridge_connected') == 'on'
             and state('binary_sensor.lab_span_living_room_supply') == 'on',
             'HA did not recover')
        assert state(lamp) == 'off', 'Appliance restarted without a command'
        print('PASS automatic MQTT reconnection, tree publication, and HA recovery', flush=True)
        service('light', 'turn_on', lamp)
        wait(lambda: state(lamp) == 'on', 'Lamp unavailable after recovery')
        service('switch', 'turn_off', breaker)
        wait(lambda: health()['relays']['living_room'] == 'OPEN' and state(lamp) == 'off',
             'Recovered MQTT subscription did not operate relay')
        time.sleep(2.2)
        service('switch', 'turn_on', breaker)
        wait(lambda: health()['relays']['living_room'] == 'CLOSED'
             and state('binary_sensor.lab_span_living_room_supply') == 'on',
             'Recovered relay did not close')
        wait(lambda: abs(float(state('sensor.span_panel_current_power'))
                         - float(state('sensor.virtual_household_power'))) < .1,
             'Live power did not resume')
        print('PASS native breaker control and live power after reconnection', flush=True)
    finally:
        if broker_stopped:
            docker('mosquitto', '-c', '/app/mosquitto/mosquitto.conf', '-d')
        wait(lambda: health()['connected'], 'Bridge unavailable during cleanup')
        time.sleep(2.2)
        service('switch', 'turn_' + old_breaker, breaker)
        if old_lamp == 'on' and old_breaker == 'on':
            wait(lambda: state('binary_sensor.lab_span_living_room_supply') == 'on', 'Restore supply')
            service('light', 'turn_on', lamp)
        else:
            service('light', 'turn_off', lamp)


if __name__ == '__main__':
    main()
