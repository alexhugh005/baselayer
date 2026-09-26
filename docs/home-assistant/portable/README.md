# Portable Energy Lab

Start the virtual household and SPAN simulator together. Docker Engine with
Compose v2, or Docker Desktop, and internet access for the first build are required.
The build pins Home Assistant 2026.9.3, SPAN integration 2.1.2 and PanelBench 2.5.3.

From the BaseHack repository root:

```sh
docker compose -f compose.energy-lab.yaml up --build -d --wait --wait-timeout 300
```

For the standalone ZIP, extract it, open a terminal in the extracted directory,
and run:

```sh
docker compose up --build -d --wait --wait-timeout 300
```

Open **http://localhost:8123** on that computer, or
**http://COMPUTER-LAN-IP:8123** from another device on the same network. Allow inbound
TCP 8123 through the host firewall if needed. Finish Home Assistant's first-run
onboarding to create your own owner account. Open **Energy Lab** in the sidebar;
the **Smart Panel** tab contains the circuit and outage controls.

The first build may take several minutes. Account creation and automatic panel
setup can happen independently. A healthy stack has both services marked healthy
in `docker compose ps` (include `-f compose.energy-lab.yaml` when using the repo).

## Included setup

- Home Assistant and the eight-circuit PanelBench simulator on a private Compose network.
- All seven current household package files, the dashboards, battery scenario,
  appliance simulation, and the patched SPAN breaker control.
- Automatic SPAN pairing with certificate verification, stable Docker DNS names,
  area assignments, and a one-time quiet-home reset.
- A locally generated, read-only system credential for PanelBench's meter reads.
  No manual token copy or integration pairing is required.
- Persistent named volumes for HA configuration/accounts/history, panel configuration,
  certificates, and the private bridge credential. The credential is never embedded
  in an image or printed in logs; it is refreshed at HA startup and expires after ten years.

The configuration is a **clean reproduction of the lab in the backup**. It does
not import the original owner's login, tokens, integrations for other services,
recorded history, or saved outage state. No restore upload is needed. Only
allowlisted lab source files enter the images and distribution ZIP; the personal
backup archive and `.local` directory are excluded.

This launches a separate Home Assistant instance. It does not modify an existing
instance. Do not restore the old full backup over it: that would replace the new
accounts and automatic provisioning. The seed refuses an existing unrecognized
HA configuration volume.

## Ports and persistence

Home Assistant listens on the LAN on port 8123. PanelBench's unauthenticated debug
dashboard (18080) and health endpoint (18081) are bound to localhost only. MQTT and
pairing ports stay inside Docker. No router port forwarding is needed.

If another HA instance already uses port 8123, put a `.env` next to the Compose file:

```dotenv
LAB_HA_PORT=18123
LAB_PANEL_PORT=28080
LAB_HEALTH_PORT=28081
```

Then open port 18123 instead. Optionally set `LAB_BIND_ADDRESS=127.0.0.1` to allow
access only from the Docker host. Docker must remain running and the host awake.

Stop and start without losing data:

```sh
docker compose down
docker compose up -d --wait --wait-timeout 300
```

Do not add `--volumes`/`-v` to `down` unless you intend to erase this installation.
The seed and reset run only once; ordinary restarts retain your configuration,
fault toggles, battery charge and appliance progress. Helpers with explicit
`initial` values (such as the EV target and current limit) reset to those defaults.
As in the original lab, large appliances start paused after HA
restarts and a bridge outage stops powered appliances. Restoring supply does not
restart appliances automatically. PanelBench relay positions may reset when its
container restarts; persisted fault toggles remain authoritative.

Rebuilding an image does not overwrite an existing volume's HA packages or custom
integrations. This initial installer does not implement in-place schema upgrades;
back up your named volumes before manually migrating an existing installation.

## Troubleshooting

```sh
docker compose ps
docker compose logs --tail 80 homeassistant panelbench
```

Early connection errors are expected while the two services initialize. If setup
does not finish, look for `Energy Lab initialization will retry` in HA's logs.
Check `binary_sensor.lab_span_bridge_connected` and the eight
`binary_sensor.lab_span_*_supply` entities in Developer tools → States. The bridge
must be on; supplies are on unless an outage, fault or open breaker was requested.
Keep the Lab SPAN automations enabled.

The bridge token grants only read access. To connect a separate Base Layer app,
create your own HA long-lived access token through your profile. This Compose
stack includes HA and PanelBench; it does not start Base Layer's frontend/API.

## Distribution and validation

Run `python3 scripts/package-energy-lab.py` from the repository root to create
`artifacts/energy-lab/energy-lab-compose.zip`. Its contents include a default
`compose.yaml`, this README, and the small build context; recipients need neither
the repository nor the old backup. The first build downloads public upstream
source and image dependencies.

The automated smoke test is intentionally restricted to a new disposable lab.
It creates a test owner and operates virtual devices; never run it against a
personal Home Assistant instance. See `smoke_test.py` for usage.

Reference: [Compose startup ordering](https://docs.docker.com/compose/how-tos/startup-order/)
and [Home Assistant Container](https://www.home-assistant.io/installation/linux#install-home-assistant-container).
