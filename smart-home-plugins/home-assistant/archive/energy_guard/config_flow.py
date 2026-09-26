"""User setup for Energy Guard."""
import voluptuous as vol
from homeassistant import config_entries
from homeassistant.helpers import selector

class EnergyGuardFlow(config_entries.ConfigFlow, domain="energy_guard"):
    VERSION = 1

    async def async_step_user(self, user_input=None):
        errors = {}
        if user_input is not None:
            meter = self.hass.states.get(user_input["energy_sensor"])
            switch = self.hass.states.get(user_input["switch"])
            if meter is None or meter.attributes.get("unit_of_measurement") != "kWh":
                errors["energy_sensor"] = "invalid_energy"
            elif switch is None:
                errors["switch"] = "invalid_switch"
            else:
                await self.async_set_unique_id(user_input["switch"])
                self._abort_if_unique_id_configured()
                return self.async_create_entry(title=f"Energy Guard: {switch.name}", data=user_input)
        return self.async_show_form(step_id="user", errors=errors, data_schema=vol.Schema({
            vol.Required("energy_sensor"): selector.EntitySelector(selector.EntitySelectorConfig(domain="sensor", device_class="energy")),
            vol.Required("switch"): selector.EntitySelector(selector.EntitySelectorConfig(domain="switch")),
        }))
