"""Write demo fixtures from real hours of the training table.

    .venv/bin/python -m baselayer_model.api.demo.make_demo

- demo_cases.json: hand-picked hour sequences (low -> medium -> high ...)
  for the demo. Each hour is a real row of hourly.parq, scored by the
  served model; hours come from different days, since the model's hourly
  risk barely changes within a day.
- test_set_outages.json: every medium and high day of the held-out test
  split (never seen in training), hour by hour, with the model's output.

Every hour keeps its source (oper_day, hour_ending, true label, split) and
the 69 feature values in model order, so `run.model.predict_proba` on them
reproduces the stored probabilities.
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path

import pandas as pd

from baselayer_data.paths import dataset_root
from baselayer_model.api.artifacts import load_run
from baselayer_model.api.predictor import feature_matrix, score_hours
from baselayer_model.austin_outage import modeling as m

OUT = Path(__file__).resolve().parent
DATA = dataset_root() / "austin-outage" / "hourly.parq"
SPLIT = dataset_root() / "model" / "split.json"

# (description, [(source oper_day, hour_ending, expected risk), ...]).
# Each hour keeps its real hour of day.
CASES = {
    "case_1": (
        "Low, rising to medium, then easing: June 2021 heat near-miss (ERCOT "
        "conservation appeal). All hours are from the held-out test split.",
        [
            ("2021-12-27", 14, "low"),
            ("2021-12-27", 15, "low"),
            ("2021-06-14", 16, "medium"),
            ("2021-06-14", 17, "medium"),
            ("2022-01-11", 18, "low"),
        ],
    ),
    "case_2": (
        "Winter Storm Uri timeline: quiet days, the near-miss day before, the "
        "first load-shed day, and recovery. Training-split hours (no test "
        "hour scores high).",
        [
            ("2021-02-12", 13, "low"),
            ("2021-02-12", 14, "low"),
            ("2021-02-13", 15, "low"),
            ("2021-02-14", 16, "medium"),
            ("2021-02-15", 17, "high"),
            ("2021-02-21", 18, "low"),
        ],
    ),
}


def _num(value):
    return None if pd.isna(value) else float(value)


def score_table(run):
    df = m.load_table(DATA).reset_index(drop=True)
    return df, score_hours(feature_matrix(df, run), run)


def hour_record(df, scored, i, run, test_days, hour_ending=None):
    row, s = df.loc[i], scored.loc[i]
    return {
        "hour_ending": int(hour_ending if hour_ending is not None else row["hour_ending"]),
        "risk": s["risk"],
        "p_low": float(s["p_low"]),
        "p_medium": float(s["p_medium"]),
        "p_high": float(s["p_high"]),
        "p_elevated": float(s["p_elevated"]),
        "source": {
            "oper_day": str(row["oper_day"]),
            "hour_ending": int(row["hour_ending"]),
            "label": row["label"],
            "event": row["event"] if isinstance(row["event"], str) and row["event"] else None,
            "split": "test" if row["oper_day"] in test_days else "train",
        },
        "features": {c: _num(row[c]) for c in run.features},
    }


def main():
    run = load_run()
    df, scored = score_table(run)
    test_days = set(pd.to_datetime(json.loads(SPLIT.read_text())["test_days"]).date)
    header = {
        "model_version": run.version,
        "thresholds": run.thresholds["thresholds"],
        "decision_rule": run.thresholds["decision_rule"],
    }

    cases = {}
    for name, (description, hours) in CASES.items():
        records = []
        for day, hour, expected in hours:
            match = df.index[(df["oper_day"] == dt.date.fromisoformat(day)) & (df["hour_ending"] == hour)]
            if len(match) != 1:
                raise ValueError(f"{name}: {day} HE{hour} not found once in {DATA}")
            record = hour_record(df, scored, match[0], run, test_days)
            if record["risk"] != expected:
                raise ValueError(f"{name}: {day} HE{hour} scores {record['risk']}, expected {expected}")
            records.append(record)
        cases[name] = {
            "description": description,
            "sequence": [r["risk"] for r in records],
            "hourly": records,
        }
    (OUT / "demo_cases.json").write_text(json.dumps({**header, "cases": cases}, indent=2) + "\n")

    outage_days = []
    test = df[df["oper_day"].isin(test_days) & df["label"].isin(["medium", "high"])]
    for day, rows in test.groupby("oper_day"):
        idx = rows.index
        daily = scored.loc[idx, ["p_low", "p_medium", "p_high"]].mean()
        daily_frame = pd.DataFrame([{**daily, "p_elevated": daily["p_medium"] + daily["p_high"]}])
        risk = m.CLASSES[int(m.apply_thresholds(daily_frame, run.thresholds)[0])]
        label = rows["label"].iloc[0]
        outage_days.append({
            "oper_day": str(day),
            "label": label,
            "event": rows["event"].iloc[0] if isinstance(rows["event"].iloc[0], str) else None,
            "risk": risk,
            "correct": risk == label,
            "p_low": float(daily["p_low"]),
            "p_medium": float(daily["p_medium"]),
            "p_high": float(daily["p_high"]),
            "p_elevated": float(daily_frame["p_elevated"].iloc[0]),
            "hourly": [hour_record(df, scored, i, run, test_days) for i in idx],
        })
    (OUT / "test_set_outages.json").write_text(json.dumps({
        **header,
        "note": "Medium and high days of the held-out test split; the model never trained on them.",
        "days": outage_days,
    }, indent=2) + "\n")

    for name, case in cases.items():
        print(name, case["sequence"])
    for day in outage_days:
        print(day["oper_day"], day["label"], "->", day["risk"], f"p_high={day['p_high']:.3f}")


if __name__ == "__main__":
    main()
