"""Baselayer data engineering: pull ERCOT data, prepare it, and save or load
datasets as .parq files under $DATA_ROOT/dataset/ (DATA_ROOT defaults to the
current directory; logs go to $DATA_ROOT/logs/).

    from baselayer_data import load, list_datasets
    daily = load("austin-outage/daily")
"""

from .paths import data_root, dataset_root, logs_root
from .storage import dataset_path, list_datasets, load, save, write_parq

__all__ = [
    "data_root",
    "dataset_path",
    "dataset_root",
    "list_datasets",
    "load",
    "logs_root",
    "save",
    "write_parq",
]
