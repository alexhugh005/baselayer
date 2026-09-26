"""Retrieval forecast of ERCOT generation-outage likelihood, linked to weather.

Real-time settlement prices and hourly weather are the retrieval corpus. Each
15-minute state is matched to historical regimes with a similar price path and
the same hazard labels. The likelihood for a future minute is the chance that
realized generation outage is at or above the historical 90th percentile.
Grok 4.7 scores that chance from the retrieved episodes when an API key is set.
Without a key, the score is the distance-weighted frequency of those episodes.

Outage megawatts come from NP3-233-CD and change on the hour. The forecast is
still written once a minute. Weather hazards come from Open-Meteo hourly
observations at four ERCOT cities and from NOAA Storm Events reports in Texas.
"""

from __future__ import annotations

import bisect
import csv
import gzip
import heapq
import io
import json
import math
import re
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

GROK_MODEL = "grok-4.7"
ELEVATED_QUANTILE = 0.90
LIKELIHOOD_CHUNK = 120

HAZARD_TYPES = (
    "heavy_rain",
    "storm",
    "tornado",
    "hurricane",
    "construction",
    "disaster",
)

HAZARD_LABELS = {
    "heavy_rain": "heavy rain",
    "storm": "storms",
    "tornado": "tornadoes",
    "hurricane": "hurricanes or tropical storms",
    "construction": "construction",
    "disaster": "other disasters",
}

# NOAA event names that correspond to the hazard labels above.
# Tropical storms stay in the hurricane bucket; the original NOAA name is kept
# in the event summary so a tropical storm is not described as a hurricane.
EVENT_HAZARDS = {
    "Heavy Rain": ("heavy_rain",),
    "Flash Flood": ("heavy_rain", "disaster"),
    "Flood": ("disaster",),
    "Coastal Flood": ("disaster",),
    "Thunderstorm Wind": ("storm",),
    "Marine Thunderstorm Wind": ("storm",),
    "Lightning": ("storm",),
    "Hail": ("storm",),
    "High Wind": ("storm",),
    "Strong Wind": ("storm",),
    "Tornado": ("tornado",),
    "Hurricane": ("hurricane",),
    "Hurricane (Typhoon)": ("hurricane",),
    "Tropical Storm": ("hurricane",),
    "Tropical Depression": ("hurricane",),
    "Storm Surge/Tide": ("hurricane",),
    "Wildfire": ("disaster",),
    "Debris Flow": ("disaster",),
    "Dust Storm": ("disaster",),
    "Ice Storm": ("disaster",),
    "Blizzard": ("disaster",),
    "Winter Storm": ("disaster",),
    "Sleet": ("disaster",),
    "Excessive Heat": ("disaster",),
    "Dense Smoke": ("disaster",),
    "Construction": ("construction",),
}

LOAD_ZONES = ("LZ_HOUSTON", "LZ_NORTH", "LZ_SOUTH", "LZ_WEST")
HUB = "HB_HUBAVG"
HEAVY_RAIN_MM = 7.6
STORM_GUST_MS = 26.0
THUNDER_CODES = {95, 96, 99}
HEAVY_RAIN_CODES = {65, 67, 82}

ZONE_COORDS = {
    "Houston": (29.7604, -95.3698),
    "North": (32.7767, -96.7970),
    "South": (29.4241, -98.4936),
    "West": (31.9973, -102.0779),
}

STORM_INDEX = "https://www.ncei.noaa.gov/pub/data/swdi/stormevents/csvfiles/"
_POSTED_AT = re.compile(r"\.(\d{8})\.(\d{6})\.")
_MONTHS = {
    "JAN": 1, "FEB": 2, "MAR": 3, "APR": 4, "MAY": 5, "JUN": 6,
    "JUL": 7, "AUG": 8, "SEP": 9, "OCT": 10, "NOV": 11, "DEC": 12,
}


def classify_weather_hour(precip_mm, weather_code, gust_ms):
    """Map one hour of city weather onto heavy rain and storm labels.

    Tornado, hurricane, construction, and other disaster labels are not inferred
    from a gust or a weather code. Those come from named event reports.
    """
    flags = set()
    precip = float(precip_mm or 0)
    gust = float(gust_ms or 0)
    code = int(weather_code or 0)
    if precip >= HEAVY_RAIN_MM or code in HEAVY_RAIN_CODES:
        flags.add("heavy_rain")
    if code in THUNDER_CODES or gust >= STORM_GUST_MS:
        flags.add("storm")
    return flags


def hazards_for_event_type(event_type):
    return tuple(EVENT_HAZARDS.get((event_type or "").strip(), ()))


def _request_json(url, timeout=90):
    request = Request(url, headers={"User-Agent": "baselayer-ercot/1.0"})
    with urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def _request_bytes(url, timeout=180):
    request = Request(url, headers={"User-Agent": "baselayer-ercot/1.0"})
    with urlopen(request, timeout=timeout) as response:
        return response.read()


def fetch_hourly_weather(path, start="2025-08-01", end="2026-08-31"):
    """Save hourly rain, gust, and weather code for four ERCOT cities."""
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    by_time = {}
    for zone, (latitude, longitude) in ZONE_COORDS.items():
        url = (
            "https://archive-api.open-meteo.com/v1/archive"
            f"?latitude={latitude}&longitude={longitude}"
            f"&start_date={start}&end_date={end}"
            "&hourly=precipitation,weather_code,wind_gusts_10m"
            "&timezone=America%2FChicago"
        )
        payload = _request_json(url)
        hourly = payload.get("hourly") or {}
        times = hourly.get("time") or []
        if not times:
            reason = payload.get("reason") or "no hourly weather returned"
            raise ValueError(f"Open-Meteo weather for {zone} failed: {reason}")
        precip = hourly.get("precipitation") or []
        codes = hourly.get("weather_code") or []
        gusts = hourly.get("wind_gusts_10m") or []
        for index, stamp in enumerate(times):
            moment = datetime.fromisoformat(stamp)
            row = by_time.setdefault(moment, {})
            row[f"{zone}_precip_mm"] = float(precip[index] or 0) if index < len(precip) else 0.0
            row[f"{zone}_weather_code"] = int(codes[index] or 0) if index < len(codes) else 0
            row[f"{zone}_gust_ms"] = float(gusts[index] or 0) if index < len(gusts) else 0.0

    fieldnames = ["time", "precip_mm", "wind_gust_ms", "weather_code"]
    for zone in ZONE_COORDS:
        fieldnames.extend((f"{zone}_precip_mm", f"{zone}_weather_code", f"{zone}_gust_ms"))
    with destination.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for moment in sorted(by_time):
            zones = by_time[moment]
            precip_values = [zones.get(f"{zone}_precip_mm", 0.0) for zone in ZONE_COORDS]
            gust_values = [zones.get(f"{zone}_gust_ms", 0.0) for zone in ZONE_COORDS]
            best = max(ZONE_COORDS, key=lambda zone: zones.get(f"{zone}_precip_mm", 0.0))
            writer.writerow({
                "time": moment.isoformat(timespec="minutes"),
                "precip_mm": max(precip_values),
                "wind_gust_ms": max(gust_values),
                "weather_code": zones.get(f"{best}_weather_code", 0),
                **zones,
            })
    return destination


def _parse_noaa_time(value):
    date_part, time_part = value.strip().split()
    day, month, year = date_part.split("-")
    hour, minute, second = (time_part.split(":") + ["0", "0"])[:3]
    year = int(year)
    if year < 100:
        year += 2000 if year < 70 else 1900
    return datetime(year, _MONTHS[month.upper()], int(day), int(hour), int(minute), int(second))


def _storm_detail_urls(years):
    index = _request_bytes(STORM_INDEX, timeout=60).decode("utf-8", "replace")
    urls = {}
    for year in years:
        matches = re.findall(
            rf"StormEvents_details-ftp_v1\.0_d{year}_c(\d+)\.csv\.gz",
            index,
        )
        if not matches:
            continue
        stamp = sorted(set(matches))[-1]
        name = f"StormEvents_details-ftp_v1.0_d{year}_c{stamp}.csv.gz"
        urls[year] = STORM_INDEX + name
    if not urls:
        raise ValueError("NOAA storm-event index did not list the requested years")
    return urls


def fetch_texas_storm_events(path, years=(2025, 2026)):
    """Save Texas NOAA storm reports that map onto the hazard labels."""
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    rows = []
    for url in _storm_detail_urls(years).values():
        payload = _request_bytes(url)
        with gzip.open(io.BytesIO(payload), "rt", newline="", encoding="utf-8", errors="replace") as handle:
            for record in csv.DictReader(handle):
                if (record.get("STATE") or "").upper() != "TEXAS":
                    continue
                event_type = (record.get("EVENT_TYPE") or "").strip()
                hazards = hazards_for_event_type(event_type)
                if not hazards:
                    continue
                try:
                    start = _parse_noaa_time(record.get("BEGIN_DATE_TIME") or "")
                    end = _parse_noaa_time(record.get("END_DATE_TIME") or "")
                except (KeyError, ValueError):
                    continue
                if end <= start:
                    end = start + timedelta(minutes=15)
                region = (record.get("CZ_NAME") or "").strip()
                narrative = (record.get("EVENT_NARRATIVE") or "").replace("\n", " ").strip()
                summary = f"{event_type} in {region}" if region else event_type
                if narrative:
                    summary = f"{summary}: {narrative[:160]}"
                rows.append({
                    "start": start.isoformat(sep=" ", timespec="minutes"),
                    "end": end.isoformat(sep=" ", timespec="minutes"),
                    "event_type": event_type,
                    "region": region,
                    "summary": summary,
                    "hazards": "|".join(hazards),
                })
    rows.sort(key=lambda item: item["start"])
    with destination.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=("start", "end", "event_type", "region", "summary", "hazards"),
        )
        writer.writeheader()
        writer.writerows(rows)
    return destination


def prepare_weather(directory, start="2025-08-01", end="2026-08-31", refresh=False):
    """Download hourly weather and Texas storm reports if they are not cached."""
    root = Path(directory)
    hourly = root / "hourly.csv"
    events = root / "texas-storm-events.csv"
    if refresh or not hourly.exists():
        fetch_hourly_weather(hourly, start, end)
    if refresh or not events.exists():
        fetch_texas_storm_events(events)
    return {"hourly": hourly, "events": events}


def load_prices(path):
    """Average duplicate settlement-point rows onto each 15-minute interval end."""
    totals = defaultdict(lambda: defaultdict(float))
    counts = defaultdict(lambda: defaultdict(int))
    with Path(path).open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            point = row.get("settlementPoint")
            if point not in LOAD_ZONES and point != HUB:
                continue
            try:
                day = date.fromisoformat(row["deliveryDate"])
                hour = int(row["deliveryHour"])
                interval = int(row["deliveryInterval"])
                price = float(row["settlementPointPrice"])
            except (KeyError, TypeError, ValueError):
                continue
            if not 1 <= hour <= 24 or not 1 <= interval <= 4:
                continue
            # ERCOT deliveryHour is hour-ending. Interval 4 of hour 1 ends at 01:00.
            moment = datetime.combine(day, datetime.min.time()) + timedelta(
                hours=hour - 1, minutes=15 * interval
            )
            totals[moment][point] += price
            counts[moment][point] += 1
    prices = {}
    for moment, points in totals.items():
        averaged = {
            point: points[point] / counts[moment][point]
            for point in points
        }
        if all(zone in averaged for zone in LOAD_ZONES):
            if HUB not in averaged:
                averaged[HUB] = sum(averaged[zone] for zone in LOAD_ZONES) / len(LOAD_ZONES)
            prices[moment] = averaged
    return prices


def _hour_ending(day, hour_ending):
    return datetime.combine(day, datetime.min.time()) + timedelta(hours=int(hour_ending))


def _sum_zones(row, prefix):
    total = 0.0
    for zone in ("South", "North", "West", "Houston"):
        total += float(row.get(f"{prefix}Zone{zone}") or 0)
    return total


def load_realized_outages(directory):
    """Keep the latest report posted at or after each operating hour.

    A row dated after the file's post time is still a schedule, so it is not
    treated as the realized outage.
    """
    realized = {}
    paths = sorted(Path(directory).glob("*.csv"))
    for path in paths:
        match = _POSTED_AT.search(path.name)
        if not match:
            continue
        posted = datetime.strptime(match.group(1) + match.group(2), "%Y%m%d%H%M%S")
        with path.open(newline="", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                try:
                    day = datetime.strptime(row["Date"], "%m/%d/%Y").date()
                    hour_end = _hour_ending(day, row["HourEnding"])
                except (KeyError, TypeError, ValueError):
                    continue
                if hour_end > posted:
                    continue
                resource = _sum_zones(row, "TotalResourceMW")
                irr = _sum_zones(row, "TotalIRRMW")
                new_equip = _sum_zones(row, "TotalNewEquipResourceMW")
                realized[hour_end] = {
                    "total": resource + irr + new_equip,
                    "resource": resource,
                    "irr": irr,
                    "new_equip": new_equip,
                }
    return realized


def load_hourly_weather(path):
    weather = {}
    with Path(path).open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            moment = datetime.fromisoformat(row["time"])
            zones = {}
            for zone in ZONE_COORDS:
                precip_key = f"{zone}_precip_mm"
                if precip_key not in row or row[precip_key] == "":
                    continue
                zones[zone] = {
                    "precip_mm": float(row.get(precip_key) or 0),
                    "weather_code": int(float(row.get(f"{zone}_weather_code") or 0)),
                    "gust_ms": float(row.get(f"{zone}_gust_ms") or 0),
                }
            weather[moment] = {
                "precip_mm": float(row.get("precip_mm") or 0),
                "wind_gust_ms": float(row.get("wind_gust_ms") or 0),
                "weather_code": int(float(row.get("weather_code") or 0)),
                "zones": zones,
            }
    return weather


def load_hazard_events(path):
    events = []
    file_path = Path(path)
    if not file_path.exists():
        return events
    with file_path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            event_type = (row.get("event_type") or "").strip()
            named = [item for item in (row.get("hazards") or "").split("|") if item]
            hazards = tuple(named) or hazards_for_event_type(event_type)
            if not hazards and event_type in HAZARD_TYPES:
                hazards = (event_type,)
            if not hazards:
                continue
            start = datetime.fromisoformat(row["start"])
            end_text = (row.get("end") or "").strip()
            end = datetime.fromisoformat(end_text) if end_text else start + timedelta(hours=1)
            if end <= start:
                end = start + timedelta(minutes=15)
            events.append({
                "start": start,
                "end": end,
                "event_type": event_type or hazards[0],
                "region": (row.get("region") or "").strip(),
                "summary": (row.get("summary") or event_type).strip(),
                "hazards": hazards,
            })
    return events


def _index_events(events):
    by_hour = defaultdict(lambda: {"flags": set(), "details": []})
    for event in events:
        cursor = event["start"].replace(minute=0, second=0, microsecond=0)
        steps = 0
        while cursor < event["end"] and steps < 24 * 8:
            bucket = by_hour[cursor]
            bucket["flags"].update(event["hazards"])
            if len(bucket["details"]) < 6 and event["summary"] not in bucket["details"]:
                bucket["details"].append(event["summary"])
            cursor += timedelta(hours=1)
            steps += 1
    return by_hour


def _weather_hour(moment):
    shifted = moment - timedelta(minutes=1)
    return shifted.replace(minute=0, second=0, microsecond=0)


def describe_hazards(moment, weather, event_index):
    hour = _weather_hour(moment)
    row = weather.get(hour)
    flags = set()
    details = []
    if row:
        flags |= classify_weather_hour(row["precip_mm"], row["weather_code"], row["wind_gust_ms"])
        zone_notes = []
        for zone, values in row["zones"].items():
            zone_flags = classify_weather_hour(
                values["precip_mm"], values["weather_code"], values["gust_ms"]
            )
            if zone_flags:
                labels = ", ".join(HAZARD_LABELS[item] for item in HAZARD_TYPES if item in zone_flags)
                zone_notes.append(
                    f"{zone}: {labels} ({values['precip_mm']:.1f} mm, gust {values['gust_ms']:.0f} m/s)"
                )
        if zone_notes:
            details.extend(zone_notes[:4])
        elif flags:
            details.append(
                f"peak {row['precip_mm']:.1f} mm, gust {row['wind_gust_ms']:.0f} m/s, "
                f"weather code {row['weather_code']}"
            )
    reported = event_index.get(hour)
    if reported:
        flags |= reported["flags"]
        details.extend(reported["details"])
    ordered = [hazard for hazard in HAZARD_TYPES if hazard in flags]
    return ordered, details


def _lag_hub(series, index, minutes):
    target = series[index][0] - timedelta(minutes=minutes)
    cursor = index
    while cursor >= 0 and series[cursor][0] > target:
        cursor -= 1
    if cursor < 0:
        return None
    if abs((series[cursor][0] - target).total_seconds()) > 20 * 60:
        return None
    return series[cursor][1][HUB]


def _price_features(series, index):
    moment, prices = series[index]
    previous = series[index - 1][1] if index else None
    lag_hour = _lag_hub(series, index, 60)
    lag_day = _lag_hub(series, index, 24 * 60)
    if lag_hour is None or lag_day is None:
        return None
    values = []
    for point in (*LOAD_ZONES, HUB):
        values.append(prices[point])
    for point in (*LOAD_ZONES, HUB):
        prior = previous[point] if previous else prices[point]
        values.append(prices[point] - prior)
    hour = moment.hour + moment.minute / 60
    day = moment.timetuple().tm_yday
    values.extend((
        math.sin(2 * math.pi * hour / 24),
        math.cos(2 * math.pi * hour / 24),
        math.sin(2 * math.pi * day / 365.25),
        math.cos(2 * math.pi * day / 365.25),
        lag_hour,
        lag_day,
    ))
    return tuple(values)


def _weather_features(precip_mm, gust_ms, hazards):
    present = set(hazards)
    return (
        float(precip_mm) / HEAVY_RAIN_MM,
        float(gust_ms) / STORM_GUST_MS,
        *(1.0 if hazard in present else 0.0 for hazard in HAZARD_TYPES),
    )


def _episode_text(moment, prices, outage, hazards, details):
    price_text = " ".join(f"{point}={prices[point]:.2f}" for point in (*LOAD_ZONES, HUB))
    outage_text = f"{outage['total']:.0f}" if outage else "unknown"
    hazard_text = ",".join(hazards) if hazards else "none"
    detail_text = "; ".join(details[:3])
    return (
        f"{moment.isoformat(sep=' ', timespec='minutes')} {price_text} "
        f"known_outage_mw={outage_text} hazards={hazard_text} {detail_text}"
    ).strip()


def build_episodes(prices, outages, weather, events):
    series = sorted(prices.items())
    event_index = _index_events(events)
    hour_ends = sorted(outages)
    episodes = []
    for index, (moment, point_prices) in enumerate(series):
        features = _price_features(series, index)
        if features is None:
            continue
        hazards, details = describe_hazards(moment, weather, event_index)
        hour = _weather_hour(moment)
        observed = weather.get(hour) or {}
        known = _known_outage(hour_ends, outages, moment)
        episodes.append({
            "time": moment,
            "prices": point_prices,
            "price_features": features,
            "weather_features": _weather_features(
                observed.get("precip_mm", 0),
                observed.get("wind_gust_ms", 0),
                hazards,
            ),
            "hazards": hazards,
            "details": details,
            "outage_mw": None if known is None else known["total"],
            "text": _episode_text(moment, point_prices, known, hazards, details),
        })
    return episodes


def _hour_end_covering(moment):
    if moment.minute == 0 and moment.second == 0 and moment.microsecond == 0:
        return moment
    return moment.replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)


def _known_outage(hour_ends, outages, moment):
    index = bisect.bisect_right(hour_ends, moment) - 1
    if index < 0:
        return None
    return outages[hour_ends[index]]


def _outage_covering(hour_ends, outages, moment):
    hour_end = _hour_end_covering(moment)
    index = bisect.bisect_left(hour_ends, hour_end)
    if index >= len(hour_ends) or hour_ends[index] != hour_end:
        return None
    return outages[hour_end]


def _fit_scale(rows):
    columns = list(zip(*rows))
    stats = []
    for column in columns:
        ordered = sorted(column)
        count = len(ordered)
        median = ordered[count // 2]
        iqr = ordered[(count * 3) // 4] - ordered[count // 4]
        if iqr < 1e-9:
            span = ordered[-1] - ordered[0]
            iqr = span if span > 1e-9 else 1.0
        stats.append((median, iqr))
    return stats


def _scale(values, stats):
    return tuple((value - median) / iqr for value, (median, iqr) in zip(values, stats))


def _distance(left, right):
    return math.sqrt(sum((a - b) ** 2 for a, b in zip(left, right)))


def _weighted_mean(values, weights):
    total = sum(weights)
    return sum(value * weight for value, weight in zip(values, weights)) / total


def _percentile(values, quantile):
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * quantile
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    fraction = position - lower
    return ordered[lower] * (1 - fraction) + ordered[upper] * fraction


def _parse_moment(value):
    if isinstance(value, datetime):
        return value
    return datetime.fromisoformat(str(value).replace("Z", ""))


def _elevated_threshold(outages):
    totals = [item["total"] for item in outages.values()]
    if not totals:
        return 0.0
    return _percentile(totals, ELEVATED_QUANTILE)


def _run_length(bits):
    if not bits:
        return ""
    parts = []
    current = bits[0]
    count = 1
    for bit in bits[1:]:
        if bit == current:
            count += 1
        else:
            parts.append(f"{current}x{count}")
            current = bit
            count = 1
    parts.append(f"{current}x{count}")
    return ",".join(parts)


def forecast_outages(episodes, outages, weather, events, as_of=None, minutes=60, k=25):
    """Forecast generation-outage MW and elevated-outage likelihood each minute.

    Neighbors are historical price and weather regimes whose entire outcome
    window ended at or before `as_of`. The predicted change is the average
    change that followed those neighbors, added to the outage already known.
    Likelihood is the distance-weighted share of those neighbors whose outage
    at the same lead was at or above the historical 90th percentile.
    """
    if minutes < 1 or k < 1:
        raise ValueError("minutes and k must be positive")
    if len(episodes) < 3:
        raise ValueError("not enough price history to retrieve similar hours")
    as_of = episodes[-1]["time"] if as_of is None else _parse_moment(as_of)
    query = None
    for episode in episodes:
        if episode["time"] <= as_of:
            query = episode
        else:
            break
    if query is None:
        raise ValueError(f"no price interval at or before {as_of.isoformat(sep=' ')}")

    hour_ends = sorted(outages)
    anchor = _known_outage(hour_ends, outages, query["time"])
    if anchor is None:
        raise ValueError("no realized outage is available at the forecast time")
    cutoff = as_of - timedelta(minutes=minutes)
    # Hourly outage totals change on the hour, so a 16:45 analog is a different
    # lead than 16:15. Keep the same minute-of-hour.
    query_minute = query["time"].minute
    library = []
    for episode in episodes:
        if episode["time"].minute != query_minute:
            continue
        if episode["time"] > cutoff or episode["time"] >= query["time"]:
            continue
        future = _outage_covering(hour_ends, outages, episode["time"] + timedelta(minutes=minutes))
        known = _known_outage(hour_ends, outages, episode["time"])
        if future is None or known is None:
            continue
        library.append((episode, known["total"], future))
    if not library:
        raise ValueError("no historical interval has a complete outage path before the forecast")

    stats = _fit_scale([item[0]["price_features"] for item in library])
    query_vector = _scale(query["price_features"], stats) + query["weather_features"]
    ranked = []
    for episode, known_total, _future in library:
        vector = _scale(episode["price_features"], stats) + episode["weather_features"]
        ranked.append((_distance(query_vector, vector), episode, known_total))
    nearest = heapq.nsmallest(k, ranked, key=lambda item: (item[0], -item[1]["time"].timestamp()))

    event_index = _index_events(events)
    linked = _linked_hazards(nearest)
    linked_details = _linked_details(nearest, linked)
    threshold = _elevated_threshold(outages)
    analog_bits = [[] for _item in nearest]
    rows = []
    absolute_errors = []
    persistence_errors = []
    for step in range(1, minutes + 1):
        moment = as_of + timedelta(minutes=step)
        deltas = []
        weights = []
        elevated = []
        for analog_index, (distance, episode, known_total) in enumerate(nearest):
            future = _outage_covering(hour_ends, outages, episode["time"] + timedelta(minutes=step))
            if future is None:
                continue
            weight = 1.0 / (distance + 0.05)
            deltas.append(future["total"] - known_total)
            weights.append(weight)
            high = 1 if future["total"] >= threshold else 0
            elevated.append(high)
            analog_bits[analog_index].append(str(high))
        if not deltas:
            raise ValueError(f"retrieved hours have no outage at minute {step}")
        predicted = max(0.0, anchor["total"] + _weighted_mean(deltas, weights))
        low = max(0.0, anchor["total"] + _percentile(deltas, 0.10))
        high_band = max(0.0, anchor["total"] + _percentile(deltas, 0.90))
        likelihood = _weighted_mean(elevated, weights)
        actual = _outage_covering(hour_ends, outages, moment)
        actual_mw = None if actual is None else actual["total"]
        if actual_mw is not None:
            absolute_errors.append(abs(predicted - actual_mw))
            persistence_errors.append(abs(anchor["total"] - actual_mw))
        flags, details = describe_hazards(moment, weather, event_index)
        rows.append({
            "timestamp": moment.isoformat(sep=" ", timespec="minutes"),
            "outage_likelihood": round(likelihood, 4),
            "retrieval_likelihood": round(likelihood, 4),
            "elevated_threshold_mw": round(threshold, 1),
            "predicted_outage_mw": round(predicted, 1),
            "band_low_mw": round(low, 1),
            "band_high_mw": round(high_band, 1),
            "actual_outage_mw": None if actual_mw is None else round(actual_mw, 1),
            "linked_events": list(linked),
            "events_at_time": flags,
            "hazard_detail": "; ".join(details[:3]),
        })

    retrieved = [
        {
            "time": episode["time"].isoformat(sep=" ", timespec="minutes"),
            "distance": round(distance, 3),
            "known_outage_mw": None if episode["outage_mw"] is None else round(episode["outage_mw"], 1),
            "hazards": list(episode["hazards"]),
            "text": episode["text"],
        }
        for distance, episode, _known in nearest
    ]
    analogs = []
    for (distance, episode, _known), bits in zip(nearest, analog_bits):
        weight = 1.0 / (distance + 0.05)
        analogs.append({
            "time": episode["time"].isoformat(sep=" ", timespec="minutes"),
            "distance": round(distance, 3),
            "weight": round(weight, 4),
            "hazards": list(episode["hazards"]),
            "text": episode["text"],
            "elevated": "".join(bits),
        })
    event_counts = Counter()
    for event in events:
        event_counts.update(event["hazards"])
    return {
        "as_of": as_of.isoformat(sep=" ", timespec="minutes"),
        "query_time": query["time"].isoformat(sep=" ", timespec="minutes"),
        "minutes": minutes,
        "k": len(nearest),
        "anchor_outage_mw": round(anchor["total"], 1),
        "elevated_threshold_mw": round(threshold, 1),
        "elevated_quantile": ELEVATED_QUANTILE,
        "likelihood_source": "retrieval",
        "model": None,
        "mae_mw": None if not absolute_errors else round(sum(absolute_errors) / len(absolute_errors), 1),
        "persistence_mae_mw": (
            None if not persistence_errors else round(sum(persistence_errors) / len(persistence_errors), 1)
        ),
        "scored_minutes": len(absolute_errors),
        "linked_events": linked,
        "linked_details": linked_details,
        "hazard_event_counts": {hazard: event_counts.get(hazard, 0) for hazard in HAZARD_TYPES},
        "construction_events": event_counts.get("construction", 0),
        "retrieved": retrieved,
        "analogs": analogs,
        "minutes_ahead": rows,
        "narrative": "",
    }


def _linked_hazards(nearest):
    counts = Counter()
    for _distance, episode, _known in nearest:
        counts.update(episode["hazards"])
    nearest_flags = set(nearest[0][1]["hazards"])
    chosen = []
    size = len(nearest)
    for hazard in HAZARD_TYPES:
        if counts[hazard] and (hazard in nearest_flags or counts[hazard] / size >= 0.30):
            chosen.append(hazard)
    return chosen


def _linked_details(nearest, linked):
    counts = Counter()
    for _distance, episode, _known in nearest:
        if not set(episode["hazards"]) & set(linked):
            continue
        for detail in episode["details"][:2]:
            counts[detail] += 1
    return [detail for detail, _count in counts.most_common(4)]


def narrate(result):
    rows = result["minutes_ahead"]
    average = sum(row["predicted_outage_mw"] for row in rows) / len(rows)
    average_likelihood = sum(row["outage_likelihood"] for row in rows) / len(rows)
    peak = max(rows, key=lambda row: row["outage_likelihood"])
    sentences = [
        f"Over the next {result['minutes']} minutes, ERCOT generation outage averages {average:.0f} MW, "
        f"starting from {result['anchor_outage_mw']:.0f} MW already reported."
    ]
    sentences.append(
        f"The likelihood of an elevated generation outage, at or above "
        f"{result['elevated_threshold_mw']:.0f} MW, averages {average_likelihood:.0%} "
        f"and peaks at {peak['outage_likelihood']:.0%} at {peak['timestamp']}."
    )
    if result.get("likelihood_source") == GROK_MODEL:
        sentences.append(
            "Those likelihoods were scored by grok-4.7 from the retrieved outage and weather episodes."
        )
    if result["linked_events"]:
        labels = ", ".join(HAZARD_LABELS[hazard] for hazard in result["linked_events"])
        sentences.append(f"Similar historical price and weather regimes link that path to {labels}.")
    else:
        sentences.append(
            "Similar historical regimes do not line up with heavy rain, a storm, a tornado, "
            "a hurricane or tropical storm, construction, or another disaster report."
        )
    if result["linked_details"]:
        sentences.append("Retrieved evidence includes " + "; ".join(result["linked_details"]) + ".")
    clock = []
    for hazard in HAZARD_TYPES:
        if any(hazard in row["events_at_time"] for row in rows):
            clock.append(HAZARD_LABELS[hazard])
    if clock:
        sentences.append("Hazards recorded during these minutes: " + ", ".join(clock) + ".")
    if not result.get("construction_events"):
        sentences.append("No construction events were present in the hazard index.")
    if result.get("mae_mw") is not None:
        sentences.append(
            f"On the {result['scored_minutes']} minutes with a realized outage, "
            f"the retrieved forecast MAE is {result['mae_mw']:.0f} MW "
            f"and a flat forecast MAE is {result['persistence_mae_mw']:.0f} MW."
        )
    return " ".join(sentences)


def explain_with_grok(result, api_key, model=GROK_MODEL, timeout=60):
    """Ask Grok to narrate the retrieved episodes without adding new events."""
    evidence = "\n".join(item["text"] for item in result["retrieved"][:8])
    prompt = (
        "Explain this ERCOT generation-outage forecast in one short paragraph. "
        "Use only the retrieved episodes and the hazard labels already listed. "
        "Do not invent a tornado, hurricane, construction project, or other event.\n\n"
        f"Forecast: {result.get('narrative')}\n"
        f"Linked hazards: {', '.join(result['linked_events']) or 'none'}\n"
        f"Retrieved episodes:\n{evidence}"
    )
    payload = _grok_response(api_key, {"model": model, "input": prompt}, timeout=timeout)
    return _response_text(payload)


def _grok_response(api_key, body, timeout=90):
    request = Request(
        "https://api.x.ai/v1/responses",
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
            "User-Agent": "baselayer-ercot/1.0",
        },
        method="POST",
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        detail = exc.read().decode("utf-8", "replace")[:500]
        raise ValueError(f"Grok request failed ({exc.code}): {detail}") from exc


def _parse_json_object(text):
    raw = text.strip()
    if raw.startswith("```"):
        raw = raw.split("\n", 1)[-1]
        if raw.endswith("```"):
            raw = raw[: raw.rfind("```")]
    return json.loads(raw.strip())


def likelihood_prompt(result, start_offset, end_offset):
    """Compact retrieved-analog prompt for one slice of the minute horizon."""
    lines = []
    for analog in result.get("analogs") or []:
        bits = analog.get("elevated") or ""
        window = bits[start_offset - 1:end_offset]
        hazards = ",".join(analog["hazards"]) if analog["hazards"] else "none"
        lines.append(
            f"{analog['time']} distance={analog['distance']} weight={analog['weight']} "
            f"hazards={hazards} elevated={_run_length(window)} | {analog['text']}"
        )
    offsets = f"{start_offset}-{end_offset}"
    return (
        "Estimate the probability that ERCOT generation outage is elevated at each future minute. "
        f"Elevated means generation outage at or above {result['elevated_threshold_mw']:.0f} MW, "
        f"the historical {result['elevated_quantile']:.0%} of realized hourly totals. "
        "Each analog line is a retrieved historical price and weather regime. "
        "elevated= is a run length over this minute slice, in lead order: 1 means that analog "
        "was elevated at that lead, 0 means it was not. Weight closer analogs more. "
        "Use only these analogs. Do not invent a tornado, hurricane, construction project, "
        "or any other event that is absent from the evidence. "
        f"Return one likelihood between 0 and 1 for every offset from {offsets}.\n\n"
        f"As of {result['as_of']}. Anchor outage {result['anchor_outage_mw']:.0f} MW. "
        f"Linked hazards: {', '.join(result['linked_events']) or 'none'}.\n"
        f"Requested offsets: {offsets}\n"
        "Analogs:\n"
        + "\n".join(lines)
    )


_LIKELIHOOD_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["summary", "minutes"],
    "properties": {
        "summary": {
            "type": "string",
            "description": "One paragraph grounded only in the retrieved episodes.",
        },
        "minutes": {
            "type": "array",
            "items": {
                "type": "object",
                "additionalProperties": False,
                "required": ["offset", "likelihood"],
                "properties": {
                    "offset": {
                        "type": "integer",
                        "description": "Minutes ahead of the forecast time.",
                    },
                    "likelihood": {
                        "type": "number",
                        "description": "Probability from 0 to 1 that generation outage is elevated.",
                    },
                },
            },
        },
    },
}


def apply_model_likelihoods(result, scored_minutes, summary="", model=GROK_MODEL):
    """Replace per-minute likelihoods with Grok scores. Keep the retrieval values."""
    by_offset = {}
    for item in scored_minutes:
        try:
            offset = int(item["offset"])
            value = float(item["likelihood"])
        except (KeyError, TypeError, ValueError):
            continue
        if offset < 1:
            continue
        by_offset[offset] = min(1.0, max(0.0, value))
    rows = result["minutes_ahead"]
    matched = 0
    for offset, row in enumerate(rows, start=1):
        if offset not in by_offset:
            continue
        row["outage_likelihood"] = round(by_offset[offset], 4)
        matched += 1
    if matched == len(rows) and rows:
        result["likelihood_source"] = model
        result["model"] = model
    elif matched:
        result["likelihood_source"] = "mixed"
        result["model"] = model
    if summary and summary.strip():
        result["model_explanation"] = summary.strip()
    return matched


def score_likelihood_with_grok(result, api_key, model=GROK_MODEL, timeout=120):
    """Ask Grok 4.7 for an elevated-outage likelihood at every future minute."""
    rows = result["minutes_ahead"]
    if not rows:
        raise ValueError("forecast has no minutes to score")
    collected = []
    summaries = []
    for start in range(0, len(rows), LIKELIHOOD_CHUNK):
        window = rows[start:start + LIKELIHOOD_CHUNK]
        start_offset = start + 1
        end_offset = start + len(window)
        body = {
            "model": model,
            "temperature": 0,
            "search_parameters": {"mode": "off"},
            "instructions": (
                "Score ERCOT generation-outage likelihood from retrieved historical episodes only."
            ),
            "input": likelihood_prompt(result, start_offset, end_offset),
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": "outage_likelihood",
                    "strict": True,
                    "schema": _LIKELIHOOD_SCHEMA,
                }
            },
        }
        payload = _grok_response(api_key, body, timeout=timeout)
        parsed = _parse_json_object(_response_text(payload))
        collected.extend(parsed.get("minutes") or [])
        if parsed.get("summary"):
            summaries.append(str(parsed["summary"]).strip())
    matched = apply_model_likelihoods(result, collected, " ".join(summaries), model)
    result["narrative"] = narrate(result)
    return matched


def _response_text(payload):
    if isinstance(payload.get("output_text"), str) and payload["output_text"].strip():
        return payload["output_text"].strip()
    chunks = []
    for item in payload.get("output") or []:
        for part in item.get("content") or []:
            text = part.get("text")
            if text:
                chunks.append(text)
    if chunks:
        return "\n".join(chunks).strip()
    raise ValueError("Grok response did not include text")


def save_minute_forecast(path, result):
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = (
        "timestamp",
        "outage_likelihood",
        "retrieval_likelihood",
        "elevated_threshold_mw",
        "predicted_outage_mw",
        "band_low_mw",
        "band_high_mw",
        "actual_outage_mw",
        "linked_events",
        "events_at_time",
        "hazard_detail",
    )
    with destination.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for row in result["minutes_ahead"]:
            writer.writerow({
                **row,
                "linked_events": "|".join(row["linked_events"]),
                "events_at_time": "|".join(row["events_at_time"]),
            })
    return destination


def run_outage_forecast(
    prices_path,
    outages_dir,
    weather_csv,
    event_paths,
    as_of=None,
    minutes=60,
    k=25,
):
    prices = load_prices(prices_path)
    outages = load_realized_outages(outages_dir)
    weather = load_hourly_weather(weather_csv) if weather_csv and Path(weather_csv).exists() else {}
    events = []
    for event_path in event_paths:
        events.extend(load_hazard_events(event_path))
    episodes = build_episodes(prices, outages, weather, events)
    result = forecast_outages(
        episodes,
        outages,
        weather,
        events,
        as_of=as_of,
        minutes=minutes,
        k=k,
    )
    result["episodes"] = len(episodes)
    result["narrative"] = narrate(result)
    return result


def public_summary(result, output_path):
    preview = result["minutes_ahead"][:3]
    tail = result["minutes_ahead"][-1:]
    return {
        "file": str(output_path),
        "as_of": result["as_of"],
        "query_time": result["query_time"],
        "minutes": result["minutes"],
        "episodes": result.get("episodes"),
        "anchor_outage_mw": result["anchor_outage_mw"],
        "elevated_threshold_mw": result.get("elevated_threshold_mw"),
        "likelihood_source": result.get("likelihood_source"),
        "model": result.get("model"),
        "model_error": result.get("model_error"),
        "mae_mw": result["mae_mw"],
        "persistence_mae_mw": result["persistence_mae_mw"],
        "scored_minutes": result["scored_minutes"],
        "linked_events": result["linked_events"],
        "linked_details": result["linked_details"],
        "hazard_event_counts": result["hazard_event_counts"],
        "narrative": result["narrative"],
        "model_explanation": result.get("model_explanation"),
        "retrieved": result["retrieved"][:8],
        "preview": preview + tail,
    }
