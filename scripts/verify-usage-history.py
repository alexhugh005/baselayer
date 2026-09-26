#!/usr/bin/env python3
"""Isolated end-to-end history smoke test. Requires a built .NET API; no real HA or credentials."""
import datetime as dt
import http.server
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'backend/src/BaseLayer.Api/bin/Debug/net10.0/BaseLayer.Api.dll'


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class FakeHomeAssistant(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0)))
        assert self.path == '/auth/token', 'Unexpected device control request'
        self.respond({'access_token': 'fake-ha-access', 'refresh_token': 'fake-ha-refresh', 'expires_in': 3600})

    def do_GET(self):
        assert self.path == '/api/states'
        self.respond([
            {'entity_id': 'switch.heater', 'state': 'on', 'attributes': {'friendly_name': 'Test heater'}},
            {'entity_id': 'sensor.heater_power', 'state': '1.5',
             'attributes': {'friendly_name': 'Heater power', 'device_class': 'power', 'unit_of_measurement': 'kW'}},
        ])


def main():
    assert DLL.exists(), 'Build the API before running this script.'
    ha = http.server.ThreadingHTTPServer(('127.0.0.1', 0), FakeHomeAssistant)
    threading.Thread(target=ha.serve_forever, daemon=True).start()
    process = None
    with tempfile.TemporaryDirectory(prefix='base-layer-usage-') as temporary:
        directory = Path(temporary)
        port = free_port()
        base = f'http://127.0.0.1:{port}'
        token = secrets.token_hex(32)
        env = os.environ.copy()
        env.update({
            'ASPNETCORE_ENVIRONMENT': 'Development',
            'LocalDemo__Enabled': 'true', 'LocalDemo__Token': token, 'LocalDemo__Port': str(port),
            'HomeAssistant__AllowedOrigins__0': f'http://127.0.0.1:{ha.server_port}',
            'ConnectionStrings__Default': f'Data Source={directory / "usage.db"}',
            'Logging__LogLevel__Default': 'Warning',
        })
        log = open(directory / 'api.log', 'w+')

        def request(path, method='GET', body=None, authorized=True):
            headers = {'Content-Type': 'application/json'}
            if authorized:
                headers['Authorization'] = f'Bearer {token}'
            req = urllib.request.Request(base + path, method=method, headers=headers,
                                         data=None if body is None else json.dumps(body).encode())
            with urllib.request.urlopen(req, timeout=10) as response:
                raw = response.read()
                return json.loads(raw) if raw else None

        def start():
            nonlocal process
            process = subprocess.Popen(['dotnet', str(DLL)], cwd=directory, env=env, stdout=log, stderr=log)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError('API exited unexpectedly')
                try:
                    request('/health')
                    return
                except urllib.error.URLError:
                    time.sleep(.1)
            raise RuntimeError('API did not become healthy')

        def stop():
            nonlocal process
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
                process = None

        try:
            start()
            pending = request('/api/connections/home-assistant/start', 'POST',
                              {'name': 'History test', 'baseUrl': f'http://127.0.0.1:{ha.server_port}'})
            home = request('/api/connections/home-assistant/complete', 'POST', {'code': 'fake-code', 'state': pending['state']})
            home_id = home['id']
            request(f'/api/homes/{home_id}/settings', 'PUT', {
                'allowAll': False, 'allowFutureDevices': False, 'allowedEntityIds': [],
                'householdPowerSensorId': None, 'devicePowerSensors': {'switch.heater': 'sensor.heater_power'},
                'powerSource': 'deviceSum',
            })
            now = dt.datetime.now(dt.timezone.utc).replace(second=0, microsecond=0)
            query = urllib.parse.urlencode({'from': (now - dt.timedelta(minutes=1)).isoformat(),
                                           'to': (now + dt.timedelta(minutes=2)).isoformat(), 'resolution': 'minute'})
            path = f'/api/homes/{home_id}/devices/switch.heater/usage?{query}'
            for _ in range(30):
                result = request(path)
                if sum(b['coveredSeconds'] for b in result['buckets']) >= 6:
                    break
                time.sleep(1)
            else:
                raise AssertionError('Background polling did not record covered usage')
            assert result['energyMethod'] == 'estimatedFromPower'
            covered = sum(b['coveredSeconds'] for b in result['buckets'])
            energy = sum(b['estimatedEnergyWh'] or 0 for b in result['buckets'])
            assert abs(energy - 1500 * covered / 3600) < 1e-8
            assert all(b['bucketStartUtc'].endswith('Z') for b in result['buckets'])
            for request_path, authorized, expected in [
                (path, False, 401),
                (path.replace(home_id, '00000000-0000-0000-0000-000000000001'), True, 404),
                (path.replace('resolution=minute', 'resolution=day'), True, 400),
            ]:
                try:
                    request(request_path, authorized=authorized)
                    raise AssertionError(f'Expected HTTP {expected}')
                except urllib.error.HTTPError as error:
                    assert error.code == expected, (error.code, expected)
            states = request(f'/api/homes/{home_id}/devices/switch.heater/state-history?{query}')
            assert states and states[0]['startsObservationSegment']
            stop()
            # Seed historical minutes in this disposable DB and verify startup compaction.
            old_hour = (now - dt.timedelta(days=91)).replace(minute=0)
            old_timestamp = old_hour.replace(tzinfo=None).isoformat(sep=' ')
            with sqlite3.connect(directory / 'usage.db') as database:
                series_id = database.execute('SELECT Id FROM DeviceMeasurementSeries').fetchone()[0]
                database.execute('UPDATE DeviceMeasurementSeries SET StartedUtc = ?', (old_timestamp,))
                database.execute('''INSERT INTO DeviceUsageBuckets
                    (MeasurementSeriesId, BucketStartUtc, Resolution, WattSeconds, CoveredSeconds,
                     MinWatts, MaxWatts, SampleCount, RollupPending) VALUES (?, ?, 'minute', 90000, 60, 1500, 1500, 4, 1)''',
                                 (series_id, old_timestamp))
            start()
            hourly_query = urllib.parse.urlencode({'from': old_hour.isoformat(),
                                                   'to': (old_hour + dt.timedelta(hours=1)).isoformat(), 'resolution': 'hour'})
            for _ in range(10):
                hourly = request(f'/api/homes/{home_id}/devices/switch.heater/usage?{hourly_query}')
                if hourly['buckets']:
                    break
                time.sleep(.2)
            assert len(hourly['buckets']) == 1
            assert hourly['buckets'][0]['estimatedEnergyWh'] == 25
            with sqlite3.connect(directory / 'usage.db') as database:
                assert database.execute("SELECT COUNT(*) FROM DeviceUsageBuckets WHERE Resolution = 'minute' AND BucketStartUtc = ?",
                                        (old_timestamp,)).fetchone()[0] == 0
            request(f'/api/homes/{home_id}', 'DELETE')
            with sqlite3.connect(directory / 'usage.db') as database:
                for table in ['DeviceMeasurementSeries', 'DeviceUsageBuckets', 'DeviceUsageCursors', 'DeviceStateEvents']:
                    assert database.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0] == 0
            print(json.dumps({'result': 'passed', 'backgroundCollection': True, 'authentication': True,
                              'hourlyCompactionAndRetention': True, 'cascadeDeletion': True,
                              'observedSeconds': round(covered, 3), 'estimatedEnergyWh': round(energy, 3)}))
        except Exception:
            log.flush()
            log.seek(0)
            print(log.read()[-8000:])
            raise
        finally:
            stop()
            log.close()
            ha.shutdown()
            ha.server_close()


if __name__ == '__main__':
    main()
