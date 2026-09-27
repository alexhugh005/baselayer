"""Build one operating day's hourly day_ahead feature rows from raw ERCOT pulls.

Mirrors baselayer_data.austin_outage.features.build(): the day's rows come
from its day-ahead load forecast and outage-capacity posting, and its 1-3 day
lags from daily aggregates of the three days before it. Same-day actuals are
never needed, so the day being scored has no load, price or lambda rows.
"""

from __future__ import annotations

import datetime as dt
import logging
import shutil
from pathlib import Path

import numpy as np
import pandas as pd

from baselayer_data.austin_outage import features as f
from baselayer_data.austin_outage import pull as p

log = logging.getLogger(__name__)

HISTORY_DAYS = max(f.LAGS)
# Each raw source, its parser, and the hourly columns daily_features aggregates.
PARTS = {
    "load": (f.load_hourly, ["load_south_c_mw", "load_total_mw"]),
    "forecast": (f.forecast_hourly, ["fcst_south_c_mw", "fcst_total_mw"]),
    "outage_cap": (
        f.outage_cap_hourly,
        ["offline_resource_mw", "offline_irr_mw", "offline_new_equip_mw", "offline_south_mw", "offline_mw"],
    ),
    "price": (f.price_hourly, ["price_rt_mean", "price_rt_max", "price_intervals"]),
    "lambda": (f.lambda_hourly, ["lambda_mean", "lambda_max", "sced_runs"]),
}


class MissingForecast(Exception):
    """The day-ahead load forecast for the day isn't posted (or wasn't pulled)."""


def history_days(day):
    return [day - dt.timedelta(days=i) for i in range(HISTORY_DAYS, 0, -1)]


def pull_day(api, raw, day):
    """Download everything build_day(day) reads into a fresh `raw` folder.

    Returns {step: error} for steps that failed; build_day decides whether
    what did arrive is enough.
    """
    raw = Path(raw)
    if raw.exists():
        # The pull steps skip existing files and cache listings, so a rerun
        # would never see newer postings.
        shutil.rmtree(raw)
    history = history_days(day)
    steps = {
        "forecast": lambda: p.pull_forecast(api, raw, [*history, day]),
        "outage_cap": lambda: p.pull_outage_cap(api, raw, [*history, day]),
        "load": lambda: p.pull_load(api, raw, history),
        "price": lambda: p.pull_recent_prices(api, raw, history),
        "lambda": lambda: p.pull_lambda(api, raw, history),
    }
    failed = {}
    for name, step in steps.items():
        try:
            step()
        except Exception as exc:  # one source failing shouldn't stop the rest
            log.warning("%s pull for %s failed: %s", name, day, exc)
            failed[name] = f"{type(exc).__name__}: {exc}"
    return failed


def _parts(raw):
    parts = {}
    for name, (parse, _columns) in PARTS.items():
        try:
            parts[name] = parse(raw)
        except (FileNotFoundError, KeyError, ValueError) as exc:
            log.info("no %s data in %s (%s)", name, raw, exc)
            parts[name] = None
    return parts


def _hourly(parts, days, base):
    """features.hourly_features for `days`, with rows keyed on the `base` source."""
    frame = parts[base]
    frame = frame[frame["oper_day"].isin(set(days))]
    for name, part in parts.items():
        if name != base and part is not None:
            frame = frame.merge(part, on=f.KEY, how="left")
    frame = frame.copy()
    # The outage file has no DST flag; the repeated hour reuses hour 2.
    cap = parts["outage_cap"]
    repeated = frame["dst_flag"].astype(bool)
    if cap is not None and repeated.any():
        cap = cap.drop(columns="dst_flag")
        fill = frame.loc[repeated, ["oper_day", "hour_ending"]].merge(
            cap, on=["oper_day", "hour_ending"], how="left"
        )
        for col in cap.columns.drop(["oper_day", "hour_ending"]):
            frame.loc[repeated, col] = fill[col].to_numpy()
    for _parse, columns in PARTS.values():
        for col in columns:
            if col not in frame:
                frame[col] = np.nan

    for zone, fcst in (("south_c", "fcst_south_c_mw"), ("total", "fcst_total_mw")):
        actual = frame[f"load_{zone}_mw"]
        frame[f"fcst_err_{zone}_mw"] = actual - frame[fcst]
        frame[f"fcst_err_{zone}_pct"] = frame[f"fcst_err_{zone}_mw"] / frame[fcst] * 100
    frame["offline_to_load"] = frame["offline_mw"] / frame["load_total_mw"]
    day = pd.to_datetime(frame["oper_day"])
    frame["month"] = day.dt.month
    frame["day_of_week"] = day.dt.dayofweek
    return frame.sort_values(f.KEY).reset_index(drop=True)


def build_day(day, raw):
    """Hourly rows for `day` with its forecast, outage capacity and lag columns."""
    parts = _parts(Path(raw))
    if parts["forecast"] is None:
        raise MissingForecast(f"no load forecast data in {raw}")
    target = _hourly(parts, [day], base="forecast")
    if target.empty:
        raise MissingForecast(f"no day-ahead load forecast posted for {day}")
    history = _hourly(parts, history_days(day), base="load") if parts["load"] is not None else None

    # daily_features lags by calendar day; the target day's own aggregates
    # are empty but give it a row to receive the lags.
    daily = f.daily_features(pd.concat([h for h in (history, target) if h is not None], ignore_index=True))
    lag_columns = [c for c in daily.columns if "_lag" in c]
    return target.merge(daily[["oper_day", *lag_columns]], on="oper_day", how="left")


def postings(raw, day):
    """When the forecast and outage-capacity files used for `day` were posted."""
    out = {}
    for folder in ("forecast", "outage_cap"):
        path = Path(raw) / folder / f"{day}.parq"
        posted = pd.read_parquet(path, columns=["posted"])["posted"] if path.exists() else []
        out[folder] = str(pd.Timestamp(max(posted))) if len(posted) else None
    return out
