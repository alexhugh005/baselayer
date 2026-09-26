# baselayer_data

The project's data engineering package: pull data from ERCOT, prepare it,
save datasets as `.parq` files under `$DATA_ROOT/dataset/`, and load them back.

| Module | What it does |
| --- | --- |
| [`ercot/`](ercot/) | ERCOT Public Data API client, public dashboard feeds and CLI (standard library only). See [ercot/README.md](ercot/README.md). |
| [`ercot_pull.py`](ercot_pull.py) | Bulk pulling on top of the client: rate-paced calls, archive listing, bulk archive download, yearly RTM price workbooks from ercot.com |
| [`paths.py`](paths.py) | `data_root`, `dataset_root`, `logs_root` from the `DATA_ROOT` environment variable |
| [`storage.py`](storage.py) | `save`, `load`, `list_datasets`, `write_parq` for `$DATA_ROOT/dataset/<name>.parq` |
| [`logs.py`](logs.py) | `get_logger(name)`: prints and appends to `$DATA_ROOT/logs/<name>.log` |
| [`austin_outage/`](austin_outage/) | Austin grid outage classifier dataset: labels, pulls, hourly and daily features. See [austin_outage/README.md](austin_outage/README.md). |

## Use

Run from the repository root with the project `.venv`
(`.venv/bin/pip install pandas pyarrow python-calamine`):

```sh
.venv/bin/python -m baselayer_data list                    # saved datasets
.venv/bin/python -m baselayer_data austin-outage build     # one dataset's steps
.venv/bin/python -m baselayer_data.ercot products          # ERCOT API CLI
```

```python
from baselayer_data import list_datasets, load, save

list_datasets()                      # ["austin-outage/daily", ...]
daily = load("austin-outage/daily")
save(daily.head(), "scratch/sample") # $DATA_ROOT/dataset/scratch/sample.parq
```

## Where files go

`DATA_ROOT` sets the base directory for everything the package writes. It
defaults to the current directory, so running from the repository root uses
the gitignored `dataset/` folder there.

```
$DATA_ROOT/
  dataset/   datasets (.parq), raw pulls, ERCOT CLI downloads
  logs/      job logs, e.g. austin-outage-pull.log
```

```sh
export DATA_ROOT=/srv/baselayer    # or /Volumes/data/baselayer, etc.
.venv/bin/python -m baselayer_data austin-outage pull
```

It is read on every call, so it can also be set from Python before use.
ERCOT credentials go in
`baselayer_data/ercot/.env`; see [ercot/.env.example](ercot/.env.example).

## Tests

```sh
.venv/bin/python -m unittest baselayer_data.test_storage baselayer_data.austin_outage.test_labels \
  baselayer_data.ercot.test_ercot baselayer_data.ercot.test_outage_rag
```
