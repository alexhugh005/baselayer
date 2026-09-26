"""Estimated residential load profiles for local analysis."""

from __future__ import annotations

import csv
import math
from datetime import date, datetime, timedelta, timezone
from pathlib import Path


def previous_complete_month_start(reference=None):
    reference = reference or date.today()
    first = reference.replace(day=1)
    return (first - timedelta(days=1)).replace(day=1)


def _raw_load(hour):
    morning = 1.8 * math.exp(-((hour - 7.5) / 2.0) ** 2)
    evening = 3.0 * math.exp(-((hour - 19.5) / 3.0) ** 2)
    overnight = 0.35 * math.exp(-((hour - 2.0) / 2.5) ** 2)
    return 0.25 + morning + evening + overnight


def estimate_household_usage(start_date=None, days=30, daily_kwh=30.0, interval_minutes=5):
    """Create a normalized residential profile at 5-minute intervals."""
    start_date = start_date or previous_complete_month_start()
    if isinstance(start_date, str):
        start_date = date.fromisoformat(start_date)
    if days <= 0 or daily_kwh <= 0 or interval_minutes not in (5, 15):
        raise ValueError("days and daily_kwh must be positive; interval must be 5 or 15")

    intervals_per_day = 24 * 60 // interval_minutes
    relative = [_raw_load(index * interval_minutes / 60) for index in range(intervals_per_day)]
    scale = daily_kwh / (sum(relative) * interval_minutes / 60)
    local_zone = timezone(timedelta(hours=-5))
    rows = []
    for day_offset in range(days):
        current = start_date + timedelta(days=day_offset)
        midnight = datetime.combine(current, datetime.min.time(), local_zone)
        for index, value in enumerate(relative):
            timestamp = midnight + timedelta(minutes=index * interval_minutes)
            estimated_kw = value * scale
            rows.append({
                "date": current.isoformat(),
                "timestamp": timestamp.isoformat(),
                "estimated_kw": round(estimated_kw, 6),
                "estimated_kwh": round(estimated_kw * interval_minutes / 60, 6),
            })
    return rows


def save_household_usage(path, **kwargs):
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    rows = estimate_household_usage(**kwargs)
    with destination.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    return destination
