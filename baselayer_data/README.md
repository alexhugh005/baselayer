# baselayer_data

The project's data engineering package: pull data from ERCOT, prepare it,
save datasets as `.parq` files under `dataset/`, and load them back.

| Module | What it does |
| --- | --- |
| [`ercot/`](ercot/) | ERCOT Public Data API client, public dashboard feeds and CLI (standard library only). See [ercot/README.md](ercot/README.md). |
| [`ercot_pull.py`](ercot_pull.py) | Bulk pulling on top of the client: rate-paced calls, archive listing, bulk archive download, yearly RTM price workbooks from ercot.com |
| [`storage.py`](storage.py) | `save`, `load`, `list_datasets`, `write_parq` for `dataset/<name>.parq` |
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
save(daily.head(), "scratch/sample") # dataset/scratch/sample.parq
```

Datasets live in `dataset/` at the repository root (gitignored). Set
`BASELAYER_DATASET_DIR` to use another folder. ERCOT credentials go in
`baselayer_data/ercot/.env`; see [ercot/.env.example](ercot/.env.example).

## Tests

```sh
.venv/bin/python -m unittest baselayer_data.austin_outage.test_labels \
  baselayer_data.ercot.test_ercot baselayer_data.ercot.test_outage_rag
```
