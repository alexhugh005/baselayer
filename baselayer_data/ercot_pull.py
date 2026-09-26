"""ERCOT access for the outage dataset: paced Public API calls, bulk archive
downloads, and the yearly RTM price workbooks on ercot.com."""

from __future__ import annotations

import io
import json
import time
import zipfile

import pandas as pd

from .ercot.client import ErcotHttpError, PublicApi, http_exchange, http_json

MIN_INTERVAL = 2.1  # seconds; ERCOT allows 30 requests per minute
BULK_CHUNK = 1000
MIS_LIST = "https://www.ercot.com/misapp/servlets/IceDocListJsonWS?reportTypeId={}"
MIS_DOWNLOAD = "https://www.ercot.com/misdownload/servlets/mirDownload?doclookupId={}"
RTM_PRICES_REPORT = 13061  # NP6-785-ER Historical RTM Load Zone and Hub Prices


class PacedApi(PublicApi):
    """PublicApi that spaces calls under the rate limit and retries 429s."""

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._next_call = 0.0

    @classmethod
    def from_env(cls, env_file=None):
        api = super().from_env(env_file)
        api.timeout = 180
        return api

    def _paced(self, call):
        for attempt in range(6):
            wait = self._next_call - time.monotonic()
            if wait > 0:
                time.sleep(wait)
            self._next_call = time.monotonic() + MIN_INTERVAL
            try:
                return call()
            except ErcotHttpError as exc:
                if exc.status != 429 or attempt == 5:
                    raise
                time.sleep(15 * (attempt + 1))

    def request(self, path, params=None):
        return self._paced(lambda: super(PacedApi, self).request(path, params))

    def request_bytes(self, path, params=None, method="GET", data=None, headers=None):
        return self._paced(
            lambda: super(PacedApi, self).request_bytes(path, params, method, data, headers)
        )


def list_archive(api, emil_id, start, end):
    """Archive records posted in [start, end] (naive Central datetimes)."""
    records = api.iter_archive(
        emil_id,
        postDatetimeFrom=start.strftime("%Y-%m-%dT%H:%M:%S"),
        postDatetimeTo=end.strftime("%Y-%m-%dT%H:%M:%S"),
        size=1000,
    )
    return [
        {"doc_id": r["docId"], "posted": pd.Timestamp(r["postDatetime"]), "name": r["friendlyName"]}
        for r in records
    ]


def _read_csv_zip(raw):
    with zipfile.ZipFile(io.BytesIO(raw)) as zf:
        name = next(n for n in zf.namelist() if n.lower().endswith(".csv"))
        with zf.open(name) as fh:
            return pd.read_csv(fh, dtype=str)


def bulk_download(api, emil_id, doc_ids, chunk_size=BULK_CHUNK):
    """Download archive documents; return {doc_id: DataFrame of raw CSV strings}."""
    frames = {}
    ids = list(doc_ids)
    for i in range(0, len(ids), chunk_size):
        chunk = ids[i : i + chunk_size]
        _headers, raw = api.request_bytes(
            f"/api/public-reports/archive/{api.emil_path(emil_id)}/download",
            method="POST",
            data=json.dumps({"docIds": chunk}),
            headers={"Content-Type": "application/json"},
        )
        with zipfile.ZipFile(io.BytesIO(raw)) as outer:
            for name in outer.namelist():
                doc_id = int(name.split(".", 1)[0])
                frames[doc_id] = _read_csv_zip(outer.read(name))
        missing = set(chunk) - set(frames)
        if missing:
            raise RuntimeError(f"{emil_id}: bulk download missed {len(missing)} documents")
    return frames


def rtm_price_years():
    """{year: MIS DocID} for the yearly Historical RTM price workbooks."""
    payload = http_json("GET", MIS_LIST.format(RTM_PRICES_REPORT), timeout=60)
    docs = payload["ListDocsByRptTypeRes"]["DocumentList"]
    years = {}
    for item in docs:
        doc = item["Document"]
        year = int(doc["FriendlyName"].rsplit("_", 1)[-1])
        years.setdefault(year, doc["DocID"])  # newest posting first
    return years


def rtm_prices(year, doc_id, settlement_point="LZ_AEN"):
    """15-minute real-time settlement point prices for one year and point."""
    _status, _headers, raw = http_exchange("GET", MIS_DOWNLOAD.format(doc_id), timeout=600)
    with zipfile.ZipFile(io.BytesIO(raw)) as zf:
        name = next(n for n in zf.namelist() if n.lower().endswith(".xlsx"))
        book = io.BytesIO(zf.read(name))
    sheets = pd.read_excel(book, sheet_name=None, engine="calamine", dtype=str)
    df = pd.concat(sheets.values(), ignore_index=True)
    return df[df["Settlement Point Name"] == settlement_point].reset_index(drop=True)
