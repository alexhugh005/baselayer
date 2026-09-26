"""Download raw ERCOT data for every day the labels need.

Query endpoints only reach back to December 2023, so everything comes from
the archive (bulk-downloaded) or, for prices, the yearly RTM workbooks.
Raw files keep ERCOT's CSV columns as strings; features.py parses them.
Each step skips work whose output already exists, so a rerun resumes.
"""

from __future__ import annotations

import datetime as dt
from pathlib import Path

import pandas as pd

from ..ercot_pull import bulk_download, list_archive, rtm_price_years, rtm_prices
from ..logs import get_logger
from ..storage import write_parq

LOAD = "np6-345-cd"
FORECAST = "np3-565-cd"
OUTAGE_CAP = "np3-233-cd"
LAMBDA = "np6-322-cd"
# The day-ahead load forecast: latest posting at or before 10:00 the day before.
FORECAST_CUTOFF = dt.time(10, 0)


def _log(message):
    get_logger("austin-outage-pull").info(message)


def _write(df, path):
    write_parq(df, path)


def _tag(frames, records):
    posted = {r["doc_id"]: r["posted"] for r in records}
    out = []
    for doc_id, df in frames.items():
        out.append(df.assign(doc_id=doc_id, posted=posted.get(doc_id)))
    return pd.concat(out, ignore_index=True) if out else pd.DataFrame()


def _listing(api, raw, emil_id, start, end):
    """Archive listing for [start, end], cached so reruns skip it."""
    path = raw / "_listing" / f"{emil_id}_{start:%Y%m%d}_{end:%Y%m%d}.parq"
    if path.exists():
        return pd.read_parquet(path).to_dict("records")
    _log(f"{emil_id}: listing archive {start:%Y-%m-%d} to {end:%Y-%m-%d}")
    records = list_archive(api, emil_id, start, end)
    _write(pd.DataFrame(records), path)
    return records


def pull_load(api, raw, days):
    """Actual hourly load by weather zone. One tiny file per day, so take all."""
    path = raw / "load.parq"
    if path.exists():
        return
    start = dt.datetime.combine(days[0], dt.time())
    end = dt.datetime.combine(days[-1] + dt.timedelta(days=2), dt.time())
    records = _listing(api, raw, LOAD, start, end)
    _log(f"{LOAD}: downloading {len(records)} files")
    frames = bulk_download(api, LOAD, [r["doc_id"] for r in records])
    _write(_tag(frames, records), path)


def _pick_one_per_day(records, days, when):
    """For each day, the latest record posted at or before when(day)."""
    listing = pd.DataFrame(records).sort_values("posted")
    picks = {}
    for day in days:
        cutoff = pd.Timestamp(when(day))
        earlier = listing[listing["posted"] <= cutoff]
        if not earlier.empty and cutoff - earlier["posted"].iloc[-1] <= pd.Timedelta(days=1):
            picks[day] = earlier.iloc[-1].to_dict()
    return picks


def _pull_daily_posting(api, raw, emil_id, folder, days, when, date_column, chunk_size):
    out = raw / folder
    todo = [d for d in days if not (out / f"{d}.parq").exists()]
    if not todo:
        return
    start = dt.datetime.combine(todo[0] - dt.timedelta(days=2), dt.time())
    end = dt.datetime.combine(todo[-1] + dt.timedelta(days=1), dt.time())
    records = _listing(api, raw, emil_id, start, end)
    picks = _pick_one_per_day(records, todo, when)
    missing = sorted(set(todo) - set(picks))
    if missing:
        _log(f"{emil_id}: no posting found for {len(missing)} days, e.g. {missing[:5]}")
    doc_ids = sorted({p["doc_id"] for p in picks.values()})
    _log(f"{emil_id}: downloading {len(doc_ids)} files")
    for i in range(0, len(doc_ids), chunk_size):
        chunk = doc_ids[i : i + chunk_size]
        frames = bulk_download(api, emil_id, chunk, chunk_size=chunk_size)
        for day, pick in picks.items():
            if pick["doc_id"] not in frames:
                continue
            df = frames[pick["doc_id"]]
            dates = pd.to_datetime(df[date_column], format="%m/%d/%Y").dt.date
            rows = df[dates == day].assign(doc_id=pick["doc_id"], posted=pick["posted"])
            _write(rows, out / f"{day}.parq")
        _log(f"{emil_id}: {min(i + chunk_size, len(doc_ids))}/{len(doc_ids)} files")


def pull_forecast(api, raw, days):
    """Load forecast by model and weather zone, as posted the morning before."""
    _pull_daily_posting(
        api, raw, FORECAST, "forecast", days,
        when=lambda d: dt.datetime.combine(d - dt.timedelta(days=1), FORECAST_CUTOFF),
        date_column="DeliveryDate", chunk_size=200,
    )


def pull_outage_cap(api, raw, days):
    """Hourly resource outage capacity, as posted just before the day starts."""
    _pull_daily_posting(
        api, raw, OUTAGE_CAP, "outage_cap", days,
        when=lambda d: dt.datetime.combine(d, dt.time()),
        date_column="Date", chunk_size=1000,
    )


def _runs(days):
    """Split sorted days into runs of consecutive days."""
    runs, current = [], [days[0]]
    for day in days[1:]:
        if day - current[-1] == dt.timedelta(days=1):
            current.append(day)
        else:
            runs.append(current)
            current = [day]
    runs.append(current)
    return runs


def pull_lambda(api, raw, days):
    """5-minute SCED system lambda. ~288 files per day, bulk-downloaded per run."""
    out = raw / "lambda"
    todo = [d for d in days if not (out / f"{d}.parq").exists()]
    if not todo:
        return
    runs = _runs(todo)
    _log(f"{LAMBDA}: {len(todo)} days in {len(runs)} runs")
    for n, run in enumerate(runs, 1):
        start = dt.datetime.combine(run[0], dt.time())
        end = dt.datetime.combine(run[-1] + dt.timedelta(days=1), dt.time(0, 15))
        records = list_archive(api, LAMBDA, start, end)
        frames = bulk_download(api, LAMBDA, [r["doc_id"] for r in records])
        df = _tag(frames, records)
        stamps = pd.to_datetime(df["SCEDTimeStamp"], format="%m/%d/%Y %H:%M:%S")
        for day in run:
            _write(df[stamps.dt.date == day].reset_index(drop=True), out / f"{day}.parq")
        if n % 10 == 0 or n == len(runs):
            _log(f"{LAMBDA}: {n}/{len(runs)} runs")


def pull_prices(raw, days, settlement_point="LZ_AEN"):
    """15-minute real-time prices at the Austin load zone, one file per year."""
    years = sorted({d.year for d in days})
    available = rtm_price_years()
    for year in years:
        path = raw / "spp" / f"{year}.parq"
        if path.exists():
            continue
        _log(f"RTM prices: downloading {year}")
        _write(rtm_prices(year, available[year], settlement_point), path)


STEPS = {
    "load": pull_load,
    "forecast": pull_forecast,
    "outage_cap": pull_outage_cap,
    "prices": None,
    "lambda": pull_lambda,
}


def pull(api, raw, days, steps=tuple(STEPS)):
    raw = Path(raw)
    for step in steps:
        if step == "prices":
            pull_prices(raw, days)
        else:
            STEPS[step](api, raw, days)
        _log(f"{step}: done")
