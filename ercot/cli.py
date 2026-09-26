"""Command-line access to ERCOT dashboards and the Public Data API."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from urllib.error import URLError

from .client import ErcotError, PublicApi, load_config
from .dashboards import DASHBOARDS, DashboardClient
from .household import previous_complete_month_start, save_household_usage
from .outage_rag import (
    explain_with_grok,
    prepare_weather,
    public_summary,
    run_outage_forecast,
    save_minute_forecast,
    score_likelihood_with_grok,
)


def _print(payload):
    json.dump(payload, sys.stdout, indent=2, default=str)
    sys.stdout.write("\n")


def _xai_api_key():
    if os.environ.get("XAI_API_KEY"):
        return os.environ["XAI_API_KEY"]
    return load_config().get("XAI_API_KEY") or None


def _parse_params(pairs):
    params = {}
    for item in pairs or []:
        if "=" not in item:
            raise SystemExit(f"parameter must be key=value, got {item!r}")
        key, value = item.split("=", 1)
        params[key] = value
    return params


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="python -m ercot",
        description="Connect to ERCOT Public Data API and public dashboards.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("status", help="grid condition, prices, supply, and storage snapshot")
    dash = sub.add_parser("dashboard", help="fetch a public dashboard JSON feed")
    dash.add_argument("name", choices=sorted(DASHBOARDS), help="dashboard alias")
    usage = sub.add_parser(
        "daily-usage",
        help="save actual ERCOT demand summarized by operating day",
    )
    usage.add_argument("--out", default="dataset", help="output directory (default: dataset)")
    estimate = sub.add_parser("household-estimate", help="save an estimated household load profile")
    estimate.add_argument("--out", default=None, help="CSV output path")
    estimate.add_argument("--start-date", default=previous_complete_month_start().isoformat())
    estimate.add_argument("--days", type=int, default=30)
    estimate.add_argument("--daily-kwh", type=float, default=30.0)
    estimate.add_argument("--interval-minutes", type=int, choices=(5, 15), default=5)
    datasets = sub.add_parser(
        "datasets",
        help="save live dashboard JSON into the dataset folder",
    )
    datasets.add_argument("--out", default="dataset", help="output directory (default: dataset)")
    download = sub.add_parser(
        "download",
        help="download a Public Data API artifact or archive into the dataset folder",
    )
    download.add_argument("emil_id")
    download.add_argument(
        "artifact",
        nargs="?",
        help="artifact name; omit to download posted archive files",
    )
    download.add_argument("--out", default="dataset", help="output directory (default: dataset)")
    download.add_argument("--format", default="csv", choices=("csv", "json"))
    download.add_argument("--limit", type=int, default=None, help="max archive files")
    download.add_argument("--param", action="append", default=[], help="query parameter key=value")

    sub.add_parser("token", help="obtain a Public Data API id_token (requires credentials)")
    sub.add_parser("products", help="list EMIL products from the Public Data API")
    prod = sub.add_parser("product", help="describe one EMIL product")
    prod.add_argument("emil_id")
    art = sub.add_parser("data", help="download an EMIL product artifact")
    art.add_argument("emil_id")
    art.add_argument("artifact")
    art.add_argument("--param", action="append", default=[], help="query parameter key=value")
    arch = sub.add_parser("archive", help="list historic files for an EMIL product")
    arch.add_argument("emil_id")
    arch.add_argument("--param", action="append", default=[], help="query parameter key=value")
    forecast = sub.add_parser(
        "outage-forecast",
        help="predict elevated generation-outage likelihood each minute from similar price and weather regimes",
    )
    forecast.add_argument(
        "--prices",
        default="dataset/ercot/prices/rt-prices-2025-08-to-2026-08.csv",
    )
    forecast.add_argument("--outages", default="dataset/ercot/np3-233-cd/csv")
    forecast.add_argument("--weather-dir", default="dataset/ercot/weather")
    forecast.add_argument("--out", default="dataset/ercot/outage-forecast-minutes.csv")
    forecast.add_argument("--minutes", type=int, default=60)
    forecast.add_argument("--k", type=int, default=25)
    forecast.add_argument("--as-of", default=None, help="ISO timestamp in ERCOT operating time")
    forecast.add_argument("--refresh-weather", action="store_true")
    forecast.add_argument(
        "--no-model",
        action="store_true",
        help="publish the retrieval likelihood and skip Grok 4.7",
    )
    forecast.add_argument(
        "--explain",
        action="store_true",
        help="ask Grok 4.7 to narrate the retrieved hours (requires XAI_API_KEY)",
    )

    args = parser.parse_args(argv)
    try:
        if args.command == "status":
            _print(DashboardClient().grid_status())
            return 0
        if args.command == "dashboard":
            _print(DashboardClient().get(args.name))
            return 0
        if args.command == "daily-usage":
            path = DashboardClient().save_daily_usage(args.out)
            _print({"file": str(path)})
            return 0
        if args.command == "household-estimate":
            start = args.start_date.replace("-", "")
            output = args.out or f"dataset/household-estimate-{start}-{args.interval_minutes}min.csv"
            path = save_household_usage(
                output,
                start_date=args.start_date,
                days=args.days,
                daily_kwh=args.daily_kwh,
                interval_minutes=args.interval_minutes,
            )
            _print({"file": str(path), "rows": args.days * 24 * 60 // args.interval_minutes})
            return 0
        if args.command == "datasets":
            paths = DashboardClient().save_datasets(args.out)
            _print({"directory": args.out, "files": [str(path) for path in paths]})
            return 0
        if args.command == "outage-forecast":
            weather_dir = Path(args.weather_dir)
            print("indexing prices, realized outages, and weather hazards", file=sys.stderr)
            cached = prepare_weather(weather_dir, refresh=args.refresh_weather)
            result = run_outage_forecast(
                args.prices,
                args.outages,
                cached["hourly"],
                [cached["events"], weather_dir / "local-events.csv"],
                as_of=args.as_of,
                minutes=args.minutes,
                k=args.k,
            )
            api_key = _xai_api_key()
            if api_key and not args.no_model:
                try:
                    matched = score_likelihood_with_grok(result, api_key)
                    if matched != result["minutes"]:
                        result["model_error"] = (
                            f"Grok scored {matched} of {result['minutes']} minutes; "
                            "unscored minutes keep the retrieval likelihood"
                        )
                except (URLError, ValueError, TimeoutError, json.JSONDecodeError) as exc:
                    result["model_error"] = str(exc)
            elif args.explain and not api_key:
                result["model_error"] = "XAI_API_KEY is not set; likelihoods are the retrieval estimate"
            if args.explain and api_key and not result.get("model_explanation"):
                try:
                    result["model_explanation"] = explain_with_grok(result, api_key)
                except (URLError, ValueError, TimeoutError) as exc:
                    result["model_error"] = str(exc)
            output = save_minute_forecast(args.out, result)
            _print(public_summary(result, output))
            return 0

        api = PublicApi.from_env()
        if args.command == "token":
            token = api.fetch_token()
            _print({"id_token": token, "expires_at": api._token_expires_at})
            return 0
        if args.command == "products":
            _print(api.products())
            return 0
        if args.command == "product":
            _print(api.product(args.emil_id))
            return 0
        if args.command == "data":
            _print(api.artifact(args.emil_id, args.artifact, **_parse_params(args.param)))
            return 0
        if args.command == "archive":
            _print(api.archive(args.emil_id, **_parse_params(args.param)))
            return 0
        if args.command == "download":
            if args.artifact:
                path = api.download_artifact(
                    args.emil_id,
                    args.artifact,
                    args.out,
                    file_format=args.format,
                    **_parse_params(args.param),
                )
                _print({"file": str(path)})
            else:
                paths = api.download_archive(
                    args.emil_id,
                    args.out,
                    limit=args.limit,
                    **_parse_params(args.param),
                )
                _print({"files": [str(path) for path in paths]})
            return 0
    except (ErcotError, OSError, ValueError, URLError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    parser.error(f"unknown command {args.command}")
    return 2
