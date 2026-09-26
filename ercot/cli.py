"""Command-line access to ERCOT dashboards and the Public Data API."""

from __future__ import annotations

import argparse
import json
import sys

from .client import ErcotError, PublicApi
from .dashboards import DASHBOARDS, DashboardClient


def _print(payload):
    json.dump(payload, sys.stdout, indent=2, default=str)
    sys.stdout.write("\n")


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

    args = parser.parse_args(argv)
    try:
        if args.command == "status":
            _print(DashboardClient().grid_status())
            return 0
        if args.command == "dashboard":
            _print(DashboardClient().get(args.name))
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
    except ErcotError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    parser.error(f"unknown command {args.command}")
    return 2
