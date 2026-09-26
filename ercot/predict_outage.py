#!/usr/bin/env python3
"""Press Run on this file to write the minute-ahead outage-rate forecast.

outage_likelihood is the chance ERCOT generation outage is at or above the
historical 90th percentile. The minute rows land in
dataset/ercot/outage-forecast-minutes.csv.
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def forecast_arguments(root: Path, extra: list[str] | None = None) -> list[str]:
    data = root / "dataset" / "ercot"
    args = [
        "outage-forecast",
        "--prices",
        str(data / "prices" / "rt-prices-2025-08-to-2026-08.csv"),
        "--outages",
        str(data / "np3-233-cd" / "csv"),
        "--weather-dir",
        str(data / "weather"),
        "--out",
        str(data / "outage-forecast-minutes.csv"),
        "--minutes",
        "60",
    ]
    if extra:
        args.extend(extra)
    return args


def run(extra: list[str] | None = None) -> int:
    if str(ROOT) not in sys.path:
        sys.path.insert(0, str(ROOT))
    from ercot.cli import main

    print(
        "Running the 60-minute outage-rate forecast. "
        "outage_likelihood is the chance generation outage is at or above "
        "the historical 90th percentile.",
        file=sys.stderr,
    )
    return main(forecast_arguments(ROOT, extra))


if __name__ == "__main__":
    raise SystemExit(run(sys.argv[1:]))
