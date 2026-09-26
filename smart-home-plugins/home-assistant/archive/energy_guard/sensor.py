"""Session energy sensor and shutoff controller."""
import asyncio
import logging
from homeassistant.components.sensor import SensorEntity, SensorDeviceClass
from homeassistant.components import persistent_notification
from homeassistant.core import callback
from homeassistant.helpers.event import async_track_state_change_event
from homeassistant.helpers.storage import Store
from .meter import SessionMeter

_LOGGER = logging.getLogger(__name__)

async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([EnergyGuard(entry)])

class EnergyGuard(SensorEntity):
    _attr_device_class = SensorDeviceClass.ENERGY
    _attr_native_unit_of_measurement = "kWh"
    _attr_should_poll = False
    _attr_icon = "mdi:shield-lightning"

    def __init__(self, entry):
        self.entry = entry
        self._attr_unique_id = entry.entry_id
        self._attr_name = entry.title + " Session Energy"
        self.meter = SessionMeter()
        self.lock = asyncio.Lock()
        self.store = None
        self.stopping = False

    @property
    def native_value(self):
        return round(self.meter.used, 6)

    @property
    def extra_state_attributes(self):
        return {"limit_kwh": 1.0, "switch": self.entry.data["switch"],
                "energy_sensor": self.entry.data["energy_sensor"],
                "session_active": self.meter.active}

    async def async_added_to_hass(self):
        self.store = Store(self.hass, 1, "energy_guard." + self.entry.entry_id)
        self.meter = SessionMeter(await self.store.async_load())
        self.async_on_remove(async_track_state_change_event(
            self.hass, [self.entry.data["switch"], self.entry.data["energy_sensor"]], self.changed))
        await self.refresh()

    @callback
    def changed(self, event):
        self.hass.async_create_task(self.refresh())

    async def refresh(self):
        async with self.lock:
            switch = self.hass.states.get(self.entry.data["switch"])
            energy = self.hass.states.get(self.entry.data["energy_sensor"])
            if switch is None or switch.state not in ("on", "off"):
                return
            reading = None
            if energy is not None and energy.attributes.get("unit_of_measurement") == "kWh":
                try:
                    reading = float(energy.state)
                except (ValueError, TypeError):
                    pass
            trip = self.meter.update(switch.state == "on", reading)
            await self.store.async_save(self.meter.dump())
            self.async_write_ha_state()
        # Do not hold the lock while calling a service that emits state events.
        if not trip or self.stopping:
            return
        self.stopping = True
        try:
            await self.hass.services.async_call("switch", "turn_off", {
                "entity_id": self.entry.data["switch"]}, blocking=True)
            current = self.hass.states.get(self.entry.data["switch"])
            confirmed = current is not None and current.state == "off"
            persistent_notification.async_create(self.hass,
                f"{self.entry.data['switch']} used {self.meter.used:.3f} kWh this session. "
                + ("It has been turned off." if confirmed else "An off command was sent; off state is not yet confirmed."),
                title="Energy Guard: 1 kWh limit exceeded", notification_id=self.entry.entry_id)
        except Exception:
            _LOGGER.exception("Energy Guard could not turn off %s", self.entry.data["switch"])
            persistent_notification.async_create(self.hass,
                "Could not turn off " + self.entry.data["switch"] + ". Will retry on the next sensor update.",
                title="Energy Guard: shutoff failed", notification_id=self.entry.entry_id)
        finally:
            self.stopping = False
