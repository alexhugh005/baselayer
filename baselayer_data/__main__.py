"""python -m baselayer_data {list,austin-outage}

ERCOT API commands live in `python -m baselayer_data.ercot`.
"""

from __future__ import annotations

import argparse
import datetime as dt

from .paths import dataset_root
from .storage import list_datasets


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m baselayer_data")
    sub = parser.add_subparsers(dest="command", required=True)
    listing = sub.add_parser("list", help="list saved datasets")
    listing.add_argument("--raw", action="store_true", help="include raw pulls")

    outage = sub.add_parser("austin-outage", help="Austin grid outage classifier dataset")
    outage.add_argument(
        "--out", default=None, help="output folder (default: $DATA_ROOT/dataset/austin-outage)"
    )
    steps = outage.add_subparsers(dest="step", required=True)
    steps.add_parser("labels", help="write labels.parq")
    pull = steps.add_parser("pull", help="download raw ERCOT data for the labeled days")
    pull.add_argument("--steps", nargs="+")
    pull.add_argument("--days", nargs="+", help="only these YYYY-MM-DD days (for testing)")
    steps.add_parser("build", help="write hourly.parq, daily.parq and coverage.parq")
    args = parser.parse_args(argv)

    if args.command == "list":
        print("\n".join(list_datasets(include_raw=args.raw)))
        return 0

    from pathlib import Path

    from .austin_outage import features, labels, pull as pulling

    out = Path(args.out) if args.out else dataset_root() / "austin-outage"
    table = labels.build_labels()
    if args.step == "labels":
        path = labels.save_labels(table, out)
        print(f"{path}\n{table['label'].value_counts().to_string()}")
    elif args.step == "pull":
        from .ercot_pull import PacedApi

        days = (
            sorted(dt.date.fromisoformat(d) for d in args.days)
            if args.days
            else labels.pull_days(table)
        )
        steps = args.steps or list(pulling.STEPS)
        pulling.pull(PacedApi.from_env(), out / "raw", days, steps=steps)
    elif args.step == "build":
        features.build(out, table)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
