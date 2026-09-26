"""Small authenticated client for the local virtual lab; never prints credentials."""
import json
import os
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
TOKEN_PATH = ROOT / '.local/span-panel/secrets/ha-token'
BASE = os.environ.get('HA_URL', 'http://localhost:8123')


def request(path, data=None, method=None):
    req = urllib.request.Request(
        BASE + path, data=None if data is None else json.dumps(data).encode(),
        headers={'Content-Type': 'application/json',
                 'Authorization': 'Bearer ' + TOKEN_PATH.read_text().strip()}, method=method)
    with urllib.request.urlopen(req, timeout=60) as response:
        return json.load(response)


def service(domain, action, entity=None, **data):
    return request(f'/api/services/{domain}/{action}',
                   {**({'entity_id': entity} if entity else {}), **data})
