<div align="center">

<img src="assets/logo.png" alt="DotnetDeployer.Fleet logo" width="96" />

# DotnetDeployer.Fleet

**Run DotnetDeployer on managed machines — up and running in 3 commands.**

DotnetDeployer is the deployment engine. DotnetDeployer.Fleet adds a coordinator,
workers, repository cloning, secrets, live logs, and package artifacts so the
same `deployer.yaml` can run remotely or distributed across your own machines.

Built on top of [DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer).

</div>

---

## Quick Start

> **Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Git](https://git-scm.com/).

**1. Install the CLI**

```bash
dotnet tool install -g DotnetDeployer.Fleet.Tool
```

**2. Start a coordinator**

```bash
fleet coordinator
```

**3. Connect a worker**

```bash
fleet worker
```

That's it. The worker auto-detects the coordinator running on the same machine — no URL or token needed. (Across machines on the LAN, mDNS finds the coordinator automatically and you only need to pass `--token <token>` once.)

You now have managed remote execution for DotnetDeployer. Add projects from the [Desktop App](#desktop-app), trigger deploys, and watch live logs.

---

## Table of Contents

- [How It Works](#how-it-works)
- [Installation](#installation)
  - [CLI Tool](#cli-tool)
  - [Desktop App](#desktop-app)
- [Getting Started](#getting-started)
  - [Step 1 — Start the Coordinator](#step-1--start-the-coordinator)
  - [Step 2 — Connect Workers](#step-2--connect-workers)
  - [Step 3 — Add Projects and Deploy](#step-3--add-projects-and-deploy)
- [Production Setup](#production-setup)
  - [Install as OS Services](#install-as-os-services)
  - [Custom Storage Paths](#custom-storage-paths)
  - [Security](#security)
- [Windows Guide](docs/WINDOWS.md)
- [CLI Reference](#cli-reference)
- [Configuration](#configuration)
- [Architecture](#architecture)
- [Coordinator ↔ Worker Protocol](#coordinator--worker-protocol)
- [API Reference](#api-reference)
- [Development](#development)
- [Running Tests](#running-tests)
- [FAQ](docs/FAQ.md) — troubleshooting and common questions (Spanish)

---

## How It Works

```
                    You add a project
                          │
                          ▼
               ┌─────────────────────┐
               │    Coordinator      │  Queues jobs, streams logs,
               │    (fleet coord.)   │  auto-polls for new commits
               └────────┬────────────┘
                        │
              ┌─────────┼─────────┐
              ▼         ▼         ▼
          ┌────────┐┌────────┐┌────────┐
          │Worker 1││Worker 2││Worker N│  Clone/fetch repo,
          └────────┘└────────┘└────────┘  run DotnetDeployer,
                                          stream logs back
```

1. **Add a project** — paste a Git URL and branch in the Desktop App (or call the API).
2. **Trigger a deploy** — click *Deploy Now* or let automatic polling detect new commits.
3. **A worker claims the job** — atomic claim ensures only one worker wins, even under contention.
4. **Clone, test and deploy** — the worker clones (or fetches) the repo with full history, tests its root solution by default, then runs `dnx dotnetdeployer.tool -y`.
5. **Live logs** — the coordinator streams log lines via SSE; the Desktop App shows them in real time.
6. **Repo cache** — cloned repos stay on disk for faster subsequent deploys; an LRU policy prevents disk exhaustion.

### Requirements for Target Repos

Each repo you deploy must contain a **`deployer.yaml`** at the root. See the [DotnetDeployer docs](https://github.com/SuperJMN/DotnetDeployer) for the format.

Deployment projects run solution tests by default. The repository root must contain exactly one `.slnx` or `.sln` file; Fleet runs a best-effort `dotnet workload restore <solution>` followed by `dotnet test <solution> -c Release --nologo`. A failing test command stops the deployment before DotnetDeployer starts. Disable **Run solution tests before deployment** when creating or editing a project to opt out. Package-only builds do not run this test gate.

> The worker runs `dnx dotnetdeployer.tool -y` — .NET 10's `dnx` downloads and caches the tool automatically. No global install or tool manifest needed.

---

## Installation

### CLI Tool

```bash
dotnet tool install -g DotnetDeployer.Fleet.Tool
```

This gives you the `fleet` command. Verify with:

```bash
fleet version
```

To update later:

```bash
dotnet tool update -g DotnetDeployer.Fleet.Tool
```

The older `DotnetFleet.Tool` package remains available as a compatibility
package for existing installs. Do not install both packages globally at the same
time because both expose the same `fleet` command.

### Desktop App

Download the latest release for your platform from [GitHub Releases](https://github.com/SuperJMN/DotnetDeployer.Fleet/releases):

| Platform | Format |
|---|---|
| Linux x64 / arm64 | AppImage |
| Linux x64 | .deb |
| Windows x64 | Installer (.exe) |
| macOS x64 / arm64 | .dmg |

Or build from source:

```bash
cd src/DotnetDeployer.Fleet.Desktop
dotnet run
```

#### Auto-updates on Linux (recommended)

The desktop app does not self-update. On Linux, install the AppImage with an
external manager so new releases are pulled automatically — the same idea as
[Obtainium](https://github.com/ImranR98/Obtainium) on Android.

The most convenient option today is [Zap](https://github.com/srevinsaju/zap):

```bash
# Install the manager once
curl https://raw.githubusercontent.com/srevinsaju/zap/main/install.sh | bash

# Install DotnetDeployer.Fleet from GitHub Releases (picks the right arch automatically)
zap install --from-github SuperJMN/DotnetDeployer.Fleet

# Later, update everything tracked by Zap
zap update --all
```

Alternatives: [AM / AppMan](https://github.com/ivan-hc/AM) (CLI, broader catalog)
or [Gear Lever](https://flathub.org/apps/it.mijorus.gearlever) (GTK4 GUI for
GNOME). Both can track the GitHub release feed and surface updates without any
support code in DotnetDeployer.Fleet itself.

On Windows and macOS, just download the new installer / DMG from the releases
page when you want to upgrade.

---

## Getting Started

### Step 1 — Start the Coordinator

```bash
fleet coordinator --port 5000
```

On first run the coordinator auto-generates a **JWT secret** and a **registration token**, saves them to `~/.fleet/coordinator/config.json`, and prints a banner:

```
  ╔══════════════════════════════════════════════════════════════╗
  ║              DotnetDeployer.Fleet Coordinator                ║
  ╠══════════════════════════════════════════════════════════════╣
  ║  Listening on:       http://0.0.0.0:5000                     ║
  ║  Admin credentials:  admin / admin                           ║
  ║  Registration token: f7a2b9c1d4e5...                         ║
  ╠══════════════════════════════════════════════════════════════╣
  ║  Connect workers with:                                       ║
  ║    fleet worker --coordinator http://<host>:5000 \            ║
  ║                 --token f7a2b9c1d4e5...                      ║
  ╚══════════════════════════════════════════════════════════════╝
```

Secrets are generated once and reused across restarts — you'll always get the same token unless you explicitly override it.

### Step 2 — Connect Workers

DotnetDeployer.Fleet has two layers of auto-discovery so you rarely need to type the coordinator URL or copy a token:

**Same machine (no flags needed):**

```bash
fleet worker
```

The worker reads `~/.fleet/coordinator/config.json` (or the installed coordinator service) and connects to `http://localhost:<port>` with the token already present on disk.

**Other machine on the LAN (token only):**

```bash
fleet worker --token <token>
```

The worker queries multicast DNS (`_dotnetfleet._tcp.local.`), finds the coordinator advertised by the other host, and uses its URL. The token is **never** broadcast — you still need to provide it once.

**Multiple coordinators on the LAN, or no mDNS:**

```bash
fleet worker --coordinator http://myserver:5000 --token <token>
```

If `fleet worker` finds more than one coordinator, it lists them and asks you to pick with `--coordinator`. If your network blocks multicast, pass both flags explicitly. To skip discovery altogether, add `--no-discover`.

The worker self-registers on first run, persists its credentials to `~/.fleet/worker-<hostname>/worker.json`, and immediately starts polling for jobs.

**Scale out** by adding more workers — each one is an independent process:

```bash
fleet worker --name build-01
fleet worker --name build-02
fleet worker --name build-03
```

> After the first registration, even `--token` is no longer needed: the worker authenticates with its own per-instance credentials.

### Step 3 — Add Projects and Deploy

1. Open the **Desktop App** and connect to `http://localhost:5000`.
2. Log in with `admin` / `admin`.
3. Add a project with a Git URL (e.g., `https://github.com/you/your-app.git`) and branch.
4. Click **Deploy Now** — watch live logs as the worker clones the repo and runs DotnetDeployer.

To auto-deploy on new commits, set a **polling interval** (in minutes) on the project. The coordinator checks for new commits with `git ls-remote` and enqueues a deploy whenever the SHA changes.

---

## Production Setup

### Install as OS Services

For production, install the coordinator and workers as OS services so they start on boot and restart on failure. Linux uses **systemd**; Windows uses native **Windows Services**.

You have two equivalent ways to install. Pick whichever you prefer.

#### Option A — Zero-install (recommended for first-time setup)

If you just installed .NET and don't yet have anything else, you can do the whole thing in one command using `dnx` (.NET 10's tool runner).

On Linux, you don't need `sudo` — the tool re-executes itself under `sudo` automatically, preserving `PATH`, `DOTNET_ROOT` and `HOME` so root can still find your per-user .NET install. On Windows, the command requests UAC elevation automatically; for unattended scripts, start PowerShell as Administrator first:

```bash
dnx dotnetdeployer.fleet.tool coordinator install --port 5000
```

On Linux, you'll be prompted once for your sudo password. The first time you do this, the installer also detects it's running from `dnx`'s ephemeral cache and **automatically performs `dotnet tool install -g DotnetDeployer.Fleet.Tool`** for the calling user, so the systemd unit's `ExecStart=` points at the stable global-tool path (`~/.dotnet/tools/fleet`) instead of a cache location that may disappear later.

On Windows, the installer uses a service-local tool path under `%ProgramData%\DotnetFleet\tools`, so services do not depend on a user's profile or `dnx` cache.

A worker on the same machine looks the same — and thanks to local auto-discovery you don't need `--coordinator` or `--token`:

```bash
dnx dotnetdeployer.fleet.tool worker install --name build-01
```

For a worker on a **different** machine, mDNS finds the coordinator's URL automatically — you only have to supply the token:

```bash
dnx dotnetdeployer.fleet.tool worker install --token <token> --name build-01
```

#### Option B — Install the global tool first, then use `fleet` directly

```bash
# One-time:
dotnet tool install -g DotnetDeployer.Fleet.Tool

# Then (Linux: no sudo needed because fleet re-execs itself; Windows: UAC elevation is requested when needed):
fleet coordinator install --port 5000

# Worker on the same machine — auto-discovered:
fleet worker install --name build-01

# Worker on a remote host — pass the token (URL is discovered via mDNS):
fleet worker install --token <token> --name build-01
```

> **Manual sudo fallback.** If the automatic re-exec doesn't work for your setup (e.g., passwordless policies, custom sudoers), call `sudo` yourself but use the **absolute path** to the binary and preserve `DOTNET_ROOT` (`~/.dotnet/tools` is not in `secure_path`, and `sudo` strips `DOTNET_ROOT`). The tool detects it's already root and skips the re-exec:
> ```bash
> sudo env "PATH=$PATH" "DOTNET_ROOT=$HOME/.dotnet" "HOME=$HOME" \
>   ~/.dotnet/tools/fleet coordinator install --port 5000
> ```
> Do **not** run `sudo fleet ...` directly — it will fail with `sudo: fleet: command not found`.

#### Managing the services

```bash
# Linux status
sudo systemctl status fleet-coordinator
sudo systemctl status fleet-worker-build-01

# Linux logs
journalctl -u fleet-coordinator -f
journalctl -u fleet-worker-build-01 -f

# Update tool + restart all local fleet services in one go (no sudo — auto-elevates)
fleet update

# Uninstall (no sudo — auto-elevates)
fleet coordinator uninstall
fleet worker uninstall --name build-01
```

```powershell
# Windows status
Get-Service fleet-coordinator
Get-Service fleet-worker-build-01

# Windows stop/start
Stop-Service fleet-coordinator
Start-Service fleet-coordinator

# Update tool + restart all local fleet services (requests UAC elevation when needed)
fleet update

# Uninstall (requests UAC elevation when needed)
fleet coordinator uninstall
fleet worker uninstall --name build-01
```

On Linux, services run as the calling user (via `SUDO_USER`) and write unit files to `/etc/systemd/system/`. On Windows, services run under LocalSystem and store the service-local tool under `%ProgramData%\DotnetFleet\tools`.

For Windows-specific details, including Task Manager names, log paths, updates, UAC behavior and firewall rules, see [DotnetDeployer.Fleet on Windows](docs/WINDOWS.md).

Both platforms use these service names:

| Component | Service name |
|---|---|
| Coordinator | `fleet-coordinator` |
| Worker | `fleet-worker-{name}` |

> **Note:** Native service installation is supported on Linux with systemd and Windows Services. On other platforms, use the foreground commands with a process manager of your choice (e.g., `launchd` or Docker).

### Custom Storage Paths

Foreground commands store data under `~/.fleet/` by default. Installed Linux services use the same user-home layout; installed Windows services default to `%ProgramData%\DotnetFleet\...`.

| Component | Default path |
|---|---|
| Coordinator | `~/.fleet/coordinator/` |
| Worker `build-01` | `~/.fleet/worker-build-01/` |
| Cloned repos | `<worker-data-dir>/fleet-repos/` |

You can redirect **everything** to a different location with `--data-dir`:

```bash
# Store repos on an external drive
fleet worker install \
  --coordinator http://myserver:5000 \
  --token <token> \
  --name build-01 \
  --data-dir /mnt/external-drive/fleet
#  → Repos will be cloned to /mnt/external-drive/fleet/fleet-repos/
```

For even more control, set `Worker:RepoStoragePath` to an **absolute path** in `appsettings.json` — the repos will go directly there, regardless of `--data-dir`:

```json
{
  "Worker": {
    "RepoStoragePath": "/mnt/external-drive/repos"
  }
}
```

You can also set a disk budget per worker:

```bash
fleet worker --coordinator http://myserver:5000 --token <token> --max-disk 50
```

When total cached repo size exceeds the limit (in GB), the worker automatically evicts the least-recently-used repos.

### Security

| Topic | Details |
|---|---|
| **Admin password** | Default is `admin`. Change it: `fleet coordinator --admin-password <pass>` |
| **JWT secret** | Auto-generated on first run, stored in `config.json`. Override: `--jwt-secret <secret>` |
| **Registration token** | Shared out-of-band with workers. Rotate it by restarting the coordinator with `--token <new-token>` |
| **Worker auth** | Each worker gets a unique `(id, secret)` pair at registration. The JWT carries a `worker_id` claim, and every endpoint verifies it matches — a stolen token cannot act on behalf of another worker |
| **HTTPS** | Use a reverse proxy (nginx, Caddy) to terminate TLS in front of the coordinator |

### Upgrading

Upgrading DotnetDeployer.Fleet replaces only the tool binary — **all data is preserved** (projects, jobs, worker credentials, configuration, cached repos). Data lives under `--data-dir` (default `~/.fleet/`), which is independent of the tool installation.

#### One-shot update

If the coordinator and/or workers run as installed services on this machine, a single command updates the service tool and restarts every local fleet service:

```bash
fleet update
```

On Linux, `fleet update` (with no `sudo`) re-executes itself with `sudo` automatically, preserving `PATH`, `DOTNET_ROOT` and `HOME`. You'll be prompted for your password once. On Windows, it requests UAC elevation automatically; for unattended scripts, run it from an Administrator terminal.

Options:

```
--skip-tool-update      Only restart services; skip 'dotnet tool update'
--version <version>     Pin a specific installed Fleet tool version
--prerelease            Allow prerelease versions when updating
```

#### Manual update

If you'd rather do it by hand:

```bash
dotnet tool update -g DotnetDeployer.Fleet.Tool
fleet version   # verify the new version
sudo systemctl restart fleet-coordinator
sudo systemctl restart fleet-worker-<name>
```

On Windows:

```powershell
Stop-Service fleet-coordinator
Stop-Service fleet-worker-<name>
dotnet tool update --tool-path "$env:ProgramData\DotnetFleet\tools" DotnetDeployer.Fleet.Tool
fleet version   # verify the new version
Start-Service fleet-coordinator
Start-Service fleet-worker-<name>
```

If the service-local tool was originally installed from the compatibility
package, use `DotnetFleet.Tool` in the manual `dotnet tool update` command
instead. The automatic `fleet update` command detects the installed package for
you.

> Linux systemd unit files point at the **global tool** (`~/.dotnet/tools/fleet`), and Windows services point at the service-local tool under `%ProgramData%\DotnetFleet\tools`. There's no need to re-run `install` after a tool update.

#### What is preserved across upgrades

| Component | Persisted data | Location |
|---|---|---|
| Coordinator | SQLite database (projects, jobs, history), `config.json` (JWT secret, registration token) | `~/.fleet/coordinator/` or `%ProgramData%\DotnetFleet\coordinator\` for Windows services |
| Worker | `worker.json` (id + secret), cached git repos | `~/.fleet/worker-{name}/` or `%ProgramData%\DotnetFleet\worker-{name}\` for Windows services |

#### Rollback

If something goes wrong, downgrade to a specific version:

```bash
fleet update --version <previous-version>
```

---

## CLI Reference

```
fleet coordinator [options]            Run coordinator in foreground
fleet coordinator install [options]    Install as OS service
fleet coordinator uninstall            Remove OS service
fleet coordinator status               Show service status

  --port, -p <port>             HTTP port (default: 5000)
  --data-dir <path>             Data directory (default: ~/.fleet/coordinator)
  --token, -t <token>           Registration token (auto-generated)
  --jwt-secret <secret>         JWT signing secret (auto-generated)
  --admin-password <pass>       Admin password (default: admin)
  --urls <urls>                 ASP.NET Core URLs override
  --no-mdns                     Disable mDNS advertising on the LAN

fleet worker [options]                 Run worker in foreground
fleet worker install [options]         Install as OS service
fleet worker uninstall [--name <n>]    Remove OS service
fleet worker status [--name <n>]       Show service status
fleet worker unregister --name <n>     Unregister worker from coordinator

  --coordinator, -c <url>       Coordinator URL (auto-discovered if omitted)
  --token, -t <token>           Registration token (auto-discovered for local coordinators)
  --name, -n <name>             Worker display name (default: hostname)
  --data-dir <path>             Data directory (default: ~/.fleet/worker-{name})
  --poll-interval <secs>        Polling interval in seconds (default: 10)
  --max-disk <gb>               Max disk usage in GB (default: 10)
  --no-discover                 Disable local + mDNS auto-discovery
  --admin-user <user>           Admin username for unregister (default: admin)
  --admin-password <pass>       Admin password for unregister (default: admin)

fleet version                          Show version

fleet update [options]                 Update tool + restart local services
  --skip-tool-update            Only restart services
  --version <version>           Pin a specific installed Fleet tool version
  --prerelease                  Allow prerelease versions
```

---

## Configuration

All settings can be provided via CLI flags, `appsettings.json`, or **environment variables** (using `__` as separator, e.g., `Worker__PollIntervalSeconds=5`).

### Coordinator

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `Data Source=fleet.db` | SQLite connection string |
| `Jwt:Secret` | *(auto-generated)* | JWT signing secret — **change in production** |
| `Jwt:Issuer` / `Jwt:Audience` | `DotnetFleet` | JWT issuer / audience |
| `Workers:RegistrationToken` | *(auto-generated)* | Shared secret for worker self-registration |

### Worker

| Setting | Default | Description |
|---|---|---|
| `Worker:CoordinatorBaseUrl` | `http://localhost:5000` | Coordinator URL |
| `Worker:RegistrationToken` | *(unset)* | Used only for first-time self-registration |
| `Worker:Id` / `Worker:Secret` | *(from credentials file)* | Skip self-registration if both provided |
| `Worker:CredentialsFile` | `worker.json` | Where to persist credentials after registration |
| `Worker:Name` | `Environment.MachineName` | Display name shown in the workers list |
| `Worker:PollIntervalSeconds` | `10` | Queue polling cadence |
| `Worker:HeartbeatIntervalSeconds` | `30` | Heartbeat cadence |
| `Worker:RepoStoragePath` | `fleet-repos` | Where cloned repos live (relative to data-dir, or absolute) |
| `Worker:MaxDiskUsageBytes` | `10 GiB` | LRU eviction threshold |

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│              DotnetDeployer.Fleet.Coordinator       │
│                                                     │
│  ASP.NET Core Minimal API   • job queue & polling   │
│  EF Core / SQLite           • repo-cache metadata   │
│  JWT (User + Worker roles)  • SSE log fan-out       │
└─────────────────────────────────────────────────────┘
        ▲                              ▲
        │ HTTP (REST + SSE)            │ HTTP (REST)
        │                              │
        ▼                              ▼
┌──────────────────────────┐   ┌──────────────────────────┐
│ DotnetDeployer.Fleet.App │   │ DotnetDeployer.Fleet.Worker × N │
│                          │   │                          │
│ Login → Projects →       │   │ register → login → poll  │
│ Trigger Deploy →         │   │ → clone/fetch → run dnx  │
│ Live Logs                │   │ → stream logs            │
└──────────────────────────┘   └──────────────────────────┘
```

Coordinator and Workers are **separate processes**. The primary implementation assemblies now use the `DotnetDeployer.Fleet.*` names; `DotnetFleet.Tool` remains only as a compatibility NuGet package for existing installs.

### Components

| Project | Role |
|---|---|
| `DotnetDeployer.Fleet.Tool` | **CLI tool** (`fleet`) — install via `dotnet tool install -g DotnetDeployer.Fleet.Tool` |
| `DotnetFleet.Tool` | Compatibility CLI package for existing installs |
| `DotnetDeployer.Fleet.Tooling` | Shared CLI implementation used by both tool packages |
| `DotnetDeployer.Fleet.Core` | Domain models, interfaces, enums |
| `DotnetDeployer.Fleet.Coordinator` | ASP.NET Core API + queue + polling + EF Core SQLite |
| `DotnetDeployer.Fleet.Worker` | Standalone Generic Host: git clone/fetch + `dnx dotnetdeployer.tool -y` |
| `DotnetDeployer.Fleet.Api.Client` | Typed HTTP client used by the Desktop App |
| `DotnetDeployer.Fleet.App` | Avalonia 12 shared ViewModels + Views |
| `DotnetDeployer.Fleet.Desktop` | Desktop host (Windows / Linux / macOS) |
| `DotnetDeployer.Fleet.Browser` | WebAssembly host (optional) |
| `DotnetDeployer.Fleet.Android` | Android host |
| `DotnetDeployer.Fleet.Tests` | xUnit unit tests |

### Worker Lifecycle

Each worker runs an independent loop:

1. **Bootstrap** — load credentials from config / credentials file, or self-register against the coordinator.
2. **Login** — exchange `(workerId, secret)` for a JWT (cached, refreshed on 401 or near-expiry).
3. **Heartbeat** — `POST /heartbeat` on a timer so the coordinator knows it's alive.
4. **Poll** — `POST /api/queue/next` every `PollIntervalSeconds`; claims are atomic.
5. **Execute** — clone or fetch with full history, run the root solution tests when enabled, then run `dnx dotnetdeployer.tool -y`.
6. **Stream** — stdout/stderr line by line, report final status.

### Disk Management

Cached repos live under the worker's `RepoStoragePath`. When total disk usage exceeds `MaxDiskUsageBytes`, the worker evicts least-recently-used repos until enough space is reclaimed. Limits are configurable per worker via the Desktop App's *Workers* tab or `--max-disk`.

---

## Coordinator ↔ Worker Protocol

All worker traffic flows over plain HTTP/JSON. There is no direct DB or queue access from the worker side.

| Step | Endpoint | Auth | Purpose |
|---|---|---|---|
| 1. Register | `POST /api/workers/register` | `X-Registration-Token` header *or* Admin JWT | Issues `{workerId, secret}` once |
| 2. Login | `POST /api/workers/login` | anonymous (body has id+secret) | Returns a Worker JWT |
| 3. Heartbeat | `POST /api/workers/{id}/heartbeat` | Worker JWT | Marks the worker Online |
| 4. Claim | `POST /api/queue/next` | Worker JWT | Atomically claims the oldest queued job |
| 5. Logs | `POST /api/jobs/{id}/logs` | Worker JWT | Stream log lines back |
| 6. Status | `POST /api/jobs/{id}/status` | Worker JWT | Report job completion |

---

## API Reference

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/login` | anonymous | Obtain User JWT |
| `GET` / `POST` / `DELETE` | `/api/projects[/...]` | User | CRUD projects |
| `POST` | `/api/projects/{id}/deploy` | User | Trigger deploy |
| `GET` | `/api/jobs/{id}/logs` | User | Live log stream (SSE) |
| `GET` | `/api/workers` | Admin | List workers |
| `PUT` | `/api/workers/{id}/config` | Admin | Update worker config |
| `DELETE` | `/api/workers/{id}` | Admin | Unregister worker |
| `POST` | `/api/workers/register` | RegistrationToken / Admin | Worker self-registration |
| `POST` | `/api/workers/login` | anonymous | Worker login |
| `POST` | `/api/workers/{id}/heartbeat` | Worker | Liveness ping |
| `POST` | `/api/queue/next` | Worker | Atomic job claim |

---

## Development

### From Source

```bash
# Coordinator
cd src/DotnetDeployer.Fleet.Coordinator
dotnet run

# Worker (separate terminal)
cd src/DotnetDeployer.Fleet.Worker
export Worker__CoordinatorBaseUrl=http://localhost:5000
export Worker__RegistrationToken=change-me
dotnet run

# Desktop App
cd src/DotnetDeployer.Fleet.Desktop
dotnet run
```

### One-Liner (everything at once)

```bash
./scripts/run-fleet.sh              # 1 coordinator + 1 worker + Desktop App
WORKERS=3 ./scripts/run-fleet.sh    # 1 coordinator + 3 workers + Desktop App
NO_GUI=1 ./scripts/run-fleet.sh     # headless (no Desktop App)
```

---

## Running Tests

```bash
dotnet test DotnetDeployer.Fleet.slnx
```

---

## Credits

App icon: <a href="https://www.flaticon.com/free-icons/expand" title="expand icons">Expand icons created by deemakdaksina - Flaticon</a>
