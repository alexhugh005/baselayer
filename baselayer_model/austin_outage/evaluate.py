"""Score a trained run on its held-out test split.

    python -m baselayer_model.austin_outage.evaluate --run-dir /tmp/austin_outage_run_xxxx

Loads model.json, classification_thresholds.json and split.json from the run
directory, keeps only the test days that train.py held out, and reports
hourly and daily metrics (argmax and tuned thresholds) plus a per-event
breakdown. Writes test_metrics.json and test_daily_predictions.csv.
"""

from __future__ import annotations

import argparse
import json
import logging
from pathlib import Path

import pandas as pd
import xgboost as xgb

from baselayer_model.austin_outage import modeling as m

log = logging.getLogger("baselayer_model.austin_outage.evaluate")


def parse_args(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run-dir", required=True, help="directory written by the train script")
    p.add_argument("--data", help="hourly feature table (default: the one used for training)")
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    run = Path(args.run_dir)
    config = json.loads((run / "run_config.json").read_text())
    split = json.loads((run / m.SPLIT_FILE).read_text())
    thresholds = json.loads((run / m.THRESHOLDS_FILE).read_text())

    df = m.load_table(args.data or config["data"])
    test_days = set(pd.to_datetime(split["test_days"]).date)
    test = df[df["oper_day"].isin(test_days)].reset_index(drop=True)
    if test["oper_day"].nunique() != len(test_days):
        log.warning("only %d of %d test days found in the data", test["oper_day"].nunique(), len(test_days))
    log.info("test split: %d rows, %d days", len(test), test["oper_day"].nunique())

    missing = set(config["features"]) - set(test.columns)
    if missing:
        raise ValueError(f"data is missing trained features: {sorted(missing)}")

    model = xgb.XGBClassifier()
    model.load_model(run / m.MODEL_FILE)
    proba = model.predict_proba(test[config["features"]])
    y = test["label"].map(m.CLASS_TO_ID).to_numpy()

    daily = m.daily_probs(test, proba)
    dy, dproba = daily["y"].to_numpy(), m.daily_proba_matrix(daily)
    daily["pred_argmax"] = dproba.argmax(1)
    daily["pred"] = m.apply_thresholds(daily, thresholds)

    report = {
        "hourly_argmax": m.classification_metrics(y, proba.argmax(1), proba),
        "daily_argmax": m.classification_metrics(dy, daily["pred_argmax"].to_numpy(), dproba),
        "daily_thresholds": m.classification_metrics(dy, daily["pred"].to_numpy(), dproba),
        "thresholds": thresholds["thresholds"],
    }
    for name in ("hourly_argmax", "daily_argmax", "daily_thresholds"):
        log.info("\n%s", m.format_metrics(f"TEST {name}", report[name]))

    # Per-event view: did the held-out storm get flagged, and when?
    group_of = test.drop_duplicates("oper_day").set_index("oper_day")["group"]
    daily["group"] = daily["oper_day"].map(group_of)
    events = {}
    for group, rows in daily[~daily["group"].str.startswith("episode_")].groupby("group"):
        hi = rows[rows["y"] == m.HIGH]
        events[group] = {
            "high_days": int(len(hi)),
            "high_days_flagged_high": int((hi["pred"] == m.HIGH).sum()),
            "high_days_flagged_elevated": int((hi["pred"] >= m.MEDIUM).sum()),
            "false_alarm_days_in_window": int(((rows["y"] == m.LOW) & (rows["pred"] >= m.MEDIUM)).sum()),
            "max_p_high": float(hi["p_high"].max()) if len(hi) else None,
        }
        log.info("event %s: %s", group, events[group])
    report["events"] = events

    daily["pred_label"] = daily["pred"].map(dict(enumerate(m.CLASSES)))
    daily.drop(columns=["group"]).to_csv(run / "test_daily_predictions.csv", index=False)
    m.write_json(run / "test_metrics.json", report)
    log.info("wrote %s and %s", run / "test_metrics.json", run / "test_daily_predictions.csv")
    return report


if __name__ == "__main__":
    main()
