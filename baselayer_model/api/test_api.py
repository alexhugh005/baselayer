"""Tests for the model API.

    .venv/bin/python -m unittest baselayer_model.api.test_api

The golden and parity tests need the local dataset (skipped without it):
$DATA_ROOT/dataset/austin-outage/{hourly.parq,raw/} and the run's
split.json and test_daily_predictions.csv in $DATA_ROOT/dataset/model/.
"""

import datetime as dt
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import numpy as np
import pandas as pd
from fastapi.testclient import TestClient

from baselayer_data.paths import dataset_root
from baselayer_model.api import live_features
from baselayer_model.api.app import CENTRAL, create_app
from baselayer_model.api.artifacts import DEFAULT_DIR, load_run
from baselayer_model.api.predictor import score_day
from baselayer_model.austin_outage import modeling as m

DATA = dataset_root() / "austin-outage"
RUN_OUTPUT = dataset_root() / "model"
# The local lambda pull ends here; later test days were scored at training
# time with lambda data this checkout doesn't have.
LAMBDA_THROUGH = dt.date(2024, 7, 10)


def empty_day(day, hours=24):
    """Hourly rows with only calendar columns, so every other feature is missing."""
    return pd.DataFrame({
        "oper_day": [day] * hours,
        "hour_ending": range(1, hours + 1),
        "dst_flag": False,
        "month": day.month,
        "day_of_week": day.weekday(),
    })


class ArtifactTests(unittest.TestCase):
    def copy_run(self, **config_changes):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp)
        for name in ("model.json", "run_config.json", m.THRESHOLDS_FILE):
            shutil.copy(DEFAULT_DIR / name, tmp / name)
        config = json.loads((tmp / "run_config.json").read_text())
        config.update(config_changes)
        (tmp / "run_config.json").write_text(json.dumps(config))
        return tmp

    def test_loads_bundled_run(self):
        run = load_run()
        self.assertEqual(len(run.features), 69)
        self.assertEqual(run.int_features, {"month", "day_of_week"})
        self.assertTrue(run.version.startswith("20260926-2006-"))

    def test_rejects_nowcast_run(self):
        with self.assertRaisesRegex(ValueError, "day_ahead"):
            load_run(self.copy_run(feature_set="nowcast"))

    def test_rejects_feature_mismatch(self):
        features = load_run().features
        with self.assertRaisesRegex(ValueError, "feature names"):
            load_run(self.copy_run(features=features[::-1]))


class ScoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model_run = load_run()

    def test_result_shape(self):
        out = score_day(empty_day(dt.date(2026, 2, 16)), self.model_run)
        self.assertIn(out["risk"], m.CLASSES)
        self.assertAlmostEqual(out["p_low"] + out["p_medium"] + out["p_high"], 1, places=5)
        self.assertAlmostEqual(out["p_elevated"], out["p_medium"] + out["p_high"], places=6)
        self.assertEqual(len(out["hourly"]), 24)
        self.assertEqual([h["hour_ending"] for h in out["hourly"]], list(range(1, 25)))
        for hour in out["hourly"]:
            self.assertIn(hour["risk"], m.CLASSES)
            self.assertAlmostEqual(hour["p_low"] + hour["p_elevated"], 1, places=5)
        self.assertTrue(out["degraded"])
        self.assertIn("fcst_total_mw", out["missing_features"])

    def test_hourly_risk_uses_the_threshold_cascade(self):
        out = score_day(empty_day(dt.date(2026, 2, 16)), self.model_run)
        t = out["thresholds"]
        for hour in out["hourly"]:
            expected = ("high" if hour["p_high"] >= t["high"]
                        else "medium" if hour["p_elevated"] >= t["medium"] else "low")
            self.assertEqual(hour["risk"], expected)
        # The day is the mean of its hours.
        self.assertAlmostEqual(out["p_high"], np.mean([h["p_high"] for h in out["hourly"]]), places=6)

    def test_rejects_multiple_days(self):
        rows = pd.concat([empty_day(dt.date(2026, 2, 16)), empty_day(dt.date(2026, 2, 17))])
        with self.assertRaises(ValueError):
            score_day(rows, self.model_run)


@unittest.skipUnless((DATA / "hourly.parq").exists() and (RUN_OUTPUT / "split.json").exists(),
                     "needs the austin-outage dataset and run output")
class GoldenTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model_run = load_run()
        cls.table = m.load_table(DATA / "hourly.parq")
        split = json.loads((RUN_OUTPUT / "split.json").read_text())
        cls.test_days = pd.to_datetime(split["test_days"]).date
        cls.saved = pd.read_csv(RUN_OUTPUT / "test_daily_predictions.csv", parse_dates=["oper_day"])

    def day_rows(self, day):
        return self.table[self.table["oper_day"] == day].drop(columns=list(m.DERIVED))

    def test_matches_evaluate_on_every_test_day(self):
        for day in self.test_days:
            rows = self.table[self.table["oper_day"] == day]
            expected = m.daily_probs(rows, self.model_run.model.predict_proba(rows[self.model_run.features])).iloc[0]
            out = score_day(self.day_rows(day), self.model_run)
            for col in ("p_low", "p_medium", "p_high"):
                self.assertAlmostEqual(out[col], expected[col], places=6, msg=f"{day} {col}")

    def test_reproduces_saved_predictions(self):
        saved = self.saved[self.saved["oper_day"].dt.date <= LAMBDA_THROUGH]
        self.assertGreater(len(saved), 20)
        for row in saved.itertuples():
            out = score_day(self.day_rows(row.oper_day.date()), self.model_run)
            np.testing.assert_allclose(
                [out["p_low"], out["p_medium"], out["p_high"]], [row.p_low, row.p_medium, row.p_high],
                atol=1e-6, err_msg=str(row.oper_day.date()),
            )
            self.assertEqual(out["risk"], row.pred_label, row.oper_day.date())


@unittest.skipUnless((DATA / "hourly.parq").exists() and (DATA / "raw" / "load.parq").exists(),
                     "needs the austin-outage dataset and raw pulls")
class FeatureParityTests(unittest.TestCase):
    """build_day on the training raw pulls reproduces the training table."""

    def test_matches_training_rows(self):
        run = load_run()
        table = m.load_table(DATA / "hourly.parq")
        keys = ["hour_ending", "dst_flag"]
        # Uri (price and forecast error extremes) and a recent summer day.
        for day in (dt.date(2021, 2, 16), dt.date(2026, 8, 10)):
            built = m.add_derived_features(live_features.build_day(day, DATA / "raw"))
            expected = table[table["oper_day"] == day].set_index(keys)[run.features]
            actual = built.set_index(keys).reindex(expected.index)[run.features]
            pd.testing.assert_frame_equal(actual, expected, check_dtype=False, rtol=1e-9, obj=str(day))


class AppTests(unittest.TestCase):
    TODAY = dt.date(2026, 9, 26)

    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.root)
        self.builds = []
        self.fail = False
        self.now = dt.datetime.combine(self.TODAY, dt.time(8, 0), CENTRAL)

    def build(self, day, raw):
        self.builds.append(day)
        if self.fail:
            raise live_features.MissingForecast(f"no day-ahead load forecast posted for {day}")
        return empty_day(day)

    def client(self):
        app = create_app(root=self.root, fetch=lambda raw, day: {}, build=self.build,
                         clock=lambda: self.now, refresh_seconds=0)
        client = TestClient(app)
        client.__enter__()
        self.addCleanup(client.__exit__, None, None, None)
        return client

    def test_health(self):
        body = self.client().get("/health").json()
        self.assertEqual(body["status"], "ok")
        self.assertEqual(body["features"], 69)

    def test_scores_today_by_default(self):
        body = self.client().get("/v1/outage-risk").json()
        self.assertEqual(body["oper_day"], str(self.TODAY))
        self.assertIn(body["risk"], m.CLASSES)
        self.assertTrue((self.root / "results" / f"{self.TODAY}.json").exists())
        self.assertTrue((self.root / "features" / f"{self.TODAY}.parq").exists())

    def test_caches_until_retry(self):
        client = self.client()
        client.get("/v1/outage-risk")
        client.get("/v1/outage-risk")
        self.assertEqual(len(self.builds), 1)
        # Degraded results (these rows have no features) are retried after an hour.
        self.now += dt.timedelta(hours=2)
        client.get("/v1/outage-risk")
        self.assertEqual(len(self.builds), 2)

    def test_recomputes_results_cached_in_an_older_shape(self):
        path = self.root / "results" / f"{self.TODAY}.json"
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps({"model_version": load_run().version, "degraded": False,
                                    "generated_at": self.now.isoformat(), "hourly": []}))
        body = self.client().get("/v1/outage-risk").json()
        self.assertEqual(len(self.builds), 1)
        self.assertEqual(len(body["hourly"]), 24)

    def test_serves_stale_result_when_pull_fails(self):
        client = self.client()
        client.get("/v1/outage-risk")
        self.fail = True
        self.now += dt.timedelta(hours=2)
        body = client.get("/v1/outage-risk").json()
        self.assertTrue(body["stale"])

    def test_503_without_forecast(self):
        self.fail = True
        response = self.client().get("/v1/outage-risk", params={"day": "2026-09-27"})
        self.assertEqual(response.status_code, 503)
        self.assertIn("forecast", response.json()["detail"])

    def test_rejects_days_past_tomorrow(self):
        response = self.client().get("/v1/outage-risk", params={"day": "2026-09-28"})
        self.assertEqual(response.status_code, 422)


if __name__ == "__main__":
    unittest.main()
