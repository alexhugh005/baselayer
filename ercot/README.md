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

Dashboards are enough for live prices, reserves, supply/demand, and
system-wide energy storage. The Public Data API is the catalog of official
EMIL products (settlement point prices, forecasts, outages, archives).

ERCOT allows 30 Public Data API requests per minute and currently serves
those APIs from US addresses only.

## Register for the Public Data API

1. Open [API Explorer](https://apiexplorer.ercot.com/) and sign up.
2. Open **Products**, subscribe (for example "Public API"), then copy the
   **Primary key** from your profile.
3. Copy `.env.example` to `.env` and fill in email, password, and that key.

Token requests go to Azure AD B2C (`B2C_1_PUBAPI-ROPC-FLOW`). The id token
lasts one hour; this client requests a new one when it expires. Official
steps: [Registration and Authentication](https://developer.ercot.com/applications/pubapi/user-guide/registration-and-authentication/).

## Use it

From the repository root. Dashboards need no credentials:

```sh
python3 -m ercot status
python3 -m ercot dashboard prices
python3 -m ercot dashboard storage
```

Public Data API (requires `.env`):

```sh
python3 -m ercot token
python3 -m ercot products
python3 -m ercot product NP3-233-CD
python3 -m ercot data NP3-233-CD hourly_res_outage_cap
python3 -m ercot archive NP3-233-CD
python3 -m ercot data NP4-188-CD spp --param deliveryDateFrom=2026-09-01
```

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
python3 -m unittest ercot.test_ercot
```

Set `ERCOT_LIVE=1` to also hit live dashboard endpoints. Public Data API
calls still need credentials in `.env`.
