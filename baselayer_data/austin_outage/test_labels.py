import datetime as dt
import unittest

from baselayer_data.austin_outage.labels import EXCLUDED, HIGH_EVENTS, build_labels, pull_days


class LabelTests(unittest.TestCase):
    def setUp(self):
        self.labels = build_labels()

    def test_class_counts_match_spec(self):
        counts = self.labels["label"].value_counts().to_dict()
        self.assertEqual(counts, {"low": 330, "medium": 25, "high": 8})

    def test_restoration_days_are_dropped(self):
        self.assertFalse(set(EXCLUDED) & set(self.labels["oper_day"]))

    def test_event_windows_cover_each_high_event(self):
        windows = self.labels[self.labels["source"] == "event_window"]
        self.assertEqual(set(windows["event"]), set(HIGH_EVENTS))
        uri = windows[windows["event"] == "uri_2021"]["oper_day"]
        self.assertEqual(min(uri), dt.date(2021, 2, 1))
        self.assertEqual(max(uri), dt.date(2021, 3, 4))

    def test_sampling_is_reproducible(self):
        again = build_labels()
        self.assertTrue(self.labels.equals(again))

    def test_pull_days_include_three_day_lags(self):
        days = set(pull_days(self.labels))
        for d in (dt.date(2021, 2, 11), dt.date(2021, 2, 12), dt.date(2021, 2, 13)):
            self.assertIn(d, days)


if __name__ == "__main__":
    unittest.main()
