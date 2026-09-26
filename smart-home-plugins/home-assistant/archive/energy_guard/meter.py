"""Pure session accounting, independent of Home Assistant."""
from math import isfinite

class SessionMeter:
    def __init__(self, saved=None):
        saved = saved or {}
        self.active = saved.get("active", False)
        self.last = saved.get("last")
        self.used = saved.get("used", 0.0)

    def update(self, on, reading):
        if not on:
            self.active = False
            return False
        if reading is None or not isfinite(reading) or reading < 0:
            return False
        if not self.active:
            self.active, self.last, self.used = True, reading, 0.0
        else:
            # A decreasing cumulative meter indicates a meter reset.
            self.used += reading - self.last if reading >= self.last else reading
            self.last = reading
        return self.used > 1.0 + 1e-9

    def dump(self):
        return {"active": self.active, "last": self.last, "used": self.used}
