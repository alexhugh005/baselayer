# ERCOT API connector

Python client for the ERCOT Public Data API and the public grid-condition
JSON feeds. Register and subscribe at the
[ERCOT Public API Applications](https://www.ercot.com/services/mdt/data-portal)
page, then use this folder to talk to the APIs.

## Two APIs

| Surface | Auth | What it is |
| --- | --- | --- |
| Public dashboards | none | Live grid JSON used by [Grid and Market Conditions](https://www.ercot.com/gridmktinfo/dashboards) |
| Public Data API | Azure B2C id token + APIM subscription key | EMIL reports at `https://api.ercot.com/api/public-reports` |
| ESR API | Azure B2C id token + its own APIM subscription key | Energy storage four-second data at `https://api.ercot.com/api/public-data` |

Dashboards are enough for live prices, reserves, supply/demand, and
system-wide energy storage. The Public Data API is the catalog of official
EMIL products (settlement point prices, forecasts, outages, archives).

ERCOT allows 30 Public Data API requests per minute and currently serves
those APIs from US addresses only.

## Register for the Public Data API

1. Open [API Explorer](https://apiexplorer.ercot.com/) and sign up.
2. Open **Products** and subscribe to "Public API". For energy storage
   four-second data, also subscribe to "Energy Storage Resource (ESR) API".
3. Copy `.env.example` to `.env` and fill in email, password, and each
   subscription's **Primary key** from your profile:
   `ERCOT_PUBLIC_API_SUBSCRIPTION_KEY` for `/api/public-reports` and
   `ERCOT_ESR_API_SUBSCRIPTION_KEY` for `/api/public-data`. Each key only
   works on its own API; the client picks the key from the request path.
   `ERCOT_SUBSCRIPTION_KEY` is still read as the Public API key.

Token requests go to Azure AD B2C (`B2C_1_PUBAPI-ROPC-FLOW`). The id token
lasts one hour; this client requests a new one when it expires. Official
steps: [Registration and Authentication](https://developer.ercot.com/applications/pubapi/user-guide/registration-and-authentication/).

## Use it

From the repository root. Dashboards need no credentials:

```sh
python3 -m ercot status
python3 -m ercot dashboard prices
python3 -m ercot dashboard storage
python3 -m ercot datasets
python3 -m ercot daily-usage
python3 ercot/predict_outage.py
python3 -m ercot outage-forecast --minutes 60
```

Running `python3 ercot/predict_outage.py` from the repository root writes the grid outage prediction. Pressing Run on `ercot/predict_outage.py` does the same thing. The file saves the next 60 minutes to `dataset/ercot/outage-forecast-minutes.csv`. Extra arguments are passed through, including `--no-model`.

`outage-forecast` retrieves historical real-time price and weather regimes that resemble the latest interval, then writes one row per future minute. `outage_likelihood` is the chance that ERCOT generation outage is at or above the historical 90th percentile of realized NP3-233-CD hourly totals. With `XAI_API_KEY` set, Grok 4.7 (`grok-4.7`) scores that chance from the retrieved episodes. Without a key, or with `--no-model`, the value is the distance-weighted share of those episodes that were elevated at the same lead. `retrieval_likelihood` always keeps that share. Each minute is also annotated with linked hazards: heavy rain, storm, tornado, hurricane or tropical storm, construction, and other disaster reports. Hourly city weather is cached from Open-Meteo and Texas storm reports from NOAA under `dataset/ercot/weather/`. Optional construction or local disaster rows can be added as `dataset/ercot/weather/local-events.csv` with columns `start,end,event_type,region,summary`. The megawatt path is the hourly generation total, repeated across the minutes of that hour.

`datasets` writes each live feed to `dataset/dashboards/`, a compact snapshot to `dataset/grid-status.json`, and an actual-demand summary to `dataset/daily-usage.json`. The summary excludes forecast intervals and reports partial-day coverage. That folder is gitignored.

Official historical daily reports are available as NP6-344-CD (study area), NP6-345-CD (weather zone), and NP6-346-CD (forecast zone) through the [ERCOT load data page](https://www.ercot.com/gridinfo/load).

Public Data API (requires `.env`):

```sh
python3 -m ercot token
python3 -m ercot products
python3 -m ercot product NP3-233-CD
python3 -m ercot data NP3-233-CD hourly_res_outage_cap
python3 -m ercot archive NP3-233-CD
python3 -m ercot data NP4-188-CD spp --param deliveryDateFrom=2026-09-01
python3 -m ercot download NP4-188-CD spp
python3 -m ercot download NP3-233-CD --limit 5
```

`download` with an artifact name saves a CSV (or `--format json`) under
`dataset/<emil-id>/`. Without an artifact name it saves posted archive
files for that product, paced to stay under the 30-request-per-minute cap.
Files that are already present are left in place.

In code:

```python
from ercot import DashboardClient, PublicApi

status = DashboardClient().grid_status()
api = PublicApi.from_env()
products = api.products()
rows = api.artifact("NP4-188-CD", "spp")
```

## Tests

```sh
python3 -m unittest ercot.test_ercot ercot.test_outage_rag
```

Set `ERCOT_LIVE=1` to also hit live dashboard endpoints. Public Data API
calls still need credentials in `.env`.
