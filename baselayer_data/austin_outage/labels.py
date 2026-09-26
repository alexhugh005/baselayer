"""Day labels for the Austin grid outage classifier (see README.md)."""

from __future__ import annotations

import datetime as dt

import numpy as np
import pandas as pd

from ..storage import save

START = dt.date(2020, 1, 1)
# Last day covered by every source; the yearly RTM price file for 2026 was
# posted 2026-09-20.
END = dt.date(2026, 9, 18)
LOW_TARGET = 330
WINDOW_DAYS = 14
LAG_DAYS = 3
SEED = 20260926


def _days(first, last=None):
    first = dt.date.fromisoformat(first)
    last = dt.date.fromisoformat(last) if last else first
    return [first + dt.timedelta(days=i) for i in range((last - first).days + 1)]


# Outage onset days: 25,000+ Austin Energy customers out at once or
# ERCOT-ordered load shed.
HIGH_EVENTS = {
    "uri_2021": _days("2021-02-15", "2021-02-18"),
    "mara_2023": _days("2023-02-01", "2023-02-02"),
    "microburst_2025": _days("2025-05-28", "2025-05-29"),
}

# (dates, trigger, event it belongs to for leave-one-event-out splits)
MEDIUM_DAYS = [
    (_days("2026-07-13"), "voluntary conservation notice (single secondary source)", None),
    (_days("2025-05-27"), "day before microburst", "microburst_2025"),
    (_days("2024-01-15", "2024-01-16"), "winter conservation appeal (Heather)", None),
    (_days("2023-09-06"), "EEA2", None),
    (_days("2023-09-07"), "conservation appeal", None),
    (
        [dt.date(2023, 8, d) for d in (17, 20, 24, 25, 26, 27, 29, 30)],
        "summer conservation appeal",
        None,
    ),
    (_days("2023-06-20"), "conservation appeal", None),
    (_days("2023-01-31"), "day before ice storm outages", "mara_2023"),
    ([dt.date(2022, 7, 11), dt.date(2022, 7, 13)], "summer conservation appeal", None),
    (_days("2021-06-14", "2021-06-18"), "conservation alert after plant outages", None),
    (_days("2021-02-19"), "Uri emergency still active", "uri_2021"),
    (_days("2021-02-14"), "day before Uri load shed", "uri_2021"),
]

# Restoration-only days: outages continuing but not starting. Dropped.
EXCLUDED = _days("2023-02-03", "2023-02-12") + _days("2025-05-30", "2025-06-01")

# Hot and cold months count double when sampling normal days.
MONTH_WEIGHTS = {1: 2, 2: 2, 7: 2, 8: 2, 9: 2}


def build_labels(start=START, end=END, low_target=LOW_TARGET, seed=SEED):
    """Return one row per labeled day: oper_day, label, source, event, note."""
    rows = []
    for event, days in HIGH_EVENTS.items():
        rows += [(d, "high", "outage_event", event, "") for d in days]
    for days, note, event in MEDIUM_DAYS:
        rows += [(d, "medium", "near_miss", event, note) for d in days]
    taken = {r[0] for r in rows} | set(EXCLUDED)

    for event, days in HIGH_EVENTS.items():
        first = min(days) - dt.timedelta(days=WINDOW_DAYS)
        last = max(days) + dt.timedelta(days=WINDOW_DAYS)
        for d in pd.date_range(first, last).date:
            if d not in taken:
                rows.append((d, "low", "event_window", event, ""))
                taken.add(d)

    pool = [d for d in pd.date_range(start, end).date if d not in taken]
    weights = np.array([MONTH_WEIGHTS.get(d.month, 1) for d in pool], dtype=float)
    remaining = low_target - sum(r[1] == "low" for r in rows)
    rng = np.random.default_rng(seed)
    picks = rng.choice(len(pool), size=remaining, replace=False, p=weights / weights.sum())
    rows += [(pool[i], "low", "random", None, "") for i in sorted(picks)]

    labels = pd.DataFrame(rows, columns=["oper_day", "label", "source", "event", "note"])
    labels = labels.sort_values("oper_day").reset_index(drop=True)
    assert labels["oper_day"].is_unique
    return labels


def save_labels(labels, out):
    return save(labels, "labels", root=out)


def pull_days(labels, lag_days=LAG_DAYS):
    """Labeled days plus the `lag_days` before each, sorted."""
    days = set()
    for d in labels["oper_day"]:
        days.update(d - dt.timedelta(days=i) for i in range(lag_days + 1))
    return sorted(days)
