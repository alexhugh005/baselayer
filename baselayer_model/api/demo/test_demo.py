"""Functional tests for the demo routes, through HTTP with the real served model.

    .venv/bin/python -m unittest baselayer_model.api.demo.test_demo

By default the tests start the app in-process (FastAPI TestClient, full
startup, the model in baselayer_model/api/model). Set DEMO_API_URL to run
the same assertions against a running service instead, e.g. the container:

    DEMO_API_URL=http://127.0.0.1:8000 .venv/bin/python -m unittest baselayer_model.api.demo.test_demo

The expected values below are written out here, not read from
demo_cases.json, so a changed model or fixture file fails the suite.
"""

from __future__ import annotations

import json
import os
import unittest
from collections import namedtuple
from pathlib import Path

import httpx
import numpy as np
import pandas as pd
from fastapi.testclient import TestClient

from baselayer_model.api.app import create_app
from baselayer_model.api.artifacts import load_run
from baselayer_model.api.predictor import feature_matrix, score_hours

MODEL_VERSION = "20260926-2006-ef94496938cc"
THRESHOLDS = {"low": 0.426848, "medium": 0.573152, "high": 0.377967}

Hour = namedtuple("Hour", "hour_ending risk p_low p_medium p_high oper_day label event split")

# Per hour: the model's expected call and probabilities, and the real hour it comes from.
EXPECTED = {
    "case-1": [
        Hour(14, "low", 0.5718, 0.2145, 0.2137, "2021-12-27", "low", None, "test"),
        Hour(15, "low", 0.5718, 0.2145, 0.2137, "2021-12-27", "low", None, "test"),
        Hour(16, "medium", 0.3422, 0.3293, 0.3285, "2021-06-14", "medium", None, "test"),
        Hour(17, "medium", 0.3422, 0.3293, 0.3285, "2021-06-14", "medium", None, "test"),
        Hour(18, "low", 0.5720, 0.2142, 0.2137, "2022-01-11", "low", None, "test"),
    ],
    "case-2": [
        Hour(13, "low", 0.5557, 0.2222, 0.2221, "2021-02-12", "low", "uri_2021", "train"),
        Hour(14, "low", 0.5557, 0.2222, 0.2221, "2021-02-12", "low", "uri_2021", "train"),
        Hour(15, "low", 0.5333, 0.2534, 0.2133, "2021-02-13", "low", "uri_2021", "train"),
        Hour(16, "medium", 0.2185, 0.5670, 0.2146, "2021-02-14", "medium", "uri_2021", "train"),
        Hour(17, "high", 0.2174, 0.2131, 0.5696, "2021-02-15", "high", "uri_2021", "train"),
        Hour(18, "low", 0.5720, 0.2142, 0.2138, "2021-02-21", "low", "uri_2021", "train"),
    ],
}


class DemoRouteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        url = os.environ.get("DEMO_API_URL")
        if url:
            cls.client = httpx.Client(base_url=url, timeout=30)
            cls.addClassCleanup(cls.client.close)
            return

        def no_ercot(raw, day):
            raise AssertionError("demo routes must not pull ERCOT data")

        cls.client = TestClient(create_app(fetch=no_ercot, refresh_seconds=0))
        cls.client.__enter__()
        cls.addClassCleanup(cls.client.__exit__, None, None, None)

    def get_case(self, case, **params):
        response = self.client.get(f"/v1/demo/{case}", params=params)
        self.assertEqual(response.status_code, 200, response.text)
        return response.json()

    def test_each_hour_matches_expected_outcome(self):
        for case, expected in EXPECTED.items():
            hours = self.get_case(case)["hourly"]
            self.assertEqual(len(hours), len(expected), case)
            for got, want in zip(hours, expected):
                with self.subTest(case=case, hour_ending=want.hour_ending):
                    self.assertEqual(got["hour_ending"], want.hour_ending)
                    self.assertEqual(got["risk"], want.risk)
                    self.assertAlmostEqual(got["p_low"], want.p_low, places=3)
                    self.assertAlmostEqual(got["p_medium"], want.p_medium, places=3)
                    self.assertAlmostEqual(got["p_high"], want.p_high, places=3)

    def test_each_hour_names_its_real_source(self):
        for case, expected in EXPECTED.items():
            for got, want in zip(self.get_case(case)["hourly"], expected):
                with self.subTest(case=case, hour_ending=want.hour_ending):
                    source = got["source"]
                    self.assertEqual(source["oper_day"], want.oper_day)
                    self.assertEqual(source["hour_ending"], want.hour_ending)
                    self.assertEqual(source["label"], want.label)
                    self.assertEqual(source.get("event"), want.event)
                    self.assertEqual(source["split"], want.split)
                    # Every demo hour is one the model gets right.
                    self.assertEqual(got["risk"], want.label)

    def test_sequence_lists_hourly_risks_in_order(self):
        self.assertEqual(self.get_case("case-1")["sequence"], ["low", "low", "medium", "medium", "low"])
        self.assertEqual(self.get_case("case-2")["sequence"], ["low", "low", "low", "medium", "high", "low"])

    def test_case_metadata(self):
        for case in EXPECTED:
            body = self.get_case(case)
            with self.subTest(case=case):
                self.assertEqual(body["case"], case.replace("-", "_"))
                self.assertEqual(body["model_version"], MODEL_VERSION)
                self.assertEqual(body["thresholds"], THRESHOLDS)
                self.assertTrue(body["description"])

    def test_risk_follows_threshold_cascade(self):
        for case in EXPECTED:
            for hour in self.get_case(case)["hourly"]:
                with self.subTest(case=case, hour_ending=hour["hour_ending"]):
                    self.assertAlmostEqual(hour["p_low"] + hour["p_medium"] + hour["p_high"], 1, places=5)
                    self.assertAlmostEqual(hour["p_elevated"], hour["p_medium"] + hour["p_high"], places=6)
                    expected = ("high" if hour["p_high"] >= THRESHOLDS["high"]
                                else "medium" if hour["p_elevated"] >= THRESHOLDS["medium"] else "low")
                    self.assertEqual(hour["risk"], expected)

    def test_features_only_on_request(self):
        for case, expected in EXPECTED.items():
            with self.subTest(case=case):
                self.assertTrue(all("features" not in h for h in self.get_case(case)["hourly"]))
                hours = self.get_case(case, include_features="true")["hourly"]
                self.assertEqual(len(hours), len(expected))
                for hour in hours:
                    self.assertEqual(len(hour["features"]), 69)
                    self.assertEqual(hour["features"]["month"], int(hour["source"]["oper_day"][5:7]))

    def test_unknown_case_is_404(self):
        self.assertEqual(self.client.get("/v1/demo/case-3").status_code, 404)


class DemoFixtureTests(unittest.TestCase):
    """The committed demo files still match what the served model says."""

    def test_stored_features_reproduce_stored_scores(self):
        run = load_run()
        here = Path(__file__).resolve().parent
        cases = json.loads((here / "demo_cases.json").read_text())
        outages = json.loads((here / "test_set_outages.json").read_text())
        hours = [h for c in cases["cases"].values() for h in c["hourly"]]
        hours += [h for d in outages["days"] for h in d["hourly"]]
        for fixture in (cases, outages):
            self.assertEqual(fixture["model_version"], run.version)

        scored = score_hours(feature_matrix(pd.DataFrame([h["features"] for h in hours]), run), run)
        np.testing.assert_allclose(scored["p_high"], [h["p_high"] for h in hours], atol=1e-6)
        self.assertEqual(scored["risk"].tolist(), [h["risk"] for h in hours])


if __name__ == "__main__":
    unittest.main()
