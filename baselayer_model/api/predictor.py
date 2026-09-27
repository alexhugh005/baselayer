"""Score one operating day's hourly feature rows."""

from __future__ import annotations

import numpy as np
import pandas as pd

from baselayer_model.austin_outage import modeling as m

# Missing in training too, so their absence doesn't make a result degraded:
# lambda archive gaps left 29.5% of training rows null, and the forecast ramp
# is a diff, so hour ending 1 is always null.
def _expected_gap(column):
    return column.startswith("lambda_") or column == "fcst_total_mw_ramp"


def score_day(hourly, run):
    """Risk for the single oper_day in `hourly` (one row per hour).

    `hourly` needs oper_day plus the raw day_ahead columns; derived features
    are added here and absent features are passed to XGBoost as missing.
    """
    days = hourly["oper_day"].unique()
    if len(days) != 1:
        raise ValueError(f"expected one oper_day, got {len(days)}")
    df = m.add_derived_features(hourly).reset_index(drop=True)

    X = df.reindex(columns=run.features).astype(float)
    for column in run.int_features:
        X[column] = X[column].astype("int32")
    missing = [c for c in run.features if X[c].isna().all()]

    proba = run.model.predict_proba(X)
    daily = m.daily_probs(df.assign(label=None), proba)
    risk = m.CLASSES[int(m.apply_thresholds(daily, run.thresholds)[0])]
    day = daily.iloc[0]

    # The model scores each hour; its training label was the hour's day class.
    # Hours get the same cascade as the day (thresholds were tuned on daily means).
    hourly = pd.DataFrame(proba, columns=["p_low", "p_medium", "p_high"])
    hourly["p_elevated"] = hourly["p_medium"] + hourly["p_high"]
    hourly["risk"] = [m.CLASSES[i] for i in m.apply_thresholds(hourly, run.thresholds)]
    hourly["hour_ending"] = df["hour_ending"].to_numpy() if "hour_ending" in df else np.arange(1, len(df) + 1)
    hourly["dst_flag"] = df["dst_flag"].astype(bool).to_numpy() if "dst_flag" in df else False
    return {
        "oper_day": str(days[0]),
        "risk": risk,
        "p_low": float(day["p_low"]),
        "p_medium": float(day["p_medium"]),
        "p_high": float(day["p_high"]),
        "p_elevated": float(day["p_elevated"]),
        "thresholds": run.thresholds["thresholds"],
        "degraded": any(not _expected_gap(c) for c in missing),
        "missing_features": missing,
        "model_version": run.version,
        "hourly": [
            {
                "hour_ending": int(r.hour_ending),
                "dst_flag": bool(r.dst_flag),
                "risk": r.risk,
                "p_low": float(r.p_low),
                "p_medium": float(r.p_medium),
                "p_high": float(r.p_high),
                "p_elevated": float(r.p_elevated),
            }
            for r in hourly.itertuples()
        ],
    }
