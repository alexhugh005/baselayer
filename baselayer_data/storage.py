"""Save and load datasets as .parq files under $DATA_ROOT/dataset/ (see paths.py).

pandas is imported only when reading, so importing baselayer_data.ercot
stays standard-library only.
"""

from __future__ import annotations

from pathlib import Path

from .paths import dataset_root

SUFFIX = ".parq"


def dataset_path(name, root=None):
    """<root>/<name>.parq, where name may contain folders ("austin-outage/daily").

    root defaults to $DATA_ROOT/dataset.
    """
    return Path(root or dataset_root()) / f"{name}{SUFFIX}"


def write_parq(df, path):
    """Write a DataFrame to an explicit .parq path, creating folders."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    df.to_parquet(path, index=False)
    return path


def save(df, name, root=None):
    return write_parq(df, dataset_path(name, root))


def load(name, columns=None, root=None):
    path = dataset_path(name, root)
    if not path.exists():
        raise FileNotFoundError(f"no dataset {name!r} at {path}; see list_datasets()")
    import pandas as pd

    return pd.read_parquet(path, columns=columns)


def list_datasets(root=None, include_raw=False):
    """Dataset names under the root, skipping raw/ pulls unless asked."""
    root = Path(root or dataset_root())
    names = []
    for path in sorted(root.rglob(f"*{SUFFIX}")):
        relative = path.relative_to(root)
        if not include_raw and ("raw" in relative.parts or "_listing" in relative.parts):
            continue
        names.append(str(relative.with_suffix("")))
    return names
