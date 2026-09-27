"""FastAPI service for the Austin outage risk model.

    uvicorn baselayer_model.api.app:app --host 0.0.0.0 --port 8000

GET /v1/outage-risk rates an operating day (default: today, Central time)
low / medium / high. The first request for a day pulls ERCOT data (a minute
or two, paced under ERCOT's rate limit) and caches the result in memory and
under $DATA_ROOT/serve/. A background task keeps today's result current.

GET /v1/demo/case-1 and /v1/demo/case-2 score fixed sequences of real
historical hours (demo/demo_cases.json) for demos; no ERCOT calls.

Environment:
    MODEL_DIR         run directory with model.json, run_config.json and
                      classification_thresholds.json (default: ./model)
    DATA_ROOT         where raw pulls, features and results are kept
    REFRESH_SECONDS   background refresh period for today; 0 disables (900)
    ERCOT_*           ERCOT Public API credentials (see baselayer_data/ercot)
"""

from __future__ import annotations

import asyncio
import datetime as dt
import functools
import json
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path
from zoneinfo import ZoneInfo

from fastapi import FastAPI, HTTPException, Query
from fastapi.concurrency import run_in_threadpool
from pydantic import BaseModel

from baselayer_data.paths import data_root
from baselayer_model.api import demo, live_features
from baselayer_model.api.artifacts import DEFAULT_DIR, load_run
from baselayer_model.api.predictor import score_day

log = logging.getLogger("baselayer_model.api")

CENTRAL = ZoneInfo("America/Chicago")
# Yesterday's actual load posts around 05:50; after this a complete result is final.
FINAL_AFTER = dt.time(7, 0)
# Incomplete results are retried at most this often.
RETRY_AFTER = dt.timedelta(hours=1)
# Bump when the stored result shape changes, so older cached results are recomputed.
RESULT_SCHEMA = 2


class HourlyRisk(BaseModel):
    hour_ending: int
    dst_flag: bool
    risk: str
    p_low: float
    p_medium: float
    p_high: float
    p_elevated: float


class OutageRisk(BaseModel):
    oper_day: dt.date
    risk: str
    p_low: float
    p_medium: float
    p_high: float
    p_elevated: float
    thresholds: dict[str, float]
    degraded: bool
    missing_features: list[str]
    model_version: str
    generated_at: dt.datetime
    data_as_of: dict[str, str | None]
    pull_errors: dict[str, str]
    stale: bool = False
    hourly: list[HourlyRisk]


class DemoSource(BaseModel):
    oper_day: dt.date
    hour_ending: int
    label: str
    event: str | None
    split: str


class DemoHour(BaseModel):
    hour_ending: int
    risk: str
    p_low: float
    p_medium: float
    p_high: float
    p_elevated: float
    source: DemoSource
    features: dict[str, float | None] | None = None


class DemoCase(BaseModel):
    case: str
    description: str
    model_version: str
    thresholds: dict[str, float]
    sequence: list[str]
    hourly: list[DemoHour]


def now_central():
    return dt.datetime.now(CENTRAL)


@functools.cache
def ercot_api():
    """One paced client, so every pull shares its rate limit and token."""
    from baselayer_data.ercot_pull import PacedApi

    return PacedApi.from_env()


def default_fetch(raw, day):
    return live_features.pull_day(ercot_api(), raw, day)


class RiskService:
    def __init__(self, run, root, fetch=default_fetch, build=live_features.build_day, clock=now_central):
        self.run = run
        self.root = Path(root)
        self.fetch = fetch
        self.build = build
        self.clock = clock
        self.results = {}
        # One ERCOT pull at a time keeps every call under the shared rate limit.
        self.lock = asyncio.Lock()
        self.last_refresh = None
        self.last_error = None

    def _result_path(self, day):
        return self.root / "results" / f"{day}.json"

    def cached(self, day):
        if day not in self.results:
            path = self._result_path(day)
            if path.exists():
                self.results[day] = json.loads(path.read_text())
        return self.results.get(day)

    def usable(self, result):
        return (
            result is not None
            and result.get("schema") == RESULT_SCHEMA
            and result["model_version"] == self.run.version
        )

    def fresh(self, result, day):
        if not self.usable(result):
            return False
        generated = dt.datetime.fromisoformat(result["generated_at"])
        final_from = dt.datetime.combine(day, FINAL_AFTER, CENTRAL)
        if not result["degraded"] and generated >= final_from:
            return True
        return self.clock() - generated < RETRY_AFTER

    def compute(self, day):
        raw = self.root / "raw" / str(day)
        pull_errors = self.fetch(raw, day) or {}
        rows = self.build(day, raw)
        features = self.root / "features" / f"{day}.parq"
        features.parent.mkdir(parents=True, exist_ok=True)
        rows.to_parquet(features, index=False)

        result = score_day(rows, self.run)
        result.update(
            schema=RESULT_SCHEMA,
            generated_at=self.clock().isoformat(),
            data_as_of=live_features.postings(raw, day),
            pull_errors=pull_errors,
        )
        path = self._result_path(day)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(result, indent=2) + "\n")
        self.results[day] = result
        log.info("%s: %s (p_high=%.3f, degraded=%s)", day, result["risk"], result["p_high"], result["degraded"])
        return result

    async def risk(self, day, refresh=False):
        hit = self.cached(day)
        if not refresh and self.fresh(hit, day):
            return hit
        async with self.lock:
            hit = self.cached(day)
            if not refresh and self.fresh(hit, day):
                return hit
            try:
                return await run_in_threadpool(self.compute, day)
            except Exception as exc:
                log.exception("scoring %s failed", day)
                if self.usable(hit):
                    return {**hit, "stale": True}
                raise HTTPException(503, f"could not score {day}: {type(exc).__name__}: {exc}") from exc

    async def refresh_forever(self, seconds):
        while True:
            try:
                await self.risk(self.clock().date())
                self.last_refresh, self.last_error = self.clock().isoformat(), None
            except Exception as exc:
                self.last_error = str(getattr(exc, "detail", exc))
            await asyncio.sleep(seconds)


def create_app(run_dir=None, root=None, fetch=default_fetch, build=live_features.build_day,
               clock=now_central, refresh_seconds=None):
    run_dir = run_dir or os.environ.get("MODEL_DIR") or DEFAULT_DIR
    if refresh_seconds is None:
        refresh_seconds = int(os.environ.get("REFRESH_SECONDS", "900"))

    @asynccontextmanager
    async def lifespan(app):
        logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
        run = load_run(run_dir)
        log.info("loaded model %s (%d features)", run.version, len(run.features))
        service = RiskService(run, root or data_root() / "serve", fetch, build, clock)
        app.state.service = service
        task = asyncio.create_task(service.refresh_forever(refresh_seconds)) if refresh_seconds > 0 else None
        yield
        if task:
            task.cancel()

    app = FastAPI(title="Austin outage risk", lifespan=lifespan)

    @app.get("/health")
    async def health():
        service = app.state.service
        return {
            "status": "ok",
            "model_version": service.run.version,
            "features": len(service.run.features),
            "last_refresh": service.last_refresh,
            "last_error": service.last_error,
        }

    @app.get("/v1/outage-risk", response_model=OutageRisk)
    async def outage_risk(
        day: dt.date | None = Query(None, description="operating day (default: today, Central time)"),
        refresh: bool = Query(False, description="ignore the cached result"),
    ):
        service = app.state.service
        today = service.clock().date()
        day = day or today
        if day > today + dt.timedelta(days=1):
            raise HTTPException(422, "the day-ahead forecast only reaches tomorrow")
        return await service.risk(day, refresh)

    features_query = Query(False, description="include each hour's 69 model inputs")

    @app.get("/v1/demo/case-1", response_model=DemoCase, response_model_exclude_none=True)
    async def demo_case_1(include_features: bool = features_query):
        """Low, low, medium, medium, low: held-out test hours around the June 2021 near-miss."""
        return demo.score_case("case_1", app.state.service.run, include_features)

    @app.get("/v1/demo/case-2", response_model=DemoCase, response_model_exclude_none=True)
    async def demo_case_2(include_features: bool = features_query):
        """Low, low, low, medium, high, low: Winter Storm Uri (training hours)."""
        return demo.score_case("case_2", app.state.service.run, include_features)

    return app


app = create_app()
