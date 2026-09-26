# EV charging current control

In **Device settings**, pair the EV's on/off switch with its power sensor, then choose its **EV charging → Charging current control**. This explicit pairing identifies the device as an EV; names alone never grant control. Home Assistant must expose a `number` or `input_number` entity measured in amperes (`A`) with valid minimum, maximum, and step attributes.

Set **Charger watts per amp** to the total charging power per amp across all phases: 240 for a 240 V single-phase charger, or 690 for a 230 V three-phase charger. For the included virtual EV, select `input_number.virtual_ev_current_limit` and use 240. Pair its switch with `sensor.virtual_ev_charger_power`.

The device row then shows the current limit and supports manual changes within the Home Assistant range and step. A permitted, connected EV is required. This changes the current limit without starting a stopped charger. Recent activity records requests, retries, cancellation, and observed confirmation.

With **Smart Shutoff enabled** and the EV set to **Anytime**, a fresh total at or above 11,000 W triggers current reduction before automatic shutoffs when the EV can continue charging within the available capacity:

```
other load = household watts − measured EV watts
available EV watts = 11,000 − 100 − other load
current = round down available EV watts / charger watts per amp
```

The result respects the charger's minimum, maximum and step, and is always lower than its current setting. At 6,000 W of other load, a 240 V charger drops from 32 A to 20 A: 4,800 W of charging and 10,800 W total. Requests use Home Assistant's `number.set_value` or `input_number.set_value` actions, and are confirmed from a subsequent observed current limit. One EV is adjusted at a time before replanning from measured household load.

If there is insufficient capacity for minimum charging, or its current control is unavailable, the existing automatic shutoff selection applies. Failed/unconfirmed current requests expire after 30 seconds and are not endlessly recreated during the same overload. Unknown household power pauses automation; unknown EV power prevents estimating a reduction. Never and Sometimes devices are never automatically throttled. Automatic reductions persist the original current limit and the last managed setting. Once capacity has been available for 5 seconds, the shared restore queue increases to the highest supported current that fits with 500 W of spare capacity, capped at the original limit. Partial increases remain at the front of the queue; other eligible devices may proceed if no further amp increment fits. Only observed confirmation at the original limit removes the entry. Further reductions preserve the first original target. Manual changes, Keep current rate, and removing device access relinquish automatic current restoration. Failed increases remain visible without endless retries. A shut-off EV is first restarted at its reduced current, then its amp limit is restored. **Demo only:** device restart delays are 15 seconds off, 5 seconds of stable capacity, and 5 seconds between restorations. These are deliberately shortened from the production-oriented 2-minute/1-minute delays. This is a polling-based load-management feature, not an electrical protection mechanism.

API: `POST /api/homes/{homeId}/ev/current` with `{ "entityId": "switch.virtual_ev_charger", "amps": 20, "idempotencyKey": "unique-request-key" }`. Settings accept `evCharging: { "switch.virtual_ev_charger": { "currentEntityId": "input_number.virtual_ev_current_limit", "wattsPerAmp": 240 } }`; `null` removes a pairing. Omitted mappings preserve existing configuration.

Home Assistant references: [Number actions](https://www.home-assistant.io/integrations/number/), [Input number actions](https://www.home-assistant.io/integrations/input_number/).
