# Austin grid outage dataset

Training data for a daily 3-class model (`high`, `medium`, `low`) that rates
Austin's grid-outage risk from ERCOT data. It follows the "Austin Grid Outage
Classifier — Data Spec" (2026-09-26). This package builds the data only;
modeling lives elsewhere.

## Run

From the repository root, with `baselayer_data/ercot/.env` holding
`ERCOT_PUBLIC_API_SUBSCRIPTION_KEY` (see [ercot/README.md](../ercot/README.md)):

```sh
.venv/bin/pip install pandas pyarrow python-calamine
.venv/bin/python -m baselayer_data austin-outage labels   # labels.parq
.venv/bin/python -m baselayer_data austin-outage pull     # raw/ (resumable; ~1-2 hours)
.venv/bin/python -m baselayer_data austin-outage build    # hourly.parq, daily.parq, coverage.parq
.venv/bin/python -m unittest baselayer_data.austin_outage.test_labels
```

Everything is written under `dataset/austin-outage/`, which is gitignored.
Load the results with:

```python
from baselayer_data import load
hourly = load("austin-outage/hourly")
daily = load("austin-outage/daily")
```

## Outputs

| File | Grain | Contents |
| --- | --- | --- |
| `labels.parq` | labeled day | `oper_day`, `label`, `source`, `event`, `note` |
| `hourly.parq` | labeled day × hour | hourly features, 1–3 day lags of the daily features, label columns |
| `daily.parq` | labeled day | daily aggregates, their 1–3 day lags, label columns |
| `coverage.parq` | pulled day | share of hours each source is present (1.0 = complete) |
| `raw/` | per source | ERCOT data as downloaded (CSV columns as strings), see below |

Rows are keyed by `oper_day`, `hour_ending` (1–24, Central time, ERCOT's
convention) and `dst_flag` (the repeated hour on the fall-back day).

### Label columns

- `label`: `high`, `medium` or `low`.
- `source`: `outage_event`, `near_miss`, `event_window` (low days within 14
  days of a high event) or `random` (season-weighted sample).
- `event`: `uri_2021`, `mara_2023` or `microburst_2025` for high days, their
  event windows, and the medium days tied to them (day before, Uri's
  2021-02-19). Empty otherwise. Use it for leave-one-event-out splits.

### Hourly features

| Column | Source | Meaning |
| --- | --- | --- |
| `load_south_c_mw`, `load_total_mw` | NP6-345-CD | Actual load, South Central weather zone and ERCOT total |
| `fcst_south_c_mw`, `fcst_total_mw`, `fcst_model` | NP3-565-CD | In-use load forecast posted at or before 10:00 the day before |
| `fcst_err_{south_c,total}_{mw,pct}` | derived | actual − forecast (pct of forecast) |
| `offline_resource_mw`, `offline_irr_mw`, `offline_new_equip_mw` | NP3-233-CD | Capacity on outage (thermal/other, intermittent renewable, new equipment), statewide, from the last posting before the day starts |
| `offline_mw` | derived | `offline_resource_mw + offline_irr_mw` |
| `offline_south_mw` | NP3-233-CD | South zone share of `offline_mw`; only in the zonal file format (null for older days) |
| `offline_to_load` | derived | `offline_mw / load_total_mw` |
| `price_rt_mean`, `price_rt_max`, `price_intervals` | NP6-785-ER | Real-time settlement point price at `LZ_AEN` over the hour's 15-minute intervals |
| `lambda_mean`, `lambda_max`, `sced_runs` | NP6-322-CD | SCED system lambda over the hour's 5-minute runs |
| `month`, `day_of_week` | calendar | |

Daily columns are `<feature>_<agg>` (for example `price_rt_max_max`), and
lags add `_lag1`, `_lag2`, `_lag3` by calendar day.

## Where the data comes from

The spec's query endpoints (`/np6-345-cd/act_sys_load_by_wzn` and the rest)
return nothing before December 2023, so the 2021 and early-2023 events can't
come from them. Every source is pulled from ERCOT archives instead, which go
back to 2014:

| Source | How it's pulled |
| --- | --- |
| NP6-345-CD actual load | Every daily archive file in range (tiny), bulk-downloaded |
| NP3-565-CD load forecast | One archive posting per day: the latest at or before 10:00 the day before (the day-ahead view) |
| NP3-233-CD outage capacity | One archive posting per day: the latest before midnight |
| RT prices | The yearly *Historical RTM Load Zone and Hub Prices* workbooks (NP6-785-ER) from ercot.com, not the 15-minute NP6-905-CD archive (~96 files per day) |
| NP6-322-CD system lambda | Every 5-minute archive file for the needed days, bulk-downloaded |

Bulk download is `POST /api/public-reports/archive/{emil}/download` with up
to 1,000 `docIds`. All API calls are paced under 30 per minute.

## Label construction

- 8 `high` and 25 `medium` days exactly as the spec lists them.
- 13 restoration-only days (2023-02-03 to 02-12, 2025-05-30 to 06-01) are
  dropped.
- `low`: every unlabeled day within 14 days of a high event (67 days; fewer
  than the spec's ~75 because restoration days overlap the windows), plus 263
  random days from 2020-01-01 to 2026-09-18 with Jan, Feb, Jul, Aug and Sep
  weighted double. Seeded, so reruns give the same days.
- 2026-09-18 is the last day the 2026 price workbook covered when this was
  built.

## Caveats for modeling

- **Same-day actuals leak the label on load-shed days.** During Uri, actual
  load fell up to ~38% below forecast because ERCOT was shedding it. Same-day
  `load_*`, `fcst_err_*` and `offline_to_load` describe the outage, not the
  risk before it. For a forecast-style model, use the lag columns, the
  forecast, prices and offline capacity from the day before.
- **Storm outages don't show in ERCOT data.** Mara (2023) and the 2025
  microburst were local line damage while the statewide grid was fine. Only
  Uri is a grid supply event. Weather features (NOAA wind, ice) would be
  needed for the storm days.
- **Offline capacity by zone only exists in the newer file format.** Older
  days have system totals only, so `offline_south_mw` is null for them.
- **2026-07-13 (`medium`) rests on one secondary source.** ERCOT's TXANS page
  keeps no archive, and no ERCOT release was found for that date. 2024–2026
  searches found Weather Watches (2024-05-08, 2025-01-17, 2025-02-17,
  2026-01-21), which are below the spec's bar and are not labeled.
- **Labels are Austin Energy territory only.**
