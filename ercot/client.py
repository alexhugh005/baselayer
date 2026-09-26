"""Authenticated ERCOT Public Data API client.

Registration and token flow:
https://developer.ercot.com/applications/pubapi/user-guide/registration-and-authentication/
"""

from __future__ import annotations

import json
import os
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urljoin, urlsplit, urlunsplit
from urllib.request import Request, urlopen

TOKEN_URL = (
    "https://ercotb2c.b2clogin.com/ercotb2c.onmicrosoft.com"
    "/B2C_1_PUBAPI-ROPC-FLOW/oauth2/v2.0/token"
)
CLIENT_ID = "fec253ea-0d06-4272-a5e6-b478baeecd70"
API_BASE = "https://api.ercot.com"
USER_AGENT = "baselayer-ercot/1.0"
TOKEN_SKEW_SECONDS = 60
DEFAULT_TIMEOUT = 30


class ErcotError(Exception):
    """Base error for ERCOT client failures."""


class ErcotConfigError(ErcotError):
    """Missing or invalid client configuration."""


class ErcotAuthError(ErcotError):
    """Token request failed."""


class ErcotHttpError(ErcotError):
    def __init__(self, status, message, body=None):
        super().__init__(f"{status}: {message}")
        self.status = status
        self.body = body


def _parse_dotenv(path):
    values = {}
    text = Path(path).read_text(encoding="utf-8")
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip("'").strip('"')
        if key:
            values[key] = value
    return values


def load_config(env_file=None):
    """Load credentials from a .env file and process environment.

    Search order for the file: `env_file`, `$ERCOT_ENV_FILE`, `./.env`,
    `ercot/.env` next to this package.
    """
    candidates = []
    if env_file:
        candidates.append(Path(env_file))
    if os.environ.get("ERCOT_ENV_FILE"):
        candidates.append(Path(os.environ["ERCOT_ENV_FILE"]))
    candidates.append(Path.cwd() / ".env")
    candidates.append(Path(__file__).resolve().parent / ".env")
    file_values = {}
    for path in candidates:
        if path.is_file():
            file_values = _parse_dotenv(path)
            break
    merged = dict(file_values)
    for key in (
        "ERCOT_USERNAME",
        "ERCOT_PASSWORD",
        "ERCOT_SUBSCRIPTION_KEY",
        "ERCOT_CLIENT_ID",
        "ERCOT_TOKEN_URL",
        "ERCOT_API_BASE",
    ):
        if os.environ.get(key):
            merged[key] = os.environ[key]
    return merged


def _json_body(raw):
    if not raw:
        return None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return raw.decode("utf-8", "replace")


def http_json(method, url, headers=None, data=None, timeout=DEFAULT_TIMEOUT):
    request_headers = {"User-Agent": USER_AGENT, "Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    body = data.encode("utf-8") if isinstance(data, str) else data
    request = Request(url, data=body, headers=request_headers, method=method)
    try:
        with urlopen(request, timeout=timeout) as response:
            raw = response.read()
            status = getattr(response, "status", 200)
    except HTTPError as exc:
        raw = exc.read()
        parsed = _json_body(raw)
        message = exc.reason
        if isinstance(parsed, dict):
            message = (
                parsed.get("error_description")
                or parsed.get("error_message")
                or parsed.get("message")
                or parsed.get("error")
                or message
            )
        raise ErcotHttpError(exc.code, message, parsed) from exc
    except URLError as exc:
        raise ErcotError(f"request failed: {exc.reason}") from exc
    parsed = _json_body(raw)
    if status >= 400:
        raise ErcotHttpError(status, "request failed", parsed)
    return parsed


class PublicApi:
    """Client for https://api.ercot.com/api/public-reports."""

    def __init__(
        self,
        username=None,
        password=None,
        subscription_key=None,
        client_id=CLIENT_ID,
        token_url=TOKEN_URL,
        api_base=API_BASE,
        timeout=DEFAULT_TIMEOUT,
        config=None,
    ):
        values = dict(config or {})
        self.username = username or values.get("ERCOT_USERNAME")
        self.password = password or values.get("ERCOT_PASSWORD")
        self.subscription_key = subscription_key or values.get("ERCOT_SUBSCRIPTION_KEY")
        self.client_id = client_id or values.get("ERCOT_CLIENT_ID") or CLIENT_ID
        self.token_url = token_url or values.get("ERCOT_TOKEN_URL") or TOKEN_URL
        self.api_base = (api_base or values.get("ERCOT_API_BASE") or API_BASE).rstrip("/")
        self.timeout = timeout
        self._id_token = None
        self._token_expires_at = 0

    @classmethod
    def from_env(cls, env_file=None):
        return cls(config=load_config(env_file))

    def _require_credentials(self):
        missing = [
            name
            for name, value in (
                ("ERCOT_USERNAME", self.username),
                ("ERCOT_PASSWORD", self.password),
                ("ERCOT_SUBSCRIPTION_KEY", self.subscription_key),
            )
            if not value
        ]
        if missing:
            raise ErcotConfigError(
                "missing " + ", ".join(missing) + "; see ercot/.env.example"
            )

    def fetch_token(self):
        self._require_credentials()
        form = urlencode(
            {
                "username": self.username,
                "password": self.password,
                "grant_type": "password",
                "scope": f"openid {self.client_id} offline_access",
                "client_id": self.client_id,
                "response_type": "id_token",
            }
        )
        try:
            payload = http_json(
                "POST",
                self.token_url,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
                data=form,
                timeout=self.timeout,
            )
        except ErcotHttpError as exc:
            raise ErcotAuthError(str(exc)) from exc
        token = payload.get("id_token") if isinstance(payload, dict) else None
        if not token:
            raise ErcotAuthError("token response did not include id_token")
        expires_in = 3600
        if isinstance(payload, dict) and payload.get("expires_in"):
            try:
                expires_in = int(payload["expires_in"])
            except (TypeError, ValueError):
                expires_in = 3600
        self._id_token = token
        self._token_expires_at = time.time() + max(expires_in - TOKEN_SKEW_SECONDS, 0)
        return token

    def id_token(self):
        if not self._id_token or time.time() >= self._token_expires_at:
            return self.fetch_token()
        return self._id_token

    def _url(self, path):
        if path.startswith("http://") or path.startswith("https://"):
            return path
        return urljoin(self.api_base + "/", path.lstrip("/"))

    def request(self, path, params=None):
        url = self._url(path)
        if params:
            query = urlencode(
                {key: value for key, value in params.items() if value is not None}
            )
            parts = urlsplit(url)
            joined = "&".join(item for item in (parts.query, query) if item)
            url = urlunsplit((parts.scheme, parts.netloc, parts.path, joined, parts.fragment))
        headers = {
            "Authorization": f"Bearer {self.id_token()}",
            "Ocp-Apim-Subscription-Key": self.subscription_key,
        }
        try:
            return http_json("GET", url, headers=headers, timeout=self.timeout)
        except ErcotHttpError as exc:
            if exc.status == 429:
                raise ErcotHttpError(
                    429,
                    "rate limited (ERCOT allows 30 requests per minute)",
                    exc.body,
                ) from exc
            raise

    def products_page(self, **params):
        return self.request("/api/public-reports", params=params or None)

    def products(self, all_pages=True, **params):
        """Return EMIL product records, following HAL next links when requested."""
        items = []
        path = "/api/public-reports"
        query = dict(params)
        while path:
            page = self.request(path, params=query or None)
            query = None
            embedded = page.get("_embedded", {}) if isinstance(page, dict) else {}
            items.extend(embedded.get("products", []))
            if not all_pages:
                break
            nxt = (
                page.get("_links", {}).get("next", {}).get("href")
                if isinstance(page, dict)
                else None
            )
            path = nxt
        return items

    @staticmethod
    def emil_path(emil_id):
        return str(emil_id).strip().lower()

    def product(self, emil_id, **params):
        return self.request(f"/api/public-reports/{self.emil_path(emil_id)}", params=params or None)

    def artifact(self, emil_id, artifact, **params):
        emil = self.emil_path(emil_id)
        name = str(artifact).strip().strip("/")
        return self.request(f"/api/public-reports/{emil}/{name}", params=params or None)

    def archive(self, emil_id, **params):
        return self.request(
            f"/api/public-reports/archive/{self.emil_path(emil_id)}",
            params=params or None,
        )
