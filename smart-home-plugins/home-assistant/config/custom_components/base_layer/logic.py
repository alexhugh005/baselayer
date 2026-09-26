"""Power conversion and deterministic load selection."""
from math import isfinite
LIMIT_W = 11000

def watts(state):
    if state is None:
        return None
    try:
        value = float(state.state)
    except (TypeError, ValueError):
        return None
    unit = state.attributes.get("unit_of_measurement")
    if unit not in ("W", "kW") or not isfinite(value) or value < 0:
        return None
    return value * (1000 if unit == "kW" else 1)

def highest(states, devices, excluded=()):
    candidates = []
    for device in devices:
        switch = states.get(device["switch"])
        power = watts(states.get(device["power_sensor"]))
        if device["switch"] not in excluded and switch is not None and switch.state == "on" and power is not None and power > 0:
            candidates.append((power, device["switch"], device["power_sensor"]))
    return max(candidates, default=None)
