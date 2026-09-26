import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
import pandas as pd

from baselayer_data.austin_outage.labels import build_labels
from baselayer_model.austin_outage import modeling as m

SIGNAL = {"low": 0.0, "medium": 1.0, "high": 2.0}


def synthetic_hourly(seed=0):
    """Real label days with made-up hourly features that separate the classes."""
    rng = np.random.default_rng(seed)
    labels = build_labels()
    rows = []
    for day in labels.itertuples():
        s = SIGNAL[day.label]
        for hour in range(1, 25):
            rows.append({
                "oper_day": day.oper_day,
                "hour_ending": hour,
                "dst_flag": False,
                "label": day.label,
                "source": day.source,
                "event": day.event or "",
                "load_south_c_mw": 9000 + 1500 * s + rng.normal(0, 1200),
                "fcst_err_south_c_pct": 4 * s + rng.normal(0, 3),
                "offline_to_load": 0.1 + 0.05 * s + rng.normal(0, 0.05),
                "price_rt_max": np.exp(3 + 0.8 * s + rng.normal(0, 0.8)),
                "fcst_model": "E",
                "sced_runs": 12,
            })
    return pd.DataFrame(rows)


class ModelingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.path = Path(cls.tmp.name) / "hourly.parq"
        synthetic_hourly().to_parquet(cls.path, index=False)
        cls.df = m.load_table(cls.path)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_features_skip_meta_text_and_coverage_columns(self):
        self.assertEqual(
            m.feature_columns(self.df),
            ["load_south_c_mw", "fcst_err_south_c_pct", "offline_to_load", "price_rt_max"],
        )

    def test_each_day_belongs_to_one_group(self):
        self.assertTrue((self.df.groupby("oper_day")["group"].nunique() == 1).all())
        self.assertEqual(
            set(self.df.loc[self.df["label"] == "high", "group"]),
            {"uri_2021", "mara_2023", "microburst_2025"},
        )

    def test_test_split_holds_out_latest_event_whole(self):
        test = m.make_test_split(self.df, seed=1)
        self.assertIn("microburst_2025", test)
        self.assertNotIn("uri_2021", test)
        test_days = set(self.df.loc[self.df["group"].isin(test), "oper_day"])
        train_days = set(self.df.loc[~self.df["group"].isin(test), "oper_day"])
        self.assertFalse(test_days & train_days)

    def test_cv_folds_leave_one_event_out(self):
        dev = self.df[~self.df["group"].isin(m.make_test_split(self.df, seed=1))].reset_index(drop=True)
        folds = m.make_cv_folds(dev, seed=1)
        self.assertEqual(len(folds), 2)
        for train_idx, valid_idx in folds:
            train_groups = set(dev["group"].iloc[train_idx])
            valid_groups = set(dev["group"].iloc[valid_idx])
            self.assertFalse(train_groups & valid_groups)
            self.assertEqual(len(valid_groups & {"uri_2021", "mara_2023"}), 1)
            self.assertEqual(set(dev["label"].iloc[train_idx]), set(m.CLASSES))

    def test_daily_probs_average_hours(self):
        day = self.df[self.df["oper_day"] == self.df["oper_day"].iloc[0]]
        proba = np.tile([0.5, 0.3, 0.2], (len(day), 1))
        proba[0] = [0.0, 0.0, 1.0]
        daily = m.daily_probs(day, proba)
        self.assertEqual(len(daily), 1)
        self.assertAlmostEqual(daily[["p_low", "p_medium", "p_high"]].sum(axis=1).iloc[0], 1.0)
        self.assertAlmostEqual(daily["p_high"].iloc[0], (0.2 * 23 + 1.0) / 24)

    def test_thresholds_cascade(self):
        daily = pd.DataFrame({
            "y": [0, 0, 1, 1, 2, 2],
            "p_high": [0.01, 0.05, 0.10, 0.20, 0.70, 0.90],
            "p_elevated": [0.05, 0.20, 0.60, 0.70, 0.95, 0.99],
        })
        fitted = m.fit_thresholds(daily)
        t = fitted["thresholds"]
        self.assertTrue(0.20 < t["high"] <= 0.70)
        self.assertTrue(0.20 < t["medium"] <= 0.60)
        self.assertAlmostEqual(t["low"] + t["medium"], 1.0)
        self.assertEqual(m.apply_thresholds(daily, fitted).tolist(), [0, 0, 1, 1, 2, 2])


class TrainEvaluateTests(unittest.TestCase):
    """End-to-end run on synthetic data; skipped when xgboost can't load."""

    def test_train_then_evaluate(self):
        try:
            import xgboost  # noqa: F401
        except Exception as exc:  # missing package or OpenMP runtime
            self.skipTest(f"xgboost unavailable: {exc}")
        from baselayer_model.austin_outage import evaluate, train

        with tempfile.TemporaryDirectory() as tmp:
            data = Path(tmp) / "hourly.parq"
            synthetic_hourly().to_parquet(data, index=False)
            out = Path(tmp) / "run"
            train.main([
                "--data", str(data), "--out-dir", str(out), "--device", "cpu",
                "--n-jobs", "1", "--n-estimators", "200", "--early-stopping", "20",
                "--log-every", "0",
            ])
            doc = json.loads((out / m.THRESHOLDS_FILE).read_text())
            self.assertEqual(set(doc["thresholds"]), set(m.CLASSES))

            split = json.loads((out / m.SPLIT_FILE).read_text())
            oof_days = set(pd.read_csv(out / "oof_daily_predictions.csv")["oper_day"])
            self.assertFalse(oof_days & set(split["test_days"]))

            report = evaluate.main(["--run-dir", str(out)])
            self.assertEqual(report["daily_thresholds"]["n"], len(split["test_days"]))
            self.assertIn("microburst_2025", report["events"])


if __name__ == "__main__":
    unittest.main()
