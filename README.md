# Base Layer

A React + .NET platform for monitoring home energy and approving device shutoffs.
Clerk handles user sign-in. Home Assistant connects through its native OAuth and
REST APIs—no custom plugin is required.

## Start everything with Docker

Install Git and Docker with Compose v2 (Docker Desktop includes Compose), then
start Docker. Clone this repository and run one command from its root:

```sh
docker compose up --build -d --wait --wait-timeout 600
```

This builds and starts **Base Layer, its .NET API, Home Assistant, and PanelBench**.
No local Node, .NET, Python, database installation, or configuration file is needed.
The first build downloads dependencies and may take several minutes.

1. Open **[Base Layer](http://localhost:5173)** and sign in through Clerk.
2. Open **[Home Assistant](http://localhost:8123)** once to create your own account.
3. In Base Layer, choose **Connect a home**, enter `http://localhost:8123`, and
   authorize access. The virtual household and SPAN panel are already provisioned.

The default uses this project's public Clerk development application. To use your
own Clerk app, copy `.env.example` to `.env` and set both `CLERK_PUBLISHABLE_KEY` and
`CLERK_ISSUER`, then rerun the startup command. These are public settings; no Clerk
secret key is required. Sign-in still requires internet access.

The frontend serves a compiled build and proxies `/api` to the API container.
The API connects to HA and PanelBench over Docker's network, while HA authorization
opens the browser-accessible localhost address. SQLite and token-encryption keys
are stored together in the `app-data` named volume; lab accounts and state also
persist in named volumes. The app is available only on this machine; HA is available
on the LAN by default, as in the standalone lab.

```sh
docker compose ps                         # all four services should be healthy
docker compose logs --tail 80             # diagnose startup problems
docker compose down                       # stop, preserving accounts and data
```

Run the startup command again to resume or rebuild after pulling changes. Do not
add `--volumes` to `down` unless you intend to erase the installation. Existing HA
volumes retain their seeded configuration; see the [lab upgrade limitation](docs/home-assistant/portable/README.md#ports-and-persistence).

If ports are already occupied (including by the standalone lab or `scripts/dev.sh`),
set `APP_PORT`, `LAB_HA_PORT`, `LAB_PANEL_PORT`, and `LAB_HEALTH_PORT` in a root `.env`;
see `.env.example`. The app's OAuth URLs follow these port settings automatically.
Always open the app and connect HA using **localhost**, including the chosen ports.
The full stack uses its own `baselayer` Compose project and fresh volumes; it does
not import accounts or connected homes from an existing local installation.

This setup is for local use with Clerk authentication enabled. The API runs in
Development to permit the explicitly allowlisted local HTTP lab. The separate
Python data collection/model-training tools are offline jobs, not services required
by the app; their setup remains in `baselayer_data/` and `baselayer_model/`.

Compose waits for dependency health checks before starting the API and frontend;
see Docker's [startup-order documentation](https://docs.docker.com/compose/how-tos/startup-order/).

## Start only Home Assistant

Install Git and Docker with Compose v2 (Docker Desktop includes Compose), then
start Docker. Internet access is required for the first build.

```sh
git clone https://github.com/sftwre/baselayer.git
cd baselayer
docker compose -f compose.energy-lab.yaml up --build -d --wait --wait-timeout 300
```

If you already cloned the repository, run the Compose command from its root.
Allow several minutes for the first build. It starts Home Assistant and PanelBench,
loads the virtual household devices and dashboards, and automatically configures
the SPAN connection and private bridge credentials.

Open [http://localhost:8123](http://localhost:8123) on the Docker host, or
`http://<host-LAN-IP>:8123` from another device on the same network. Create your own
Home Assistant account, then choose **Energy Lab** in the sidebar. No backup upload
or manual token copying is required. Allow inbound TCP 8123 through the host
firewall if needed; keep Docker running and the host awake.

If port 8123 is already in use, create a `.env` file in the repository root containing
`LAB_HA_PORT=18123` before starting, then open port 18123 instead. Additional port
overrides and troubleshooting are in the [portable Energy Lab guide](docs/home-assistant/portable/README.md).

Check status or stop the lab while preserving its accounts and data:

```sh
docker compose -f compose.energy-lab.yaml ps
docker compose -f compose.energy-lab.yaml down
```

Run the startup command again to resume. Named volumes preserve the installation;
adding `--volumes` to `down` would erase it. A shareable standalone ZIP is available
at [artifacts/energy-lab/energy-lab-compose.zip](artifacts/energy-lab/energy-lab-compose.zip).

## Develop Base Layer in Docker with automatic reload

With Home Assistant and PanelBench already running (the standalone lab above or
your existing local lab), stop any host frontend/API processes, then run:

```sh
docker compose -f compose.dev.yaml up -d --wait --wait-timeout 600
```

Open **http://localhost:5173**. This starts the frontend and API in development
containers. Source files are mounted directly: Vite refreshes frontend edits,
and `dotnet watch` reloads C# changes or restarts the API when necessary. Polling
detects edits through Docker Desktop mounts. Containers run in the background
and restart with Docker unless explicitly stopped.

This mode reuses `backend/src/BaseLayer.Api/data/`, including its database and
encryption keys, and reads the local API configuration if present. Do not run the
host API against that database at the same time. Home Assistant and PanelBench
stay in their existing containers; the API reaches their published ports through
`host.docker.internal`. `LAB_HA_PORT` and `LAB_HEALTH_PORT` override those ports.
Use `CLERK_PUBLISHABLE_KEY` and `CLERK_ISSUER` in the root `.env` for another Clerk app.

```sh
docker compose -f compose.dev.yaml ps
docker compose -f compose.dev.yaml logs -f --tail 50 frontend api
docker compose -f compose.dev.yaml restart frontend  # after npm dependency changes
docker compose -f compose.dev.yaml down              # preserves saved app/lab data
```

No rebuild is needed for ordinary source edits. After changing Compose settings,
rerun the startup command. The regular `compose.yaml` remains the compiled full-stack
setup; use the development command above while editing code.

## Develop Base Layer without Docker

Prerequisites: .NET 10 SDK, Node 24 LTS, npm, and a reachable Home Assistant instance
(either the lab above or your existing installation). The standalone `compose.energy-lab.yaml`
starts Home Assistant and PanelBench; for frontend/API hot reload, run:

```sh
npm --prefix frontend ci
dotnet restore backend/BaseLayer.slnx
./scripts/dev.sh
```

Open **http://localhost:5173**. The API listens on **http://localhost:5080**.
Use `localhost` consistently because Clerk validates the requesting origin.

Clerk's public settings have been configured locally for the supplied development
application. For another checkout, copy `frontend/.env.example` to `.env.local`,
and `backend/src/BaseLayer.Api/appsettings.Local.example.json` to
`appsettings.Local.json`, then fill in the publishable key and issuer. No Clerk
secret key belongs in the frontend; the API verifies session JWTs using Clerk's
public signing keys, issuer, expiry and authorized origin.

## Connect and use a home

1. Sign in through Clerk, then choose **Connect a home**.
2. Enter your Home Assistant address.
3. Authorize in Home Assistant. The browser returns to Base Layer.
4. In **Device settings**, choose **Whole-house meter** or **Sum of device readings**. Similar device/sensor names are suggested for review;
   existing assignments are preserved and ambiguous matches stay unselected.
   Select device power sensors and a shutoff level for each device:
   **Never** blocks all shutoffs. **Sometimes** requires approval at medium grid
   outage risk and permits automatic shutoff at high risk. **Anytime** permits
   automatic reductions at medium or high risk when Smart Shutoff is enabled.
   New devices default to Never; optionally default future devices to Sometimes.
5. In **Device settings**, enable **Smart Shutoff and automatic restore** for each home you want managed.
   The grid outage detection service currently returns **low** for every home:
   usage above 11 kW is allowed, with no automatic reduction or usage alert.
6. At **medium** risk and at or above 11 kW, the server first tries to reduce an
   Anytime EV's charging current, then selects the fewest measured Anytime devices
   needed, largest loads first. After confirmation and fresh measurements, it
   recommends all Sometimes devices needed to get below 11 kW. Notifications name
   these devices; approve them with **Device Recommendations → Turn Off**.
7. At **high** risk, the server also automatically shuts off Sometimes devices as
   needed after Anytime reductions. Never devices stay excluded. If eligible loads
   cannot save enough power, the app asks for additional manual reductions.

See [grid outage detection](docs/grid-outage-detection.md) for the service contract,
risk transitions, and restoration behavior.

Smart Shutoff starts disabled, and existing control permissions become Sometimes
(or Never when control was disabled). Devices without a valid positive power reading
and thermostats are excluded from automatic shutoffs and recommendations. Thermostats
retain their temperature settings. With Smart Shutoff disabled, measured permitted
devices remain available for manual recommendations at medium or high grid risk.

Automatic commands appear in **Recent activity**, use the existing bounded retry and
confirmation flow, and are not recreated for the same device during an uninterrupted
high-usage event. A valid below-limit reading or low grid risk ends the event; a changed risk signal starts a new policy event.
Disabling Smart Shutoff, removing device permission, or a risk downgrade stops ineligible automatic
attempts; it cannot undo a command already sent. The server must remain running and
connected to Home Assistant for automatic control.

Base Layer shutoffs enter the **Restore queue** before dispatch, including approved
and automatically managed Sometimes devices. Observed confirmation starts the off timer. The queue persists across server restarts and records each
load's measured watts immediately before its first shutoff attempt. Devices already
off are not added. Estimates without a valid positive reading are shown as unknown
and cannot restore automatically. Old commands without saved estimates are not
retroactively queued.

Queue state lives in `RestoreQueueEntries`, with `DeviceId` as both its primary key
and a foreign key to `Device.Id`. Each device has at most one active queue entry;
names, permissions, sensor mappings, and EV configuration stay on the shared device
record. Queue rows hold `QueuedUtc`, `EligibleSinceUtc`, `EstimatedWatts`, `Status`,
`PowerOn`, `AtFront`, `TargetCurrentAmps`, and `LastManagedCurrentAmps`. Finishing or
removing a restoration deletes its queue row while retaining the device. Deleting
a device or home cascades to its queue entries. On startup, existing device-based
queue state is migrated transactionally into this table and the old queue columns
are removed. The API's `restoreQueue` response stays the same.

With Smart Shutoff enabled, restoration chooses the oldest eligible queued device
that fits, allowing smaller loads to return while a larger one waits. It requires
at least 15 seconds off, 5 seconds of fresh readings with sufficient spare
capacity, and at least 5 seconds between restorations. These shortened delays are
**for the demo only**, not production restart settings. The estimate plus a buffer
of the greater of 500 W or 10% of the estimate must fit strictly below the limit
at medium or high risk. At low risk, the queue can restore above 11 kW; restart
and confirmation delays still apply.
Only one restore command is active at a time; the next device waits for confirmation
and another stable interval. Unknown readings, failed polls, and gaps over 20 seconds
restart the stability window. Lost capacity pauses retries; failed/expired restores
stay visible without being requeued indefinitely. **Keep off** removes the entry and
cancels future restore attempts. Disabling Smart Shutoff pauses the queue; setting a
device to Never removes it. A device turned on outside Base Layer leaves the queue.
The UI distinguishes restore commands from shutoffs. These decisions use measured
power, not battery charge percentage or a guarantee about appliance startup surge.


[Historical fixture notes](docs/local-ha.md) document the virtual house used for
local verification before the plugin folder was removed.

Usage alerts appear for every connected home at medium or high grid risk and at or above its configured limit
(currently 11 kW), including homes not selected in the dashboard. Failed refreshes,
offline homes, and unknown readings do not trigger alerts. Open **Settings** in the sidebar and turn **Browser notifications** on to opt in.
Confirmed Smart Shutoff shutoffs also send a notification when enabled, including
a reminder if more reduction is needed. Recent confirmations are deduplicated across
page navigation; old activity is not replayed as notifications.
The preference is saved per account in this browser. Permission is requested
only when you turn the toggle on. Action notifications wait for automatic reductions
and are deduplicated until risk, status, or the recommended device list changes.
A valid reading below the limit or low risk rearms them. Turning notifications off keeps in-site
alerts active.

Browser notifications require a supported browser and HTTPS (or localhost).
They rely on page polling: the page must remain open, and background tabs may
be delayed. Delivery with the site closed is not implemented; that requires a
service worker, authenticated push-subscription storage, and backend Web Push
sending from the usage polling service.

A power reading of **unknown** is not zero. Devices without meters can still be
controlled if permitted, but are not ranked in power-saving recommendations.

In **Recent activity**, use **Cancel command** to cancel pending, retrying, or
verifying commands. This prevents further attempts; it cannot undo a request
already sent to Home Assistant. Completed command outcomes remain in history.
**Delete home** opens a separate confirmation before permanently removing the
home, device settings, and command history from Base Layer. It also stops pending
work and attempts to revoke its Home Assistant authorization. It does not delete
devices or data inside Home Assistant.
Device-sum mode includes mapped device readings regardless of control permissions
or on/off state, including standby power. It excludes unmonitored loads and is
labeled **Monitored device usage**. A missing configured reading makes the total
unknown. Do not map overlapping measurements, such as a power strip and its
individual appliances; the same sensor cannot be assigned twice.
A hosted API needs network access to the user's HA instance (e.g. an appropriate
HTTPS address). A user's private `localhost` URL cannot be reached from a cloud
server.

## Device usage history

The backend now records [device usage history](docs/usage-history.md) from Home
Assistant polling: minute summaries for 90 days, hourly summaries for two years,
and observed device state changes. Authenticated history endpoints return power,
estimated energy, and coverage so missing readings remain visible. Recording runs
while the API is running, independently of browser activity and shutoff permissions.

## Code structure

**[Smart Usage](docs/smart-usage.md)** is available from the sidebar lightbulb.
Preview battery runtime and the fewest shutoffs needed to extend it, then confirm
device switches. Turn-ons are checked against the 11 kW battery limit and fresh
power readings; a confirmed plan keeps its off devices out of automatic restore.

The [battery telemetry API](docs/battery-api.md) exposes each home's current charge
and capacity through a replaceable provider. It currently uses a configurable,
time-based simulation while the external Core API is unavailable.

```text
frontend/src/
  app/                   App shell and Clerk boundary
  components/ui/         Reusable buttons, badges, dialogs
  features/dashboard/    Live usage and polling
  features/devices/      Device list, permissions, meter mapping
  features/commands/     Approval state and command results
  features/connections/  Native Home Assistant OAuth flow
  lib/                   Typed API client and shared contracts
backend/src/
  BaseLayer.Api/         Controllers, auth, HA provider, background polling
  BaseLayer.Application/ Provider/repository interfaces and business services
  BaseLayer.Domain/      Home, device, command and authorization state
  BaseLayer.Data/        EF Core SQLite context and repository
backend/tests/           Domain/service/provider regression tests
baselayer_data/         Data engineering: ERCOT pulls, datasets (.parq), ercot/ API client
baselayer_model/        Modeling: Austin outage risk classifier (XGBoost) train/evaluate
```

This follows the Done With School controller → service → repository style, with
business logic and provider interfaces separated from the API host. React
components share primitives; data fetching and approval state live in hooks.

## Verification

```sh
dotnet test backend/BaseLayer.slnx
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run format:check
```

See [verification results](docs/verification.md) for the local end-to-end run.

## Scope and deployment

This is a local MVP for a single API instance. SQLite and ASP.NET Data Protection
keys live under the API's ignored `data/` folder. Back up the database and keys
together; protect the key directory with deployment-level permissions/encryption.
OAuth tokens never appear in the browser API responses. Connection revocation
stops future commands; already completed actions are not reversible through
revocation.

The API allows configured Home Assistant origins only, rejects redirects on
provider HTTP calls, and permits plain HTTP only for the explicit development
loopback fixture. Configure trusted HTTPS origins before hosting. Home Assistant
OAuth grants account-level access; Base Layer's device allowlist further restricts
what this application may control—it is not a per-device OAuth scope.

Before production: replace startup schema creation with migrations; choose a
production database and cross-instance command leases; persist/protect encryption
keys; add operational monitoring, telemetry retention, and deployment-specific
network controls. No purchases, hosting deployment, or paid subscriptions are part
of this setup.

Data engineering lives in [`baselayer_data/`](baselayer_data/), including ERCOT Public Data API and live dashboard access in [`baselayer_data/ercot/`](baselayer_data/ercot/). Run `python3 baselayer_data/ercot/predict_outage.py` for the grid outage prediction. The XGBoost outage risk classifier trained on those datasets lives in [`baselayer_model/`](baselayer_model/).

Automatic EV reductions also save the original current limit in the restore queue. After 5 seconds of stable spare capacity, the queue raises the amps as far as the available capacity permits, with a 500 W buffer and fresh confirmation before each further increase. Partial restores keep their queue priority until the original limit is reached. Manual current changes cancel that compensation target. See [EV charging](docs/ev-charging.md).
