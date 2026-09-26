"""Observe household load and shed one confirmed device at a time."""
import asyncio
import logging
from datetime import timedelta
from homeassistant.components.sensor import SensorEntity, SensorDeviceClass, SensorStateClass
from homeassistant.components import persistent_notification
from homeassistant.core import callback
from homeassistant.helpers.event import async_track_state_change_event, async_track_time_interval
from .logic import watts, highest, LIMIT_W

_LOGGER = logging.getLogger(__name__)

async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([BaseLayer(entry)])

class BaseLayer(SensorEntity):
    _attr_name = "Base Layer Household Power"
    _attr_native_unit_of_measurement = "kW"
    _attr_device_class = SensorDeviceClass.POWER
    _attr_state_class = SensorStateClass.MEASUREMENT
    _attr_should_poll = False
    _attr_icon = "mdi:home-lightning-bolt"

    def __init__(self, entry):
        self.entry = entry
        self._attr_unique_id = entry.entry_id + "_household_power"
        self.task = None
        self.closed = False
        self.status = "Starting"
        self.actions = []
        self.cooldown = 0

    def house(self):
        return self.hass.states.get(self.entry.data["household_sensor"])

    @property
    def available(self):
        return watts(self.house()) is not None

    @property
    def native_value(self):
        value = watts(self.house())
        return None if value is None else round(value / 1000, 4)

    @property
    def extra_state_attributes(self):
        return {"limit_kw": 11, "status": self.status, "recent_shutoffs": self.actions[-10:],
                "eligible_switches": [d["switch"] for d in self.entry.data["devices"]]}

    async def async_added_to_hass(self):
        entities = [self.entry.data["household_sensor"]]
        for d in self.entry.data["devices"]:
            entities.extend([d["switch"], d["power_sensor"]])
        self.async_on_remove(async_track_state_change_event(self.hass, entities, self.changed))
        self.async_on_remove(async_track_time_interval(self.hass, self.changed, timedelta(seconds=10)))
        self.changed(None)

    async def async_will_remove_from_hass(self):
        self.closed = True
        if self.task:
            self.task.cancel()
            try:
                await self.task
            except asyncio.CancelledError:
                pass

    @callback
    def changed(self, event):
        if not self.closed:
            self.async_write_ha_state()
            if self.task is None or self.task.done():
                self.task = self.hass.async_create_task(self.manage())

    def report(self, message):
        self.status = message
        self.async_write_ha_state()

    def blocked(self, message):
        self.report(message)
        self.cooldown = asyncio.get_running_loop().time() + 60
        persistent_notification.async_create(self.hass, message, title="Base Layer needs attention", notification_id="base_layer_attention")

    async def manage(self):
        # Debounce related template updates before ranking devices.
        await asyncio.sleep(0.5)
        if asyncio.get_running_loop().time() < self.cooldown:
            return
        while not self.closed:
            total = watts(self.house())
            if total is None:
                self.report("Waiting for a valid household power reading")
                return
            if total < LIMIT_W:
                self.report("Below 11 kW")
                return
            candidate = highest(self.hass.states, self.entry.data["devices"])
            if candidate is None:
                self.blocked("At or above 11 kW, but no eligible running device has a valid positive power reading.")
                return
            power, switch_id, sensor_id = candidate
            before = self.house().last_reported
            self.report("Turning off " + switch_id)
            try:
                await asyncio.wait_for(self.hass.services.async_call("switch", "turn_off", {"entity_id":switch_id}, blocking=True), timeout=15)
            except (Exception, asyncio.TimeoutError):
                _LOGGER.exception("Base Layer could not switch off %s", switch_id)
                self.blocked("Could not turn off " + switch_id + "; no further devices were switched off. Retrying in 60 seconds.")
                return
            # Require confirmation and a NEW household report, not an estimated subtraction.
            confirmed = False
            for _ in range(30):
                await asyncio.sleep(1)
                switch = self.hass.states.get(switch_id)
                house = self.house()
                device_power = watts(self.hass.states.get(sensor_id))
                if (switch is not None and switch.state == "off" and house is not None
                    and house.last_reported > before and watts(house) is not None
                    and device_power is not None and device_power < power):
                    confirmed = True
                    break
            if not confirmed:
                self.blocked("Waiting for confirmed shutoff and fresh meter readings from " + switch_id + ". No further devices were switched off; retrying in 60 seconds.")
                return
            self.actions.append(switch_id)
            self.actions = self.actions[-10:]
            persistent_notification.async_create(self.hass,
                f"Turned off {switch_id} ({power / 1000:.3f} kW). Household load is now {watts(self.house()) / 1000:.3f} kW. Devices stay off until you turn them on.",
                title="Base Layer reduced household load", notification_id="base_layer_action")
            await asyncio.sleep(1)
