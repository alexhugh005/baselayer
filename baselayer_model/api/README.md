# Outage risk model API

FastAPI service that rates an Austin operating day's grid-outage risk
`low`, `medium` or `high` with the XGBoost model from
[`../austin_outage`](../austin_outage/). It pulls the day's ERCOT data itself,
builds the 69 `day_ahead` features with the same `baselayer_data` code that
built the training table, and applies the run's tuned thresholds.

## Run

```sh
.venv/bin/uvicorn baselayer_model.api.app:app --port 8000   # local (needs `brew install libomp`)
docker compose up --build model                             # container, on 127.0.0.1:${MODEL_PORT:-8000}
```

ERCOT Public API credentials come from `baselayer_data/ercot/.env` (see
[its README](../../baselayer_data/ercot/README.md)); compose passes that file
to the container.

| Variable | Default | Meaning |
| --- | --- | --- |
| `MODEL_DIR` | `baselayer_model/api/model` (`/app/model` in the image) | Run directory to serve |
| `DATA_ROOT` | current directory (`/data` in the image) | Raw pulls, features and results go under `serve/` |
| `REFRESH_SECONDS` | `900` | Background refresh of today's result; `0` disables |

## Endpoints

`GET /v1/outage-risk?day=YYYY-MM-DD` scores one operating day. `day`
defaults to today in Central time and can be at most tomorrow;
`refresh=true` ignores the cache.

```json
{
  "oper_day": "2026-09-26",
  "risk": "low",
  "p_low": 0.545, "p_medium": 0.251, "p_high": 0.204, "p_elevated": 0.455,
  "thresholds": {"low": 0.426848, "medium": 0.573152, "high": 0.377967},
  "degraded": false,
  "missing_features": [],
  "model_version": "20260926-2006-ef94496938cc",
  "generated_at": "2026-09-26T21:40:12-05:00",
  "data_as_of": {"forecast": "2026-09-25 09:30:00", "outage_cap": "2026-09-25 23:00:48"},
  "pull_errors": {},
  "stale": false,
  "hourly": [
    {"hour_ending": 1, "dst_flag": false, "risk": "low",
     "p_low": 0.545, "p_medium": 0.251, "p_high": 0.204, "p_elevated": 0.455}
  ]
}
```

- `hourly`: one entry per operating hour (hour ending 1–24, Central time;
  `dst_flag` marks the repeated hour on the fall-back day). The model scores
  each hour, and each hour gets its own `risk` from the same threshold
  cascade as the day. The model was trained on hourly rows labeled with
  their day's class and its thresholds were tuned on daily means, so hours
  within a day differ only through the inputs that change by hour
  (forecast load and offline capacity); expect a nearly flat profile.
- `risk` and the top-level `p_*`: the day, i.e. the mean of the hourly
  probabilities, as `evaluate.py` scores it.

- `degraded`: a feature other than system lambda (null in 29.5% of training
  rows) is missing for every hour. Scoring still runs; XGBoost treats the
  gap as missing.
- `stale`: a fresh pull failed and this is the last good result.
- `503`: no result could be produced, usually because the day-ahead load
  forecast isn't posted yet (it posts around 09:30 the day before).

`GET /health` returns the model version and the last background refresh.

`GET /v1/demo/case-1` and `GET /v1/demo/case-2` score fixed sequences of real
historical hours (low → medium → high) for demos, without calling ERCOT; see
[demo/README.md](demo/README.md).

## When a day's result is ready

A day's features are its day-ahead load forecast (posted ~09:30 the day
before), its outage capacity (last posting before midnight), and 1–3 day lags
of actual load, forecast error, offline capacity, price and system lambda.
Yesterday's actual load posts around 05:50, so:

- **Tomorrow** can be scored after ~09:30 but is always `degraded`: today's
  actuals, the lag-1 inputs, don't exist yet.
- **Today** is complete from ~06:00. A complete result generated after 07:00
  is kept; anything else is recomputed at most hourly.

The first request for a day pulls about 300 archive files from ERCOT (paced
under its 30 requests/minute limit) and takes 30–60 seconds; later requests
are served from memory or `$DATA_ROOT/serve/results/<day>.json`. Each day's
features are kept in `$DATA_ROOT/serve/features/<day>.parq`.

## Files

| File | What it does |
| --- | --- |
| [`app.py`](app.py) | FastAPI app, caching and background refresh |
| [`live_features.py`](live_features.py) | Pulls a day's ERCOT data and builds its hourly feature rows |
| [`predictor.py`](predictor.py) | Scores hourly rows and applies the thresholds |
| [`artifacts.py`](artifacts.py) | Loads and checks a run directory |
| [`model/`](model/) | The served run: `model.json`, `run_config.json`, `classification_thresholds.json` |
| [`Dockerfile`](Dockerfile) | Image; build context is the repository root |

To serve a new run, copy those three files from a `train.py` output into
`model/` (or build with `--build-arg MODEL_RUN=<dir>`). Startup fails if the
run isn't `day_ahead`, its features don't match `model.json`, or its xgboost
major.minor version differs from the installed one.

## Tests

```sh
.venv/bin/python -m unittest baselayer_model.api.test_api
```

With the local dataset present, two tests check the service against
training:

- **Golden:** scoring the stored `hourly.parq` rows reproduces `evaluate.py`
  on all 68 test days, and `dataset/model/test_daily_predictions.csv` to
  1e-6 on test days through 2024-07-10. Later days differ from that CSV
  because the local `hourly.parq` isn't the training machine's copy; most
  likely its lambda data, which stops locally on 2024-07-10 (ERCOT renamed
  `SystemLambda` to `CappedSystemLambda` in NP6-322-CD).
- **Parity:** `build_day` on the training raw pulls reproduces the training
  table's 69 features exactly (Uri and a 2026 day).
