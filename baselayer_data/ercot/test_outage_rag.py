import csv
import unittest
from datetime import date, datetime, timedelta
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from baselayer_data.ercot.cli import main
from baselayer_data.ercot.outage_rag import (
    _response_text,
    apply_model_likelihoods,
    classify_weather_hour,
    hazards_for_event_type,
    likelihood_prompt,
    load_hazard_events,
    load_prices,
    load_realized_outages,
    run_outage_forecast,
)


def _write_prices(path, start, days):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=[
                "deliveryDate",
                "deliveryHour",
                "deliveryInterval",
                "settlementPoint",
                "settlementPointType",
                "settlementPointPrice",
                "DSTFlag",
            ],
        )
        writer.writeheader()
        for offset in range(days):
            day = start + timedelta(days=offset)
            for hour in range(1, 25):
                for interval in range(1, 5):
                    moment = datetime.combine(day, datetime.min.time()) + timedelta(
                        hours=hour - 1, minutes=15 * interval
                    )
                    storm = moment.hour == 16 or (moment.hour == 17 and moment.minute == 0)
                    price = "400" if storm else "25"
                    for point in ("LZ_HOUSTON", "LZ_NORTH", "LZ_SOUTH", "LZ_WEST", "HB_HUBAVG"):
                        writer.writerow({
                            "deliveryDate": moment.date().isoformat() if not (
                                hour == 24 and interval == 4
                            ) else day.isoformat(),
                            "deliveryHour": hour,
                            "deliveryInterval": interval,
                            "settlementPoint": point,
                            "settlementPointType": "LZ",
                            "settlementPointPrice": price,
                            "DSTFlag": "False",
                        })


def _write_outages(directory, start, days, posted_name, high_hours=(17,), high_mw="9000", low_mw="3000"):
    path = directory / posted_name
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["Date", "HourEnding", "TotalResourceMWZoneHouston"],
        )
        writer.writeheader()
        for offset in range(days):
            day = start + timedelta(days=offset)
            for hour in range(1, 25):
                writer.writerow({
                    "Date": day.strftime("%m/%d/%Y"),
                    "HourEnding": hour,
                    "TotalResourceMWZoneHouston": high_mw if hour in high_hours else low_mw,
                })
    return path


def _write_weather(path, start, days):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["time", "precip_mm", "wind_gust_ms", "weather_code"],
        )
        writer.writeheader()
        for offset in range(days + 1):
            day = start + timedelta(days=offset)
            for hour in range(24):
                storm = hour == 16
                writer.writerow({
                    "time": datetime.combine(day, datetime.min.time()).replace(hour=hour).isoformat(),
                    "precip_mm": 20 if storm else 0,
                    "wind_gust_ms": 12 if storm else 4,
                    "weather_code": 95 if storm else 1,
                })


class HazardTests(unittest.TestCase):
    def test_weather_codes_mark_rain_and_storms_only(self):
        self.assertEqual(classify_weather_hour(8, 1, 3), {"heavy_rain"})
        self.assertEqual(classify_weather_hour(0, 95, 3), {"storm"})
        self.assertEqual(classify_weather_hour(0, 1, 30), {"storm"})
        self.assertEqual(classify_weather_hour(0, 1, 10), set())
        self.assertNotIn("hurricane", classify_weather_hour(40, 99, 40))
        self.assertNotIn("tornado", classify_weather_hour(40, 99, 40))

    def test_named_events_keep_hurricane_tornado_and_construction_distinct(self):
        self.assertEqual(hazards_for_event_type("Tornado"), ("tornado",))
        self.assertEqual(hazards_for_event_type("Tropical Storm"), ("hurricane",))
        self.assertEqual(hazards_for_event_type("Hurricane"), ("hurricane",))
        self.assertEqual(hazards_for_event_type("Construction"), ("construction",))
        self.assertEqual(hazards_for_event_type("Wildfire"), ("disaster",))
        self.assertEqual(hazards_for_event_type("Heat"), ())


class ForecastTests(unittest.TestCase):
    def _build(self, root):
        start = date(2026, 1, 1)
        prices = root / "prices.csv"
        outages = root / "outages"
        weather = root / "hourly.csv"
        events = root / "events.csv"
        outages.mkdir()
        _write_prices(prices, start, 20)
        _write_outages(
            outages,
            start,
            20,
            "cdr.00013103.0000000000000000.20260121.060000.HRLYRESOUTCAPNP3233.csv",
        )
        _write_weather(weather, start, 20)
        with events.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(
                handle,
                fieldnames=("start", "end", "event_type", "region", "summary"),
            )
            writer.writeheader()
            writer.writerow({
                "start": "2026-01-20 16:00",
                "end": "2026-01-20 17:00",
                "event_type": "construction",
                "region": "Houston",
                "summary": "Transmission construction in Houston",
            })
            writer.writerow({
                "start": "2026-01-05 16:10",
                "end": "2026-01-05 16:25",
                "event_type": "Tornado",
                "region": "DALLAS",
                "summary": "Tornado in DALLAS",
            })
        return prices, outages, weather, events

    def test_storm_hours_raise_the_outage_and_carry_their_hazards(self):
        with TemporaryDirectory() as tmp:
            prices, outages, weather, events = self._build(Path(tmp))
            storm = run_outage_forecast(
                prices, outages, weather, [events],
                as_of="2026-01-20 16:15", minutes=60, k=5,
            )
            calm = run_outage_forecast(
                prices, outages, weather, [events],
                as_of="2026-01-20 10:15", minutes=60, k=5,
            )
        self.assertEqual(len(storm["minutes_ahead"]), 60)
        self.assertGreaterEqual(storm["minutes_ahead"][29]["predicted_outage_mw"], 8000)
        self.assertLessEqual(calm["minutes_ahead"][29]["predicted_outage_mw"], 4000)
        self.assertEqual(storm["linked_events"], ["heavy_rain", "storm"])
        self.assertEqual(calm["linked_events"], [])
        self.assertIn("construction", storm["minutes_ahead"][20]["events_at_time"])
        self.assertNotIn("construction", storm["linked_events"])
        for item in storm["retrieved"]:
            self.assertLessEqual(item["time"], "2026-01-20 15:15")
            self.assertTrue(item["time"].endswith("16:15"))
            self.assertIn("storm", item["hazards"])
        self.assertIn("construction", storm["narrative"])
        self.assertIn("likelihood", storm["narrative"])
        self.assertLess(storm["mae_mw"], storm["persistence_mae_mw"])
        for row in storm["minutes_ahead"]:
            self.assertGreaterEqual(row["outage_likelihood"], 0)
            self.assertLessEqual(row["outage_likelihood"], 1)
            self.assertEqual(row["outage_likelihood"], row["retrieval_likelihood"])

    def test_storm_regime_raises_elevated_outage_likelihood(self):
        with TemporaryDirectory() as tmp:
            root = Path(tmp)
            start = date(2026, 1, 1)
            prices = root / "prices.csv"
            outages = root / "outages"
            weather = root / "hourly.csv"
            events = root / "events.csv"
            outages.mkdir()
            _write_prices(prices, start, 20)
            _write_outages(
                outages,
                start,
                20,
                "cdr.00013103.0000000000000000.20260121.060000.HRLYRESOUTCAPNP3233.csv",
                high_hours=(16, 17, 18),
                high_mw="12000",
                low_mw="2000",
            )
            _write_weather(weather, start, 20)
            events.write_text(
                "start,end,event_type,region,summary\n",
                encoding="utf-8",
            )
            storm = run_outage_forecast(
                prices, outages, weather, [events],
                as_of="2026-01-20 16:15", minutes=60, k=5,
            )
            calm = run_outage_forecast(
                prices, outages, weather, [events],
                as_of="2026-01-20 10:15", minutes=60, k=5,
            )
        self.assertGreater(storm["minutes_ahead"][29]["outage_likelihood"], 0.8)
        self.assertLess(calm["minutes_ahead"][29]["outage_likelihood"], 0.2)
        self.assertEqual(storm["likelihood_source"], "retrieval")
        self.assertGreaterEqual(storm["elevated_threshold_mw"], 2000)
        self.assertLessEqual(storm["elevated_threshold_mw"], 12000)
        self.assertEqual(len(storm["analogs"][0]["elevated"]), 60)
        prompt = likelihood_prompt(storm, 1, 60)
        self.assertIn("elevated=", prompt)
        self.assertIn("12000", prompt)

    def test_future_outage_rows_are_not_treated_as_realized(self):
        with TemporaryDirectory() as tmp:
            directory = Path(tmp)
            early = directory / "cdr.00013103.0000000000000000.20260105.000000.HRLYRESOUTCAPNP3233.csv"
            late = directory / "cdr.00013103.0000000000000000.20260105.060000.HRLYRESOUTCAPNP3233.csv"
            for path, rows in (
                (early, [("01/04/2026", "24", "111"), ("01/05/2026", "6", "99999")]),
                (late, [("01/05/2026", "6", "1500")]),
            ):
                with path.open("w", newline="", encoding="utf-8") as handle:
                    writer = csv.writer(handle)
                    writer.writerow(["Date", "HourEnding", "TotalResourceMWZoneHouston"])
                    writer.writerows(rows)
            realized = load_realized_outages(directory)
        self.assertEqual(realized[datetime(2026, 1, 5, 0, 0)]["total"], 111)
        self.assertEqual(realized[datetime(2026, 1, 5, 6, 0)]["total"], 1500)

    def test_duplicate_prices_are_averaged(self):
        with TemporaryDirectory() as tmp:
            path = Path(tmp) / "prices.csv"
            with path.open("w", newline="", encoding="utf-8") as handle:
                writer = csv.DictWriter(
                    handle,
                    fieldnames=[
                        "deliveryDate", "deliveryHour", "deliveryInterval",
                        "settlementPoint", "settlementPointType",
                        "settlementPointPrice", "DSTFlag",
                    ],
                )
                writer.writeheader()
                for price in ("10", "30"):
                    for point in ("LZ_HOUSTON", "LZ_NORTH", "LZ_SOUTH", "LZ_WEST"):
                        writer.writerow({
                            "deliveryDate": "2026-01-01",
                            "deliveryHour": "1",
                            "deliveryInterval": "1",
                            "settlementPoint": point,
                            "settlementPointType": "LZ",
                            "settlementPointPrice": price,
                            "DSTFlag": "False",
                        })
            prices = load_prices(path)
        moment = datetime(2026, 1, 1, 0, 15)
        self.assertEqual(prices[moment]["LZ_HOUSTON"], 20)
        self.assertEqual(prices[moment]["HB_HUBAVG"], 20)

    def test_local_construction_file_uses_the_event_name(self):
        with TemporaryDirectory() as tmp:
            path = Path(tmp) / "local-events.csv"
            path.write_text(
                "start,end,event_type,region,summary\n"
                "2026-03-01 12:00,2026-03-01 18:00,construction,Austin,Line construction in Austin\n",
                encoding="utf-8",
            )
            events = load_hazard_events(path)
        self.assertEqual(events[0]["hazards"], ("construction",))

    def test_grok_likelihoods_replace_retrieval_when_the_horizon_is_complete(self):
        result = {
            "minutes_ahead": [
                {"outage_likelihood": 0.1, "retrieval_likelihood": 0.1},
                {"outage_likelihood": 0.2, "retrieval_likelihood": 0.2},
            ],
            "likelihood_source": "retrieval",
        }
        matched = apply_model_likelihoods(
            result,
            [{"offset": 1, "likelihood": 0.4}, {"offset": 2, "likelihood": 1.4}],
            " Storm analogs dominate. ",
            model="grok-4.7",
        )
        self.assertEqual(matched, 2)
        self.assertEqual(result["minutes_ahead"][0]["outage_likelihood"], 0.4)
        self.assertEqual(result["minutes_ahead"][1]["outage_likelihood"], 1.0)
        self.assertEqual(result["minutes_ahead"][0]["retrieval_likelihood"], 0.1)
        self.assertEqual(result["likelihood_source"], "grok-4.7")
        self.assertEqual(result["model"], "grok-4.7")
        self.assertEqual(result["model_explanation"], "Storm analogs dominate.")

        partial = {
            "minutes_ahead": [
                {"outage_likelihood": 0.1, "retrieval_likelihood": 0.1},
                {"outage_likelihood": 0.2, "retrieval_likelihood": 0.2},
            ],
            "likelihood_source": "retrieval",
        }
        apply_model_likelihoods(partial, [{"offset": 1, "likelihood": 0.3}], model="grok-4.7")
        self.assertEqual(partial["likelihood_source"], "mixed")
        self.assertEqual(partial["minutes_ahead"][1]["outage_likelihood"], 0.2)

    def test_grok_text_is_read_from_the_response_payload(self):
        self.assertEqual(
            _response_text({"output_text": " Grounded summary. "}),
            "Grounded summary.",
        )
        self.assertEqual(
            _response_text({
                "output": [{"content": [{"type": "output_text", "text": "From episodes."}]}],
            }),
            "From episodes.",
        )

    def test_cli_writes_one_row_per_minute_without_a_network_call(self):
        with TemporaryDirectory() as tmp:
            root = Path(tmp)
            prices, outages, weather, events = self._build(root)
            weather_dir = root / "weather"
            weather_dir.mkdir()
            (weather_dir / "hourly.csv").write_text(weather.read_text(encoding="utf-8"), encoding="utf-8")
            (weather_dir / "texas-storm-events.csv").write_text(
                events.read_text(encoding="utf-8"),
                encoding="utf-8",
            )
            output = root / "minutes.csv"
            status = main([
                "outage-forecast",
                "--prices", str(prices),
                "--outages", str(outages),
                "--weather-dir", str(weather_dir),
                "--out", str(output),
                "--as-of", "2026-01-20 16:15",
                "--minutes", "15",
                "--k", "5",
            ])
            rows = list(csv.DictReader(output.open(encoding="utf-8")))
        self.assertEqual(status, 0)
        self.assertEqual(len(rows), 15)
        self.assertEqual(rows[0]["timestamp"], "2026-01-20 16:16")
        self.assertEqual(rows[1]["timestamp"], "2026-01-20 16:17")
        self.assertIn("heavy_rain", rows[0]["linked_events"])
        self.assertIn("construction", rows[0]["events_at_time"])
        self.assertGreaterEqual(float(rows[0]["outage_likelihood"]), 0)
        self.assertLessEqual(float(rows[0]["outage_likelihood"]), 1)

    def test_predict_outage_file_runs_the_forecast_command(self):
        from baselayer_data.ercot.predict_outage import ROOT, forecast_arguments, run

        prices = str(ROOT / "dataset" / "ercot" / "prices" / "rt-prices-2025-08-to-2026-08.csv")
        output = str(ROOT / "dataset" / "ercot" / "outage-forecast-minutes.csv")
        args = forecast_arguments(ROOT, ["--no-model"])
        self.assertEqual(args[args.index("--prices") + 1], prices)
        self.assertEqual(args[args.index("--out") + 1], output)
        self.assertIn("--no-model", args)
        with patch("baselayer_data.ercot.cli.main", return_value=0) as cli_main:
            status = run(["--no-model"])
        self.assertEqual(status, 0)
        cli_main.assert_called_once_with(args)


if __name__ == "__main__":
    unittest.main()
