"""Demo cases: real hours from hourly.parq, scored live by the served model.

demo_cases.json stores each hour's source and its 69 feature values (built by
make_demo.py); score_case runs them through the loaded model on every call.
"""

from __future__ import annotations

import functools
import json
from pathlib import Path

import pandas as pd

from baselayer_model.api.predictor import feature_matrix, score_hours

CASES_FILE = Path(__file__).resolve().parent / "demo_cases.json"


@functools.cache
def load_cases():
    return json.loads(CASES_FILE.read_text())["cases"]


def score_case(name, run, include_features=False):
    case = load_cases()[name]
    X = feature_matrix(pd.DataFrame([h["features"] for h in case["hourly"]]), run)
    hours = []
    for stored, s in zip(case["hourly"], score_hours(X, run).itertuples()):
        hour = {
            "hour_ending": stored["hour_ending"],
            "risk": s.risk,
            "p_low": float(s.p_low),
            "p_medium": float(s.p_medium),
            "p_high": float(s.p_high),
            "p_elevated": float(s.p_elevated),
            "source": stored["source"],
        }
        if include_features:
            hour["features"] = stored["features"]
        hours.append(hour)
    return {
        "case": name,
        "description": case["description"],
        "model_version": run.version,
        "thresholds": run.thresholds["thresholds"],
        "sequence": [h["risk"] for h in hours],
        "hourly": hours,
    }
