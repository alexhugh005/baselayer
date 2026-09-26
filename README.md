# Base Layer

A React + .NET platform for monitoring home energy and approving device shutoffs.
Clerk handles user sign-in. Home Assistant connects through its native OAuth and
REST APIs—no custom plugin is required.

## Start locally

Prerequisites: .NET 10 SDK, Node 24 LTS, npm, and a reachable Home Assistant instance.
Connect to an existing Home Assistant instance; the former plugin and virtual-house fixture have been removed from this repository.

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
   **Never** blocks all shutoffs, **Sometimes** requires your approval,
   and **Anytime** permits automatic shutoffs when Smart Shutoff is enabled.
   New devices default to Never; optionally default future devices to Sometimes.
5. In **Device settings**, enable **Smart Shutoff and automatic restore** for each home you want managed.
   At or above the 11 kW limit, the server selects the fewest measured Anytime
   devices needed to get strictly below the limit, largest loads first. If those
   loads cannot cover the reduction, it selects all eligible Anytime devices.
6. After command confirmation and a fresh usage reading, any remaining overload
   produces recommendations for the fewest Sometimes devices. Approve them with
   **Device Recommendations → Turn Off**. If they cannot save enough power,
   the app tells you to turn off additional appliances yourself to help the battery
   return to a working state. This is usage guidance, not battery-state telemetry.

Smart Shutoff starts disabled, and existing control permissions become Sometimes
(or Never when control was disabled). Devices without a valid positive power reading
and thermostats are excluded from automatic shutoffs and recommendations. Thermostats
retain their temperature settings. With Smart Shutoff disabled, measured permitted
devices remain available for manual recommendations.

Automatic commands appear in **Recent activity**, use the existing bounded retry and
confirmation flow, and are not recreated for the same device during an uninterrupted
high-usage event. A valid below-limit reading starts a new event next time usage rises.
Disabling Smart Shutoff or changing a device away from Anytime stops future automatic
attempts; it cannot undo a command already sent. The server must remain running and
connected to Home Assistant for automatic control.

Confirmed Base Layer shutoffs enter the **Restore queue**, including approved
Sometimes devices. The queue persists across server restarts and records each
load's measured watts immediately before its first shutoff attempt. Devices already
off are not added. Estimates without a valid positive reading are shown as unknown
and cannot restore automatically. Old commands without saved estimates are not
retroactively queued.

With Smart Shutoff enabled, restoration chooses the oldest eligible queued device
that fits, allowing smaller loads to return while a larger one waits. It requires
at least 15 seconds off, 5 seconds of fresh readings with sufficient spare
capacity, and at least 5 seconds between restorations. These shortened delays are
**for the demo only**, not production restart settings. The estimate plus a buffer
of the greater of 500 W or 10% of the estimate must fit strictly below the limit.
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

Usage alerts appear for every connected home at or above its configured limit
(currently 11 kW), including homes not selected in the dashboard. Failed refreshes,
offline homes, and unknown readings do not trigger alerts. Open **Settings** in the sidebar and turn **Browser notifications** on to opt in.
Confirmed Smart Shutoff shutoffs also send a notification when enabled, including
a reminder if more reduction is needed. Recent confirmations are deduplicated across
page navigation; old activity is not replayed as notifications.
The preference is saved per account in this browser. Permission is requested
only when you turn the toggle on. Each home sends one notification per high-usage event;
a valid reading below the limit rearms it. Turning notifications off keeps in-site
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
ercot/                  ERCOT Public Data API and live dashboard access
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

ERCOT Public Data API and live dashboard access lives in [`ercot/`](ercot/). Run `python3 ercot/predict_outage.py` for the grid outage prediction.

Automatic EV reductions also save the original current limit in the restore queue. After 5 seconds of stable spare capacity, the queue raises the amps as far as the available capacity permits, with a 500 W buffer and fresh confirmation before each further increase. Partial restores keep their queue priority until the original limit is reached. Manual current changes cancel that compensation target. See [EV charging](docs/ev-charging.md).
