# Virtual SPAN panel for Energy Lab

Local Home Assistant: <http://localhost:8123/energy-lab/panel>  
PanelBench: <http://localhost:18080>

The **Smart Panel** tab connects a real SPAN Home Assistant custom integration
to PanelBench, with eight virtual circuits. No physical panel is connected.
PanelBench receives the existing Energy Lab's live circuit watts, rather than
generating an independent set of appliance loads. Its meters and the household
meter should agree after a short update delay.

| Circuit | Assigned loads | Rating |
| --- | --- | --- |
| Living room | Living-room light, ceiling fan | 15 A / 120 V |
| Office outlets | Space heater, desk lamp | 20 A / 120 V |
| Kitchen outlets | Refrigerator, toaster, dishwasher | 20 A / 120 V |
| Essential electronics | Existing background router/security/standby load | 15 A / 120 V |
| Laundry | Dryer | 30 A / 240 V |
| Garage | EV charger | 60 A / 240 V |
| Central HVAC | Heat pump and room thermostat | 30 A / 240 V |
| Water heater | Tank element and control electronics | 30 A / 240 V |

## Operating the lab

1. Turn on a circuit's **fault** toggle to simulate loss of power to that area.
2. Its assigned appliances turn off, their operating and standby watts become
   zero, and their ordinary controls cannot start them while supply is absent.
3. Clear the fault to restore supply. Turn appliances back on explicitly.
   The essential-electronics background load resumes when supply returns.
4. **Whole-house power outage** cuts all eight circuits.
5. **Restore all circuit supplies** clears faults and recloses breakers. Use
   **Reset realistic household** afterward for the familiar quiet-home scenario.

The native **SPAN breaker** switches also cut their associated devices. An
active fault holds supply off even if a breaker is commanded closed. Fault
helpers restore after Home Assistant restarts; a manual breaker position alone
is not persisted by the upstream simulator across a simulator restart. Use the
fault toggles for persistent outage scenarios.
Every fault change explicitly calls Home Assistant's persistent-state save
service, so a recent change does not depend on the next periodic save or a
graceful container shutdown.

To switch off just the garage, open its SPAN circuit settings and turn the
**Relay → Breaker** switch off, or use its **SPAN breaker** in Energy Lab's
Smart Panel tab. The EV stops charging and reads 0 W. Reclosing the breaker
restores supply; charging must be enabled separately.

The pinned integration's settings drawer has a frontend bug: it reads the old
reflected `checked` attribute during a change event and sends `turn_on` for an
OFF click. `patch_frontend.py <HA-config-directory>` fixes both bundled frontend
files to read the switch's new `checked` property. Refresh frontend registration (re-save unchanged integration options) or
restart Home Assistant, then refresh browser tabs afterward. Reapply this guarded patch after reinstalling
the pinned integration; review it before upgrading. Original bundles are backed
up under `.local/span-panel/frontend-backup`. Both command directions were
checked with stale attribute values. The repaired settings drawer was used live
to open the garage relay, stop the EV at 0 W, and reopen settings with the breaker
still off.

Faults are deliberate scenario inputs. This model does not calculate short
circuits, arc faults, or thermal/magnetic protective trip curves, and does not
automatically trip on the displayed amp rating. It is not an electrical design.
The 11 kW Base Layer limit is still separate from the panel's service rating.

## Wiring

`circuits.json` is the source of the device-to-circuit mapping; `relays.json`
records the installed SPAN switch IDs. `build_lab.py` generates the panel config,
the HA package, device control guards, and the Smart Panel tab. The original
appliance entity IDs remain intact, including their connections to Base Layer.
The refrigerator is included once: the existing background sensor includes it,
while panel aggregation places it in Kitchen and electronics in Essentials.

The native SPAN integration exchanges commands and telemetry over local TLS
MQTT. `live_panel.py` supplies current Energy Lab meter readings through
PanelBench's injectable `RecorderDataSource` interface. The adapter runs inside
the same container as PanelBench; no upstream energy-calculation code is edited.
Historical replay/what-if modeling is not enabled for this live source.

This pinned upstream version also drops MQTT subscription callbacks and does
not consume its async client's incoming messages. `mqtt_receiver.py` preserves
the SDK callback and delivers received messages to it. The adapter also routes
PanelBench dashboard relay/priority changes through the emitter's own setter
registry and mirrors confirmed relay state to the dashboard. Recheck these
compatibility hooks before upgrading upstream. `/health` on localhost:18081
reports the emitter's confirmed relays independently of HA's switch display.
It also requires a live MQTT connection, recent successful publication, and
completion of the device-tree refresh; live HA meter reads alone are insufficient.

The SPAN integration's snapshot update interval is zero for immediate live
updates. Its two-second relay debounce remains enabled. Fault relay commands
are queued with a four-second delay to respect that guard; appliance power is
cut immediately by the supply interlock.

If meter reads fail, the bridge reports disconnected and supply interlocks turn
off the virtual devices. It does not substitute invented live readings. A short
transient discrepancy between panel and appliance meters is expected while
Home Assistant, the half-second bridge poll, and the one-second emitter update.

## Runtime and recovery

The running container is `basehack-panelbench`, on the existing
`basehack-energy-lab_default` network alongside `basehack-homeassistant`. Only
its dashboard and health endpoint are published, bound to localhost. HTTP
bootstrap (8081), TLS bootstrap (9081), and MQTT (18883) stay on the Docker network.
This avoids dependence on macOS container mDNS reaching the host LAN.

```sh
docker compose -f docs/home-assistant/span-panel/compose.yaml up -d
docker compose -f docs/home-assistant/span-panel/compose.yaml logs --tail 50
```

Both containers use a restart policy. Docker Desktop must be running.
The Mac must be awake for the local lab to run. A sleep/wake cycle can interrupt
MQTT even while both containers appear to be running. The adapter retries failed
connections with a 1–10 second backoff, restores command subscriptions, and
republishes the complete device tree after reconnecting. Relay positions survive
this connection recovery because the emitter stays running. While disconnected,
the health endpoint reports unavailable and the HA supply interlocks turn off
virtual appliances; reconnection does not automatically restart those appliances.
The user-approved one-year **Energy Lab PanelBench** HA token is stored in
`.local/span-panel/secrets/ha-token` with mode 0600 and mounted read-only.
It expires approximately September 26, 2027; replace that file and restart
PanelBench if it is renewed or revoked. Tokens and local configuration backups
are git-ignored. The pre-install lab backup is under `.local/span-panel/backup-*`.

Installed upstream sources are pinned to:

- PanelBench 2.5.3: `ffdb5472be5b14e5346394edffb8bbd23a6f7b72`
- SPAN integration 2.1.2: `54749b90a4f83e539efb5c085d1cb1d8449d24f9`

The Dockerfile uses Python 3.14 because this PanelBench release requires it;
the upstream root Dockerfile still specifies Python 3.12. To rebuild from the
existing pinned checkout:

```sh
docker build -t basehack-panelbench:2.5.3 \
  -f docs/home-assistant/span-panel/Dockerfile .local/panelbench
```

The HA integration was installed directly under the existing config's
`custom_components/span_panel` directory because this HA Container setup has
neither an add-on store nor HACS. Pairing used host `basehack-panelbench`, HTTP
8081 and TLS 9081, with the simulator's test passphrase. TLS certificate checking
remains enabled.

## Verification

```sh
.local/span-tools/bin/python docs/home-assistant/span-panel/verify.py
.local/span-tools/bin/python docs/home-assistant/span-panel/verify_recovery.py
docker exec basehack-homeassistant python -m homeassistant \
  --script check_config --config /config
```

The verifier is restricted to the named illustrative lab and intentionally
operates its virtual devices. It checks circuit isolation, blocked appliance
restart, zero standby watts, restore behavior, direct SPAN control, whole-house
outage, and agreement between SPAN and the original household meter. It restores
the quiet-home scenario afterward. Results are in
`.local/span-panel/verification.log`.

Verified September 26, 2026 on Home Assistant 2026.9.3: all eight circuit
faults, isolation of unrelated rooms, blocked appliance restarts, zero watts
including standby, native SPAN control, PanelBench dashboard control, confirmed
emitter relay positions, and whole-house outage passed. A saved kitchen fault
also survived a native Home Assistant restart. The final state is healthy
circuits and the quiet-home scenario; restart evidence is saved in
`.local/span-panel/restart-verification.json`.

The recovery verifier stops and restarts only the simulator's MQTT broker. It
checks offline health, appliance shutdown, automatic reconnection, native breaker
commands after reconnecting, and resumed live power readings. All checks passed
September 26, 2026; output is in `.local/span-panel/recovery-verification.log`.

## Base Layer Smart Panel page

Open `/smart-panel` in Base Layer or choose **Smart Panel** in the sidebar.
The page lists discovered `switch.span_panel_*_breaker` entities, with each
section's observed on/off state and a manual toggle. Renamed entity IDs outside
that convention are not currently discovered as panel circuits.

A toggle posts `{ entityId, action: "On" | "Off", idempotencyKey }` to
`POST /api/homes/{homeId}/circuits/commands`. The authenticated server checks
home ownership, a recent connection, and a discovered circuit, then queues the
command. The polling worker calls Home Assistant's
`POST /api/services/switch/turn_on` or `turn_off` with the breaker `entity_id`;
OAuth credentials stay on the server. The UI remains pending until a subsequent
Home Assistant state read confirms the result. Failed requests retry within the
existing two-minute command lifetime and surface their outcome on the page.

Manual panel controls are separate from appliance shutoff permissions. Panel
breakers are excluded from Smart Usage recommendations and automatic shutoff /
restore selection. Turning a breaker back on restores supply, but does not
restart appliances or clear an active lab fault. Circuit state reflects the
breaker position; fault and supply-interlock status remain in Home Assistant.

### Grid outage settings

Each circuit has a **When the grid goes down** selector. Its choices use SPAN's
native circuit priority through the matching discovered
`select.span_panel_*_circuit_priority` entity:

| Choice | Home Assistant option |
| --- | --- |
| Turn off | `off_grid` |
| Stay on | `never` |
| Stay on until battery threshold | `soc_threshold` |

The battery threshold and backup behavior are managed by SPAN; this setting
does not configure a threshold or provide backup power. Missing, unavailable,
or offline priority controls cannot be changed. Only options advertised by the
discovered select entity are offered. Like breaker discovery, mapping currently
requires the installed integration's entity naming convention.

`PUT /api/homes/{homeId}/circuits/priority` accepts
`{ entityId, priority, idempotencyKey }`, where `entityId` is the circuit's breaker.
The server checks ownership, freshness, circuit discovery, and the available
priority options, then persists a command. The polling worker calls
`POST /api/services/select/select_option` with `{ entity_id, option }` using
server-held OAuth credentials. The UI keeps the observed selection and displays
only a spinner until a subsequent Home Assistant reading confirms the requested
priority. Commands have bounded retries, expiry, and cancellation. Changing a
priority does not send a breaker on/off command or change appliance permissions.

Verified with the local virtual panel: the UI's **Stay on** selection produced
`never` in Home Assistant and a confirmed command in Base Layer. The original
`off_grid` priority was restored afterward. This verifies configuration delivery;
the lab's whole-house fault scenario still deliberately cuts all circuit supplies
and is not a simulation of SPAN battery-backed islanding.

References: [SPAN integration](https://github.com/SpanPanel/span) and
[Home Assistant select service](https://www.home-assistant.io/integrations/select).
