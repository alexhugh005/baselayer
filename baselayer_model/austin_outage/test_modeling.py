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
                # same-day actuals (nowcast only)
                "load_south_c_mw": 9000 + 1500 * s + rng.normal(0, 1200),
                "fcst_err_south_c_pct": 4 * s + rng.normal(0, 3),
                "offline_to_load": 0.1 + 0.05 * s + rng.normal(0, 0.05),
                "price_rt_max": np.exp(3 + 0.8 * s + rng.normal(0, 0.8)),
                # known before the day starts
                "fcst_total_mw": 50000 + 6000 * s + rng.normal(0, 4000),
                "offline_mw": 15000 + 4000 * s + rng.normal(0, 3000),
                "load_total_mw_max_lag1": 55000 + 2000 * s + rng.normal(0, 4000),
                "price_rt_max_max_lag1": np.exp(4 + 0.8 * s + rng.normal(0, 0.8)),
                # never features
                "offline_new_equip_mw": 4000.0,
                "offline_south_mw": np.nan if day.oper_day.year < 2022 else 5000.0,
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

    def test_day_ahead_features_skip_same_day_actuals(self):
        self.assertEqual(
            m.feature_columns(self.df),
            [
                "fcst_total_mw",
                "offline_mw",
                "load_total_mw_max_lag1",
                "price_rt_max_max_lag1",
                "fcst_total_mw_day_max",
                "fcst_total_mw_ramp",
                "offline_to_fcst",
                "fcst_peak_vs_lag1_pct",
            ],
        )

    def test_nowcast_adds_same_day_actuals(self):
        nowcast = m.feature_columns(self.df, feature_set="nowcast")
        extra = set(nowcast) - set(m.feature_columns(self.df))
        self.assertEqual(
            extra, {"load_south_c_mw", "fcst_err_south_c_pct", "offline_to_load", "price_rt_max"}
        )

    def test_misleading_columns_are_never_features(self):
        for feature_set in m.FEATURE_SETS:
            cols = m.feature_columns(self.df, feature_set=feature_set)
            self.assertNotIn("offline_new_equip_mw", cols)
            self.assertNotIn("offline_south_mw", cols)
            self.assertNotIn("sced_runs", cols)

    def test_derived_features_use_whole_day_forecast(self):
        day = self.df[self.df["oper_day"] == self.df["oper_day"].iloc[0]]
        self.assertTrue((day["fcst_total_mw_day_max"] == day["fcst_total_mw"].max()).all())
        expected = (day["fcst_total_mw_day_max"] / day["load_total_mw_max_lag1"] - 1) * 100
        self.assertTrue(np.allclose(day["fcst_peak_vs_lag1_pct"], expected))

    def test_derived_features_skip_missing_inputs(self):
        slim = m.add_derived_features(self.df[["oper_day", "hour_ending", "label", "event", "fcst_total_mw"]])
        self.assertIn("fcst_total_mw_day_max", slim.columns)
        self.assertNotIn("offline_to_fcst", slim.columns)

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
