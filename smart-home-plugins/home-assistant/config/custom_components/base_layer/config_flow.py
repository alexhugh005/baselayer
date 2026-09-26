"""Select a household power meter and explicitly eligible switches."""
import voluptuous as vol
from homeassistant import config_entries
from homeassistant.helpers import selector

POWER = selector.EntitySelector(selector.EntitySelectorConfig(domain="sensor", device_class="power"))
SWITCH = selector.EntitySelector(selector.EntitySelectorConfig(domain="switch"))

class BaseLayerFlow(config_entries.ConfigFlow, domain="base_layer"):
    VERSION = 1

    def __init__(self):
        self.household = None
        self.devices = []

    def valid_power(self, entity):
        state = self.hass.states.get(entity)
        return state is not None and state.attributes.get("unit_of_measurement") in ("W", "kW")

    async def async_step_user(self, user_input=None):
        errors = {}
        if user_input is not None:
            if self.valid_power(user_input["household_sensor"]):
                await self.async_set_unique_id("household")
                self._abort_if_unique_id_configured()
                self.household = user_input["household_sensor"]
                return await self.async_step_device()
            errors["base"] = "invalid_power"
        return self.async_show_form(step_id="user", errors=errors, data_schema=vol.Schema({vol.Required("household_sensor"): POWER}))

    async def async_step_device(self, user_input=None):
        errors = {}
        if user_input is not None:
            if not self.valid_power(user_input["power_sensor"]):
                errors["base"] = "invalid_power"
            elif (user_input["power_sensor"] == self.household or
                  any(d["switch"] == user_input["switch"] or d["power_sensor"] == user_input["power_sensor"] for d in self.devices)):
                errors["base"] = "duplicate_device"
            elif self.hass.states.get(user_input["switch"]) is None:
                errors["base"] = "invalid_switch"
            else:
                self.devices.append({k:user_input[k] for k in ("switch", "power_sensor")})
                if not user_input.get("add_another", False):
                    return self.async_create_entry(title="Base Layer", data={"household_sensor":self.household,"devices":self.devices})
        return self.async_show_form(step_id="device", errors=errors, data_schema=vol.Schema({
            vol.Required("switch"): SWITCH,
            vol.Required("power_sensor"): POWER,
            vol.Required("add_another", default=False): bool,
        }))
