import json
import os
import unittest
from io import BytesIO
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch
from urllib.error import HTTPError

from ercot.client import (
    ErcotAuthError,
    ErcotConfigError,
    ErcotHttpError,
    PublicApi,
    load_config,
)
from ercot.dashboards import DashboardClient


class FakeResponse:
    def __init__(self, payload, status=200):
        if isinstance(payload, (dict, list)):
            body = json.dumps(payload).encode("utf-8")
        elif isinstance(payload, str):
            body = payload.encode("utf-8")
        else:
            body = payload
        self._body = body
        self.status = status

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


def http_error(url, status, payload):
    body = json.dumps(payload).encode("utf-8")
    return HTTPError(url, status, "error", hdrs=None, fp=BytesIO(body))


class ConfigTests(unittest.TestCase):
    def test_reads_env_file_and_prefers_process_env(self):
        with TemporaryDirectory() as tmp:
            path = Path(tmp) / ".env"
            path.write_text(
                "ERCOT_USERNAME=file-user\nERCOT_PASSWORD=file-pass\n"
                "ERCOT_SUBSCRIPTION_KEY=file-key\n",
                encoding="utf-8",
            )
            with patch.dict(
                os.environ,
                {"ERCOT_USERNAME": "env-user", "ERCOT_ENV_FILE": str(path)},
                clear=False,
            ):
                os.environ.pop("ERCOT_PASSWORD", None)
                os.environ.pop("ERCOT_SUBSCRIPTION_KEY", None)
                config = load_config()
        self.assertEqual(config["ERCOT_USERNAME"], "env-user")
        self.assertEqual(config["ERCOT_PASSWORD"], "file-pass")
        self.assertEqual(config["ERCOT_SUBSCRIPTION_KEY"], "file-key")


class PublicApiTests(unittest.TestCase):
    def setUp(self):
        self.api = PublicApi(
            username="user@example.com",
            password="secret",
            subscription_key="sub-key",
        )

    def test_missing_credentials(self):
        api = PublicApi()
        with self.assertRaises(ErcotConfigError):
            api.fetch_token()

    def test_fetch_token_posts_ropc_form_and_uses_id_token(self):
        captured = {}

        def fake_urlopen(request, timeout=None):
            captured["url"] = request.full_url
            captured["method"] = request.get_method()
            captured["content_type"] = request.headers.get("Content-type")
            captured["body"] = request.data.decode("utf-8")
            return FakeResponse(
                {
                    "access_token": "access",
                    "id_token": "id-abc",
                    "expires_in": "3600",
                    "token_type": "Bearer",
                }
            )

        with patch("ercot.client.urlopen", fake_urlopen):
            token = self.api.fetch_token()
        self.assertEqual(token, "id-abc")
        self.assertIn("B2C_1_PUBAPI-ROPC-FLOW", captured["url"])
        self.assertEqual(captured["method"], "POST")
        self.assertEqual(captured["content_type"], "application/x-www-form-urlencoded")
        self.assertIn("username=user%40example.com", captured["body"])
        self.assertIn("grant_type=password", captured["body"])
        self.assertIn("response_type=id_token", captured["body"])
        self.assertIn("client_id=fec253ea-0d06-4272-a5e6-b478baeecd70", captured["body"])

    def test_auth_error_on_failed_token(self):
        def fake_urlopen(request, timeout=None):
            raise http_error(
                request.full_url,
                400,
                {"error": "invalid_grant", "error_description": "bad password"},
            )

        with patch("ercot.client.urlopen", fake_urlopen):
            with self.assertRaises(ErcotAuthError) as ctx:
                self.api.fetch_token()
        self.assertIn("bad password", str(ctx.exception))

    def test_reuses_cached_token_until_expiry(self):
        calls = {"n": 0}

        def fake_urlopen(request, timeout=None):
            calls["n"] += 1
            if "b2clogin.com" in request.full_url:
                return FakeResponse({"id_token": "cached", "expires_in": "3600"})
            return FakeResponse({"ok": True})

        with patch("ercot.client.urlopen", fake_urlopen):
            self.api.products_page()
            self.api.products_page()
        self.assertEqual(calls["n"], 3)

    def test_public_reports_send_bearer_id_token_and_subscription_key(self):
        captured = {}

        def fake_urlopen(request, timeout=None):
            if "b2clogin.com" in request.full_url:
                return FakeResponse({"id_token": "id-xyz", "expires_in": "3600"})
            captured["url"] = request.full_url
            captured["authorization"] = request.headers.get("Authorization")
            captured["subscription"] = request.headers.get("Ocp-apim-subscription-key")
            return FakeResponse({"_embedded": {"products": [{"emilId": "NP3-233-CD"}]}})

        with patch("ercot.client.urlopen", fake_urlopen):
            page = self.api.products_page()
        self.assertEqual(captured["url"], "https://api.ercot.com/api/public-reports")
        self.assertEqual(captured["authorization"], "Bearer id-xyz")
        self.assertEqual(captured["subscription"], "sub-key")
        self.assertEqual(page["_embedded"]["products"][0]["emilId"], "NP3-233-CD")

    def test_product_paths_are_lowercased(self):
        urls = []

        def fake_urlopen(request, timeout=None):
            if "b2clogin.com" in request.full_url:
                return FakeResponse({"id_token": "t", "expires_in": "3600"})
            urls.append(request.full_url)
            return FakeResponse({})

        with patch("ercot.client.urlopen", fake_urlopen):
            self.api.product("NP3-233-CD")
            self.api.artifact("NP3-233-CD", "hourly_res_outage_cap", size=1)
            self.api.archive("NP3-233-CD")
        self.assertEqual(
            urls,
            [
                "https://api.ercot.com/api/public-reports/np3-233-cd",
                "https://api.ercot.com/api/public-reports/np3-233-cd/hourly_res_outage_cap?size=1",
                "https://api.ercot.com/api/public-reports/archive/np3-233-cd",
            ],
        )

    def test_products_follows_hal_next_links(self):
        pages = {
            "https://api.ercot.com/api/public-reports": {
                "_embedded": {"products": [{"emilId": "A"}]},
                "_links": {
                    "next": {"href": "https://api.ercot.com/api/public-reports?page=2"}
                },
            },
            "https://api.ercot.com/api/public-reports?page=2": {
                "_embedded": {"products": [{"emilId": "B"}]},
                "_links": {},
            },
        }

        def fake_urlopen(request, timeout=None):
            if "b2clogin.com" in request.full_url:
                return FakeResponse({"id_token": "t", "expires_in": "3600"})
            return FakeResponse(pages[request.full_url])

        with patch("ercot.client.urlopen", fake_urlopen):
            products = self.api.products()
        self.assertEqual([item["emilId"] for item in products], ["A", "B"])

    def test_http_error_includes_status(self):
        def fake_urlopen(request, timeout=None):
            if "b2clogin.com" in request.full_url:
                return FakeResponse({"id_token": "t", "expires_in": "3600"})
            raise http_error(
                request.full_url,
                401,
                {"statusCode": 401, "message": "Access denied due to missing subscription key."},
            )

        with patch("ercot.client.urlopen", fake_urlopen):
            with self.assertRaises(ErcotHttpError) as ctx:
                self.api.products_page()
        self.assertEqual(ctx.exception.status, 401)


class DashboardTests(unittest.TestCase):
    def test_named_feed_url_and_alias(self):
        urls = []

        def fake_urlopen(request, timeout=None):
            urls.append(request.full_url)
            self.assertEqual(request.headers.get("User-agent"), "baselayer-ercot/1.0")
            return FakeResponse({"lastUpdated": "now", "data": []})

        client = DashboardClient()
        with patch("ercot.client.urlopen", fake_urlopen):
            client.prices()
            client.get("storage")
        self.assertEqual(
            urls,
            [
                "https://www.ercot.com/api/1/services/read/dashboards/system-wide-prices.json",
                "https://www.ercot.com/api/1/services/read/dashboards/energy-storage-resources.json",
            ],
        )

    def test_grid_status_uses_latest_points(self):
        feeds = {
            "daily-prc.json": {
                "lastUpdated": "stamp",
                "current_condition": {
                    "state": "normal",
                    "title": "Normal Conditions",
                    "condition_note": "enough power",
                    "eea_level": 0,
                    "prc_value": "8,598",
                },
            },
            "system-wide-prices.json": {
                "rtSppData": [{"hbHubAvg": 10}, {"hbHubAvg": 44.85, "timestamp": "t-rt"}],
                "damSppData": [{"hbHubAvg": 20, "hourEnding": 24, "timestamp": "t-dam"}],
            },
            "supply-demand.json": {
                "data": [
                    {"capacity": 1, "demand": 1, "forecast": 0},
                    {"capacity": 80000, "demand": 63000, "forecast": 0, "timestamp": "t-sd"},
                    {"capacity": 90000, "demand": 1, "forecast": 1, "timestamp": "future"},
                ]
            },
            "energy-storage-resources.json": {
                "currentDay": {
                    "data": [
                        {"netOutput": 0},
                        {
                            "timestamp": "t-es",
                            "totalCharging": -500,
                            "totalDischarging": 200,
                            "netOutput": -300,
                        },
                    ]
                }
            },
        }

        def fake_urlopen(request, timeout=None):
            name = request.full_url.rsplit("/", 1)[-1]
            return FakeResponse(feeds[name])

        with patch("ercot.client.urlopen", fake_urlopen):
            status = DashboardClient().grid_status()
        self.assertEqual(status["condition"]["state"], "normal")
        self.assertEqual(status["realTimeSpp"]["hbHubAvg"], 44.85)
        self.assertEqual(status["supplyDemand"]["demandMw"], 63000)
        self.assertEqual(status["energyStorage"]["netOutputMw"], -300)


@unittest.skipUnless(os.environ.get("ERCOT_LIVE") == "1", "set ERCOT_LIVE=1")
class LiveDashboardTests(unittest.TestCase):
    def test_prc_feed(self):
        payload = DashboardClient().prc()
        self.assertIn("current_condition", payload)
        self.assertIn("state", payload["current_condition"])


if __name__ == "__main__":
    unittest.main()
