"""Where baselayer_data keeps its files.

DATA_ROOT names the base directory for everything the package writes:

    $DATA_ROOT/dataset/   datasets (.parq) and raw pulls
    $DATA_ROOT/logs/      log files

When DATA_ROOT is unset, the current directory is used, so running from the
repository root keeps data in the gitignored dataset/ folder. Paths are read
on every call, so setting DATA_ROOT after import still takes effect.
"""

from __future__ import annotations

import os
from pathlib import Path

ENV_VAR = "DATA_ROOT"


def data_root():
    return Path(os.environ.get(ENV_VAR) or Path.cwd()).expanduser().resolve()


def dataset_root():
    return data_root() / "dataset"


def logs_root():
    return data_root() / "logs"
