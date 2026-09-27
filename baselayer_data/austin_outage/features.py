"""Turn raw ERCOT pulls into hourly and daily feature tables.

Rows are keyed by (oper_day, hour_ending, dst_flag): ERCOT hour ending 1-24
in Central time, with dst_flag marking the repeated hour on the fall-back day.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd

from ..storage import save

KEY = ["oper_day", "hour_ending", "dst_flag"]
DAILY_AGGS = {
    "load_south_c_mw": ["max", "mean"],
    "load_total_mw": ["max", "mean"],
    "fcst_err_south_c_pct": ["max", "min", "mean"],
    "fcst_err_total_pct": ["max", "min", "mean"],
    "offline_mw": ["max", "mean"],
    "offline_to_load": ["max"],
    "offline_south_mw": ["max"],
    "price_rt_max": ["max"],
    "price_rt_mean": ["mean"],
    "lambda_max": ["max"],
    "lambda_mean": ["mean"],
}
LAGS = (1, 2, 3)


def _date(series, fmt="%m/%d/%Y"):
    return pd.to_datetime(series, format=fmt).dt.date


def _hour(series):
    return series.astype(str).str.split(":").str[0].astype(int)


def _num(df, columns):
    return df[columns].apply(pd.to_numeric, errors="coerce")


def _read_days(folder):
    files = sorted(Path(folder).glob("*.parq"))
    frames = [pd.read_parquet(f) for f in files]
    frames = [f for f in frames if not f.empty]
    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()


def load_hourly(raw):
    df = pd.read_parquet(raw / "load.parq")
    out = pd.DataFrame(
        {
            "oper_day": _date(df["OperDay"]),
            "hour_ending": _hour(df["HourEnding"]),
            "dst_flag": df["DSTFlag"].eq("Y"),
            "load_south_c_mw": pd.to_numeric(df["SOUTH_C"]),
            "load_total_mw": pd.to_numeric(df["TOTAL"]),
            "posted": df["posted"],
        }
    )
    # Keep the latest posting when ERCOT reposts a day.
    out = out.sort_values("posted").drop_duplicates(KEY, keep="last")
    return out.drop(columns="posted")


def forecast_hourly(raw):
    df = _read_days(raw / "forecast")
    df = df[df["InUseFlag"].eq("Y")]
    out = pd.DataFrame(
        {
            "oper_day": _date(df["DeliveryDate"]),
            "hour_ending": _hour(df["HourEnding"]),
            "dst_flag": df["DSTFlag"].eq("Y"),
            "fcst_south_c_mw": pd.to_numeric(df["SouthCentral"]),
            "fcst_total_mw": pd.to_numeric(df["SystemTotal"]),
            "fcst_model": df["Model"],
            "fcst_posted": pd.to_datetime(df["posted"]),
        }
    )
    return out.drop_duplicates(KEY, keep="first")


def outage_cap_hourly(raw):
    """Offline capacity. Before the zonal format, only system totals exist."""
    df = _read_days(raw / "outage_cap")
    zonal = {
        kind: [c for c in df.columns if c.startswith(f"Total{kind}MWZone")]
        for kind in ("Resource", "IRR", "NewEquipResource")
    }
    totals = {}
    for kind, columns in zonal.items():
        # Recent files are zonal only and have no system-total column.
        total = f"Total{kind}MW"
        system = pd.to_numeric(df[total], errors="coerce") if total in df else pd.Series(np.nan, index=df.index)
        by_zone = _num(df, columns).sum(axis=1, min_count=1) if columns else np.nan
        totals[kind] = system.fillna(by_zone) if columns else system
    south = [c for c in ("TotalResourceMWZoneSouth", "TotalIRRMWZoneSouth") if c in df]
    out = pd.DataFrame(
        {
            "oper_day": _date(df["Date"]),
            "hour_ending": _hour(df["HourEnding"]),
            "dst_flag": False,
            "offline_resource_mw": totals["Resource"],
            "offline_irr_mw": totals["IRR"],
            "offline_new_equip_mw": totals["NewEquipResource"],
            "offline_south_mw": _num(df, south).sum(axis=1, min_count=2) if south else np.nan,
        }
    )
    out["offline_mw"] = out["offline_resource_mw"] + out["offline_irr_mw"]
    # The file has no DST flag, so a 25-hour day shows hour 2 twice.
    return out.drop_duplicates(KEY, keep="first")


def price_hourly(raw):
    df = pd.concat([pd.read_parquet(f) for f in sorted((raw / "spp").glob("*.parq"))])
    # Each load zone appears twice: LZ (settlement price) and LZEW
    # (energy-weighted). Keep LZ.
    df = df[df["Settlement Point Type"].eq("LZ")]
    out = pd.DataFrame(
        {
            "oper_day": _date(df["Delivery Date"]),
            "hour_ending": pd.to_numeric(df["Delivery Hour"]).astype(int),
            "dst_flag": df["Repeated Hour Flag"].eq("Y"),
            "interval": pd.to_numeric(df["Delivery Interval"]).astype(int),
            "price": pd.to_numeric(df["Settlement Point Price"]),
        }
    ).drop_duplicates(KEY + ["interval"])
    return out.groupby(KEY, as_index=False).agg(
        price_rt_mean=("price", "mean"),
        price_rt_max=("price", "max"),
        price_intervals=("price", "size"),
    )


def lambda_hourly(raw):
    df = _read_days(raw / "lambda")
    stamps = pd.to_datetime(df["SCEDTimeStamp"], format="%m/%d/%Y %H:%M:%S")
    # Newer files split SystemLambda into capped and uncapped; capped is the old value.
    value = df["SystemLambda"] if "SystemLambda" in df else pd.Series(pd.NA, index=df.index)
    if "CappedSystemLambda" in df:
        value = value.fillna(df["CappedSystemLambda"])
    out = pd.DataFrame(
        {
            "oper_day": stamps.dt.date,
            # A SCED run at 00:05 falls in hour ending 1.
            "hour_ending": stamps.dt.hour + 1,
            "dst_flag": df["RepeatedHourFlag"].eq("Y"),
            "stamp": stamps,
            "lambda": pd.to_numeric(value),
        }
    ).drop_duplicates(["stamp", "dst_flag"])
    return out.groupby(KEY, as_index=False).agg(
        lambda_mean=("lambda", "mean"),
        lambda_max=("lambda", "max"),
        sced_runs=("lambda", "size"),
    )


def hourly_features(raw, days):
    raw = Path(raw)
    frame = load_hourly(raw)
    frame = frame[frame["oper_day"].isin(set(days))]
    for part in (forecast_hourly, outage_cap_hourly, price_hourly, lambda_hourly):
        frame = frame.merge(part(raw), on=KEY, how="left")
    # The outage file has no DST flag; the repeated hour reuses hour 2.
    repeated = frame["dst_flag"]
    if repeated.any():
        cap = outage_cap_hourly(raw).drop(columns="dst_flag")
        fill = frame.loc[repeated, ["oper_day", "hour_ending"]].merge(
            cap, on=["oper_day", "hour_ending"], how="left"
        )
        for col in cap.columns.drop(["oper_day", "hour_ending"]):
            frame.loc[repeated, col] = fill[col].to_numpy()

    for zone, fcst in (("south_c", "fcst_south_c_mw"), ("total", "fcst_total_mw")):
        actual = frame[f"load_{zone}_mw"]
        frame[f"fcst_err_{zone}_mw"] = actual - frame[fcst]
        frame[f"fcst_err_{zone}_pct"] = frame[f"fcst_err_{zone}_mw"] / frame[fcst] * 100
    frame["offline_to_load"] = frame["offline_mw"] / frame["load_total_mw"]
    day = pd.to_datetime(frame["oper_day"])
    frame["month"] = day.dt.month
    frame["day_of_week"] = day.dt.dayofweek
    return frame.sort_values(KEY).reset_index(drop=True)


def daily_features(hourly):
    daily = hourly.groupby("oper_day").agg(**{
        f"{col}_{how}": (col, how) for col, hows in DAILY_AGGS.items() for how in hows
    })
    daily["hours"] = hourly.groupby("oper_day").size()
    full = pd.date_range(min(daily.index), max(daily.index)).date
    by_day = daily.drop(columns="hours").reindex(full)
    for lag in LAGS:
        lagged = by_day.shift(lag).add_suffix(f"_lag{lag}")
        daily = daily.join(lagged)
    daily.index.name = "oper_day"
    return daily.reset_index()


def coverage(hourly):
    """Share of hours with each source present, per day."""
    checks = {
        "load": "load_total_mw",
        "forecast": "fcst_total_mw",
        "outage_cap": "offline_mw",
        "price": "price_rt_max",
        "lambda": "lambda_max",
    }
    present = pd.DataFrame({name: hourly[col].notna() for name, col in checks.items()})
    present["oper_day"] = hourly["oper_day"]
    return present.groupby("oper_day").mean().reset_index()


def build(out, labels):
    from .labels import pull_days

    out = Path(out)
    raw = out / "raw"
    days = pull_days(labels)
    hourly = hourly_features(raw, days)
    daily = daily_features(hourly)
    lag_columns = [c for c in daily.columns if "_lag" in c]

    keep = labels[["oper_day", "label", "source", "event"]]
    hourly_labeled = hourly.merge(keep, on="oper_day").merge(
        daily[["oper_day", *lag_columns]], on="oper_day", how="left"
    )
    daily_labeled = daily.merge(keep, on="oper_day")

    save(hourly_labeled, "hourly", root=out)
    save(daily_labeled, "daily", root=out)
    cov = coverage(hourly)
    save(cov, "coverage", root=out)

    print(f"hourly.parq: {len(hourly_labeled)} rows x {hourly_labeled.shape[1]} columns")
    print(hourly_labeled["label"].value_counts().to_string())
    print(f"daily.parq: {len(daily_labeled)} rows x {daily_labeled.shape[1]} columns")
    missing_days = sorted(set(labels["oper_day"]) - set(daily_labeled["oper_day"]))
    print(f"labeled days with no load data: {len(missing_days)} {missing_days[:10]}")
    gaps = cov[(cov.drop(columns="oper_day") < 1).any(axis=1)]
    print(f"days with an incomplete source: {len(gaps)}")
    if len(gaps):
        print(gaps.drop(columns="oper_day").lt(1).sum().to_string())
