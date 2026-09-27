# Demo cases

Two fixed sequences of real historical hours for demoing hour-level outage
risk. Each call runs the stored hours through the served model live; no
ERCOT data is pulled.

| Endpoint | Sequence | Hours |
| --- | --- | --- |
| `GET /v1/demo/case-1` | low, low, **medium**, **medium**, low | 5 hours, HE 14–18. All from the **held-out test split** (never seen in training): 2021-12-27 (low), the 2021-06-14 heat near-miss (medium), 2022-01-11 (low). |
| `GET /v1/demo/case-2` | low, low, low, **medium**, **high**, low | 6 hours, HE 13–18, following Winter Storm Uri: 2021-02-12/13 (low), 2021-02-14, the day before load shed (medium), 2021-02-15, the first load-shed day (high), 2021-02-21 (low, recovery). These are **training-split** hours: no held-out hour scores high. |

The model's risk barely changes within a real day, so each case stitches
hours from different days. Every hour keeps its real hour of day and is
one where the model's risk matches the day's true label.

Add `?include_features=true` to include each hour's model inputs.

## Output fields

Both cases return the same shape:

| Field | Meaning |
| --- | --- |
| `case` | `case_1` or `case_2` |
| `description` | What the sequence shows |
| `model_version` | Served run and model hash, e.g. `20260926-2006-ef94496938cc` |
| `thresholds` | Decision thresholds applied to each hour (see `risk`) |
| `sequence` | The hourly `risk` values in order, e.g. `["low", "low", "medium", "medium", "low"]` |
| `hourly` | One entry per hour, in order (below) |

Each `hourly` entry:

| Field | Meaning |
| --- | --- |
| `hour_ending` | Hour of the day, 1–24 in Central time (ERCOT convention: 17 is 4–5 pm) |
| `risk` | The model's call for the hour: `high` if `p_high >= thresholds.high`, else `medium` if `p_elevated >= thresholds.medium`, else `low` |
| `p_low`, `p_medium`, `p_high` | Model probabilities for each class; they sum to 1 |
| `p_elevated` | `p_medium + p_high`, the "at least medium" score |
| `source.oper_day`, `source.hour_ending` | The real operating day and hour the inputs come from (`hourly.parq`) |
| `source.label` | That day's true outage label |
| `source.event` | Named event the day belongs to (`uri_2021`, `mara_2023`, `microburst_2025`); omitted when none |
| `source.split` | `test` if the day was held out of training, `train` otherwise |
| `features` | Only with `include_features=true`: the 69 model inputs for the hour (`null` where the data had no value) |

The probabilities are compressed (`p_high` rarely exceeds 0.57), which is
why the thresholds sit well below 0.5. The thresholds were tuned on daily
averages of hourly scores; applying them to single hours matches how the
service rates each hour of a live day.

## Files

| File | Contents |
| --- | --- |
| `demo_cases.json` | The two cases: each hour's source, stored scores and 69 features |
| `test_set_outages.json` | Every medium and high day of the held-out test split, hour by hour, with the model's output. Only 2021-06-14 and 2021-06-15 are rated correctly (medium); the 2025 microburst's high days score low. |
| `make_demo.py` | Rebuilds both files from `$DATA_ROOT/dataset/austin-outage/hourly.parq` and fails if an hour no longer scores its expected risk: `.venv/bin/python -m baselayer_model.api.demo.make_demo` |

Rebuild the files after serving a new model; the fixture test below fails
when they no longer match it.

## Tests

[`test_demo.py`](test_demo.py) calls both routes over HTTP with the real
model. For each hour it checks the expected `risk` and probabilities, the
source hour and label, and the threshold rule. The expected values are
written out in the test, not read from `demo_cases.json`.

```sh
.venv/bin/python -m unittest baselayer_model.api.demo.test_demo                              # in-process app
DEMO_API_URL=http://127.0.0.1:8000 .venv/bin/python -m unittest baselayer_model.api.demo.test_demo  # a running service
```

A new model that changes any hour's outcome fails the suite; update
`EXPECTED` in the test, the cases in `make_demo.py`, and rebuild the files.
