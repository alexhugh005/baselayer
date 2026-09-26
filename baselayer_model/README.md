# baselayer_model

Models trained on datasets from [`baselayer_data`](../baselayer_data/).

## Austin outage risk classifier

[`austin_outage/`](austin_outage/) trains an XGBoost classifier that rates each
operating day's Austin grid-outage risk as `low`, `medium` or `high`, using
the hourly ERCOT feature table `dataset/austin-outage/hourly.parq` built by
`python -m baselayer_data austin-outage build`.

| Module | What it does |
| --- | --- |
| [`modeling.py`](austin_outage/modeling.py) | Data loading, split grouping, daily aggregation, threshold tuning, metrics |
| [`train.py`](austin_outage/train.py) | Holds out a test split, runs leave-one-event-out CV, tunes thresholds, refits |
| [`evaluate.py`](austin_outage/evaluate.py) | Scores the held-out test split of a finished run |

### Setup

```sh
.venv/bin/pip install -r baselayer_model/requirements.txt
```

On macOS, xgboost also needs the OpenMP runtime (`brew install libomp`).

### Run

```sh
.venv/bin/python -m baselayer_model.austin_outage.train            # prints its output dir
.venv/bin/python -m baselayer_model.austin_outage.evaluate --run-dir <that dir>
.venv/bin/python -m unittest baselayer_model.austin_outage.test_modeling
```

`train` uses CUDA when a GPU is usable (`--device auto`, the default) and
falls back to CPU otherwise. Outputs go to a new temp directory
(`austin_outage_run_*`) unless `--out-dir` is given. Run `train --help` for
split, threshold and XGBoost options.

### How it trains

1. **Test split.** The most recent `high` event (all its days, including its
   ±14-day window) plus ~15% of the other day groups are held out and saved
   to `split.json`. `train` never scores them; only `evaluate` does.
   `--test-event` picks a different event.
2. **Cross-validation.** One fold per remaining `high` event, so each storm
   is validated by a model that never saw it. Labeled days within 3 days of
   each other stay together, so the 1–3 day lag features can't leak across a
   split. Merror and mlogloss print every `--log-every` rounds; each fold
   then reports per-class precision, recall, F1, PR-AUC and a confusion
   matrix, per hour and per day.
3. **Class weights.** Each class carries equal total weight, so the few
   `high` hours count as much as all `low` ones.
4. **Thresholds.** Tuned on the out-of-fold daily predictions, then the
   model is refit on all non-test rows for the CV-average number of rounds.

The model scores hours; a day's probabilities are the mean over its hours.

### `classification_thresholds.json`

Checked in order, on daily probabilities:

- `high` if `p_high >= thresholds.high` (tuned for F2, favouring recall)
- `medium` if `p_medium + p_high >= thresholds.medium` (tuned for F1)
- `low` otherwise, equivalently `p_low > thresholds.low`

### Run directory

| File | Contents |
| --- | --- |
| `model.json` | Final XGBoost model |
| `classification_thresholds.json` | Per-class thresholds and decision rule |
| `split.json` | Held-out test groups and days |
| `train_metrics.json` | Per-fold and out-of-fold metrics |
| `oof_daily_predictions.csv` | Out-of-fold daily probabilities |
| `feature_importance.csv` | Gain importance of the final model |
| `run_config.json` | Arguments, features, device, xgboost version |
| `test_metrics.json`, `test_daily_predictions.csv` | Written by `evaluate` |

### Caveats

- The default test event, the May 2025 microburst, was local line damage
  that ERCOT data doesn't show, so expect weak `high` recall on it.
- With the microburst held out, CV has two folds (Uri and the 2023 ice
  storm). Uri is the only supply-driven `high` event.
