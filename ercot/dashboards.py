"""Unauthenticated ERCOT public dashboard JSON feeds.

These power https://www.ercot.com/gridmktinfo/dashboards and do not use
the Public Data API subscription key.
"""

from __future__ import annotations

from pathlib import Path

from .client import http_json, write_json

DASHBOARD_BASE = "https://www.ercot.com/api/1/services/read/dashboards"

DASHBOARDS = {
    "prc": "daily-prc",
    "supply-demand": "supply-demand",
    "prices": "system-wide-prices",
    "fuel-mix": "fuel-mix",
    "storage": "energy-storage-resources",
    "wind-solar": "combine-wind-solar",
    "outages": "generation-outages",
    "ancillary": "ancillary-services",
    "dc-ties": "dc-tie-flows",
    "weather": "weather-forecast",
}


class DashboardClient:
    def __init__(self, base=DASHBOARD_BASE, timeout=30):
        self.base = base.rstrip("/")
        self.timeout = timeout

    def get(self, name):
        slug = DASHBOARDS.get(name, name).removesuffix(".json")
        return http_json("GET", f"{self.base}/{slug}.json", timeout=self.timeout)

    def prc(self):
        return self.get("daily-prc")

    def supply_demand(self):
        return self.get("supply-demand")

    def prices(self):
        return self.get("system-wide-prices")

    def fuel_mix(self):
        return self.get("fuel-mix")

    def storage(self):
        return self.get("energy-storage-resources")

    def wind_solar(self):
        return self.get("combine-wind-solar")

    def outages(self):
        return self.get("generation-outages")

    def ancillary(self):
        return self.get("ancillary-services")

    def dc_ties(self):
        return self.get("dc-tie-flows")

    def weather(self):
        return self.get("weather-forecast")

    @staticmethod
    def _latest(rows, actual_key="forecast"):
        rows = rows or []
        actuals = [row for row in rows if row.get(actual_key) in (0, "0", None, "N")]
        chosen = actuals or rows
        return chosen[-1] if chosen else {}

    def grid_status(self):
        """Compact snapshot for household / battery control."""
        prc = self.prc()
        prices = self.prices()
        supply = self.supply_demand()
        storage = self.storage()
        condition = prc.get("current_condition") or {}
        rt = (prices.get("rtSppData") or [{}])[-1]
        dam = (prices.get("damSppData") or [{}])[-1]
        latest_supply = self._latest(supply.get("data"))
        current_storage = self._latest((storage.get("currentDay") or {}).get("data"))
        return {
            "lastUpdated": prc.get("lastUpdated"),
            "condition": {
                "state": condition.get("state"),
                "title": condition.get("title"),
                "note": condition.get("condition_note"),
                "eeaLevel": condition.get("eea_level"),
                "prcMw": condition.get("prc_value"),
            },
            "realTimeSpp": {
                "timestamp": rt.get("timestamp"),
                "hbHubAvg": rt.get("hbHubAvg"),
                "hbHouston": rt.get("hbHouston"),
                "hbNorth": rt.get("hbNorth"),
                "hbSouth": rt.get("hbSouth"),
                "hbWest": rt.get("hbWest"),
                "lzHouston": rt.get("lzHouston"),
                "lzNorth": rt.get("lzNorth"),
                "lzSouth": rt.get("lzSouth"),
                "lzWest": rt.get("lzWest"),
            },
            "dayAheadSpp": {
                "timestamp": dam.get("timestamp"),
                "hourEnding": dam.get("hourEnding"),
                "hbHubAvg": dam.get("hbHubAvg"),
            },
            "supplyDemand": {
                "timestamp": latest_supply.get("timestamp"),
                "capacityMw": latest_supply.get("capacity"),
                "demandMw": latest_supply.get("demand"),
            },
            "energyStorage": {
                "timestamp": current_storage.get("timestamp"),
                "totalChargingMw": current_storage.get("totalCharging"),
                "totalDischargingMw": current_storage.get("totalDischarging"),
                "netOutputMw": current_storage.get("netOutput"),
            },
        }

    def save_datasets(self, directory):
        """Write each live dashboard feed and a grid snapshot under `directory`."""
        directory = Path(directory)
        written = []
        for name in DASHBOARDS:
            written.append(
                write_json(directory / "dashboards" / f"{name}.json", self.get(name))
            )
        written.append(write_json(directory / "grid-status.json", self.grid_status()))
        return written
