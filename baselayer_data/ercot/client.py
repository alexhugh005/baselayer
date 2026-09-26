"""Authenticated ERCOT Public Data API client.

Registration and token flow:
https://developer.ercot.com/applications/pubapi/user-guide/registration-and-authentication/
"""

from __future__ import annotations

import json
import os
import re
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import unquote, urlencode, urljoin, urlsplit, urlunsplit
from urllib.request import Request, urlopen

TOKEN_URL = (
    "https://ercotb2c.b2clogin.com/ercotb2c.onmicrosoft.com"
    "/B2C_1_PUBAPI-ROPC-FLOW/oauth2/v2.0/token"
)
CLIENT_ID = "fec253ea-0d06-4272-a5e6-b478baeecd70"
API_BASE = "https://api.ercot.com"
ESR_API_PREFIX = "/api/public-data"
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
    `baselayer_data/ercot/.env` next to this package.
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
        "ERCOT_PUBLIC_API_SUBSCRIPTION_KEY",
        "ERCOT_ESR_API_SUBSCRIPTION_KEY",
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


def http_exchange(method, url, headers=None, data=None, timeout=DEFAULT_TIMEOUT):
    """Return status, lower-cased headers, and the raw body."""
    request_headers = {"User-Agent": USER_AGENT, "Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    body = data.encode("utf-8") if isinstance(data, str) else data
    request = Request(url, data=body, headers=request_headers, method=method)
    try:
        with urlopen(request, timeout=timeout) as response:
            raw = response.read()
            status = getattr(response, "status", 200)
            response_headers = {key.lower(): value for key, value in response.headers.items()}
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
    if status >= 400:
        raise ErcotHttpError(status, "request failed", _json_body(raw))
    return status, response_headers, raw


def http_json(method, url, headers=None, data=None, timeout=DEFAULT_TIMEOUT):
    _status, _headers, raw = http_exchange(
        method, url, headers=headers, data=data, timeout=timeout
    )
    return _json_body(raw)


_FILENAME_STAR = re.compile(r"filename\*\s*=\s*(?:UTF-8''|utf-8'')([^;]+)", re.I)
_FILENAME = re.compile(r'filename\s*=\s*"?([^";]+)"?', re.I)


def filename_from_content_disposition(header):
    if not header:
        return None
    match = _FILENAME_STAR.search(header)
    if match:
        return unquote(match.group(1).strip().strip('"'))
    match = _FILENAME.search(header)
    if match:
        return match.group(1).strip()
    return None


def safe_filename(name, fallback="download.bin"):
    """Keep a server-supplied name inside one directory."""
    text = str(name or "").replace("\\", "/").split("/")[-1].strip().strip('"').strip("'")
    cleaned = "".join(ch for ch in text if ch.isprintable() and ch not in '<>:"|?*')
    cleaned = cleaned.strip().strip(".")
    if not cleaned or cleaned in {".", ".."}:
        return fallback
    return cleaned


def write_json(path, payload):
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(payload, indent=2, default=str) + "\n",
        encoding="utf-8",
    )
    return destination


def with_query(url, params):
    if not params:
        return url
    query = urlencode({key: value for key, value in params.items() if value is not None})
    parts = urlsplit(url)
    joined = "&".join(item for item in (parts.query, query) if item)
    return urlunsplit((parts.scheme, parts.netloc, parts.path, joined, parts.fragment))


class PublicApi:
    """Client for https://api.ercot.com/api/public-reports and /api/public-data.

    Each API is a separate API Explorer subscription with its own key:
    "Public API" for /api/public-reports (EMIL reports) and "Energy Storage
    Resource (ESR) API" for /api/public-data.
    """

    def __init__(
        self,
        username=None,
        password=None,
        subscription_key=None,
        esr_subscription_key=None,
        client_id=CLIENT_ID,
        token_url=TOKEN_URL,
        api_base=API_BASE,
        timeout=DEFAULT_TIMEOUT,
        config=None,
    ):
        values = dict(config or {})
        self.username = username or values.get("ERCOT_USERNAME")
        self.password = password or values.get("ERCOT_PASSWORD")
        # ERCOT_SUBSCRIPTION_KEY is the older name for the Public API key.
        self.subscription_key = (
            subscription_key
            or values.get("ERCOT_PUBLIC_API_SUBSCRIPTION_KEY")
            or values.get("ERCOT_SUBSCRIPTION_KEY")
        )
        self.esr_subscription_key = esr_subscription_key or values.get(
            "ERCOT_ESR_API_SUBSCRIPTION_KEY"
        )
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
            )
            if not value
        ]
        if missing:
            raise ErcotConfigError(
                "missing " + ", ".join(missing) + "; see baselayer_data/ercot/.env.example"
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

    def _subscription_key(self, url):
        path = urlsplit(url).path
        if path == ESR_API_PREFIX or path.startswith(ESR_API_PREFIX + "/"):
            key, name = self.esr_subscription_key, "ERCOT_ESR_API_SUBSCRIPTION_KEY"
        else:
            key, name = self.subscription_key, "ERCOT_PUBLIC_API_SUBSCRIPTION_KEY"
        if not key:
            raise ErcotConfigError(f"missing {name}; see baselayer_data/ercot/.env.example")
        return key

    def _auth_headers(self, url, extra=None):
        headers = {
            "Ocp-Apim-Subscription-Key": self._subscription_key(url),
            "Authorization": f"Bearer {self.id_token()}",
        }
        if extra:
            headers.update(extra)
        return headers

    def _reraise(self, exc):
        if exc.status == 429:
            raise ErcotHttpError(
                429,
                "rate limited (ERCOT allows 30 requests per minute)",
                exc.body,
            ) from exc
        raise exc

    def request(self, path, params=None):
        url = with_query(self._url(path), params)
        try:
            return http_json("GET", url, headers=self._auth_headers(url), timeout=self.timeout)
        except ErcotHttpError as exc:
            self._reraise(exc)

    def request_bytes(self, path, params=None, method="GET", data=None, headers=None):
        """GET or POST a file endpoint and return headers plus raw bytes."""
        extra = {"Accept": "*/*"}
        if headers:
            extra.update(headers)
        url = with_query(self._url(path), params)
        try:
            _status, response_headers, raw = http_exchange(
                method,
                url,
                headers=self._auth_headers(url, extra),
                data=data,
                timeout=self.timeout,
            )
        except ErcotHttpError as exc:
            self._reraise(exc)
        return response_headers, raw

    def save_bytes(
        self,
        path,
        directory,
        filename=None,
        params=None,
        method="GET",
        data=None,
        headers=None,
        skip_existing=False,
    ):
        """Write one API file under `directory` and return its path."""
        directory = Path(directory)
        directory.mkdir(parents=True, exist_ok=True)
        preferred = safe_filename(filename) if filename else None
        if skip_existing and preferred is not None:
            existing = directory / preferred
            if existing.is_file():
                return existing
        response_headers, raw = self.request_bytes(
            path, params=params, method=method, data=data, headers=headers
        )
        header_name = filename_from_content_disposition(
            response_headers.get("content-disposition")
        )
        tail = urlsplit(self._url(path)).path.rstrip("/").split("/")[-1]
        name = safe_filename(header_name or preferred or tail)
        destination = (directory / name).resolve()
        if destination.parent != directory.resolve():
            raise ErcotError(f"refusing to write outside {directory}")
        destination.write_bytes(raw)
        return destination

    def download_artifact(
        self,
        emil_id,
        artifact,
        directory,
        file_format="csv",
        skip_existing=False,
        **params,
    ):
        """Save one EMIL artifact. `file_format` is `csv` or `json`."""
        emil = self.emil_path(emil_id)
        name = str(artifact).strip().strip("/")
        query = dict(params)
        query["download"] = file_format
        return self.save_bytes(
            f"/api/public-reports/{emil}/{name}",
            Path(directory) / emil,
            filename=f"{name}.{file_format}",
            params=query,
            skip_existing=skip_existing,
        )

    def iter_archive(self, emil_id, **params):
        """Yield archive documents, following `_meta.totalPages`."""
        page = int(params.pop("page", 1) or 1)
        size = int(params.pop("size", 200) or 200)
        seen = set()
        while True:
            payload = self.archive(emil_id, page=page, size=size, **params)
            records = payload.get("archives") if isinstance(payload, dict) else None
            if not isinstance(records, list) or not records:
                break
            fresh = []
            for record in records:
                doc_id = record.get("docId")
                key = doc_id if doc_id is not None else (
                    record.get("friendlyName"),
                    record.get("postDatetime"),
                )
                if key in seen:
                    continue
                seen.add(key)
                fresh.append(record)
            if not fresh:
                break
            yield from fresh
            meta = payload.get("_meta") or {}
            total_pages = meta.get("totalPages")
            try:
                total_pages = int(total_pages) if total_pages is not None else None
            except (TypeError, ValueError):
                total_pages = None
            if total_pages is not None and page >= total_pages:
                break
            if total_pages is None and len(records) < size:
                break
            page += 1

    def download_archive(
        self,
        emil_id,
        directory,
        limit=None,
        pause_seconds=2.0,
        skip_existing=True,
        **params,
    ):
        """Save posted archive files for one EMIL product.

        ERCOT allows 30 requests per minute, so file downloads are paced.
        """
        dest = Path(directory) / self.emil_path(emil_id)
        saved = []
        for record in self.iter_archive(emil_id, **params):
            if limit is not None and len(saved) >= limit:
                break
            href = ((record.get("_links") or {}).get("endpoint") or {}).get("href")
            if not href:
                continue
            fallback = record.get("friendlyName") or f"{record.get('docId', 'archive')}.bin"
            if saved and pause_seconds:
                time.sleep(pause_seconds)
            saved.append(
                self.save_bytes(href, dest, filename=fallback, skip_existing=skip_existing)
            )
        return saved

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
