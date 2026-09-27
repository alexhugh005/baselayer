# baselayer_model

Models trained on datasets from [`baselayer_data`](../baselayer_data/).

## Austin outage risk classifier

[`austin_outage/`](austin_outage/) trains an XGBoost classifier that rates each
operating day's Austin grid-outage risk as `low`, `medium` or `high`, using
the hourly ERCOT feature table `$DATA_ROOT/dataset/austin-outage/hourly.parq`
built by `python -m baselayer_data austin-outage build`.

| Module | What it does |
| --- | --- |
| [`modeling.py`](austin_outage/modeling.py) | Data loading, split grouping, daily aggregation, threshold tuning, metrics |
| [`train.py`](austin_outage/train.py) | Holds out a test split, runs leave-one-event-out CV, tunes thresholds, refits |
| [`evaluate.py`](austin_outage/evaluate.py) | Scores the held-out test split of a finished run |

[`api/`](api/) serves a trained run over HTTP on live ERCOT data (FastAPI,
Docker); see its [README](api/README.md).

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

### Features

`--feature-set` picks which columns of `hourly.parq` the model sees:

| Set | Uses | For |
| --- | --- | --- |
| `day_ahead` (default) | Day-ahead load forecast, outage capacity posted before midnight, 1–3 day lags, `month`, `day_of_week`, derived features | Rating a day's risk before it starts |
| `nowcast` | `day_ahead` plus same-day actual load, forecast error, `offline_to_load`, real-time price and system lambda | Detecting trouble during the day |

Same-day actuals describe an outage rather than predict it: on Uri's
load-shed days actual load ran up to 38% under forecast because ERCOT was
cutting it, so `nowcast` scores will look much better on Uri than a
forecast can.

Never used as features:

- `offline_new_equip_mw`: new equipment not yet in service, not outages.
- `offline_south_mw` and its lags: ERCOT's zonal outage file starts
  2021-06-03, so these are null for all of 2020 and Uri and would let the
  model learn the era instead of the grid.
- `hour_ending`, `dst_flag`, `price_intervals`, `sced_runs`, label columns
  (`META_COLS` in `modeling.py`).

Derived features, added by `load_table()` from existing columns (all known
before the day starts):

| Column | Meaning |
| --- | --- |
| `fcst_total_mw_day_max`, `fcst_south_c_mw_day_max` | Day's forecast peak, statewide and South Central |
| `fcst_total_mw_ramp` | Hour-over-hour change in the statewide forecast |
| `fcst_south_c_share` | South Central share of forecast load |
| `offline_to_fcst` | Offline capacity / forecast load (day-ahead `offline_to_load`) |
| `fcst_peak_vs_lag1_pct` | Forecast peak vs. yesterday's actual peak, % |
| `offline_mw_change_lag1` | Offline capacity now vs. yesterday's average |
| `load_total_mw_max_trend_3d` | Yesterday's peak minus the peak 3 days ago |
| `price_rt_max_max_3d`, `lambda_max_max_3d`, `offline_to_load_max_3d` | Worst value over the last 3 days |

`--drop-cols` removes further columns from either set.

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
- Lag features on Uri's 2nd–4th days include the previous day's load shed
  (for example `fcst_peak_vs_lag1_pct` reaches 34% on 2021-02-18). That is
  known before the day starts, so it isn't leakage, but it makes those days
  easy.
- Nothing in the ERCOT data sees local storm damage (the 2023 ice storm,
  the 2025 microburst). Weather features (wind gust, freezing rain, ice
  accretion near Austin) would need a new `baselayer_data` source.
