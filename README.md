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
4. In **Device settings**, choose **Use a whole-house meter** or **Add up device
   power readings**. Similar device/sensor names are suggested for review;
   existing assignments are preserved and ambiguous matches stay unselected.
   Select device power sensors and devices allowed to be controlled. **Allow all current devices** and
   **Allow future devices** are independent choices.
5. At 11 kW or above, review suggested devices, then choose **Review shutoff**
   and **Approve & turn off**. No command is generated merely by exceeding the
   limit. Command activity distinguishes pending, verifying, retrying, confirmed,
   failed and expired results.

[Historical fixture notes](docs/local-ha.md) document the virtual house used for
local verification before the plugin folder was removed.

A power reading of **unknown** is not zero. Devices without meters can still be
controlled if permitted, but are not ranked in power-saving recommendations.

In **Recent activity**, use **Revoke command** to cancel pending, retrying, or
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

## Code structure

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

ERCOT Public Data API and live dashboard access lives in [`ercot/`](ercot/).
