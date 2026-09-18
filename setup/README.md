# Rhino.Compute (x9 Branch) - Docker Setup

Automated Docker setup for running Rhino.Compute from the x9 branch on Linux.

> Just need the commands? See [docs/quick-start-docker.md](../docs/quick-start-docker.md)
> for a no-prose cheat-sheet of the day-to-day workflow.

## Quick Start (automated)

Run the launch script for your shell — it checks for Docker, starts
OrbStack/Docker Desktop if it's not running, builds the image, and runs the
container:

**macOS / Linux:**

```bash
cd setup
RHINO_TOKEN=your-token-here ./docker-launch.sh
```

**Windows (PowerShell):**

```powershell
cd setup
$env:RHINO_TOKEN="your-token-here"; .\docker-launch.ps1
```

Both scripts build the same Linux image and pass the same mounts — only the
host-side scripting differs. On Windows this needs **Docker Desktop in Linux
container mode** (the default, backed by WSL2); the script stops with a clear
message if Docker is in Windows-container mode. Everything inside the
container — `start.sh`, plugin staging, the duplicate-`.gha` guard, native
library fixes, fonts — is identical on every host.

Or, to avoid typing the token every time, copy `.env.example` to `.env` and
fill in `RHINO_TOKEN` (and any other overrides). `setup/.env` is gitignored
and is loaded automatically by both scripts:

```bash
cd setup
cp .env.example .env
# edit .env and set RHINO_TOKEN
./docker-launch.sh            # or  .\docker-launch.ps1  on Windows
```

On Windows, write `LOCAL_PLUGINS` paths in `.env` the way Windows spells
them (`C:\Users\you\src\my-plugin\Build\net8.0`), comma-separated. Docker
Desktop translates them; the container side stays POSIX either way.

Values already set in your shell environment take priority over `.env`.

See the script header for env vars (`PORT`, `CHILD_COUNT`, `IMAGE_NAME`,
`REPO_URL`, `BRANCH`, `NO_BUILD`, `PLATFORM`). Re-running it rebuilds the
image and replaces the running container. It waits for `/healthcheck` to
respond before declaring success — you'll see a clear pass/fail, not just
"container started".

The image is always built and run as `linux/amd64`, also on Apple Silicon
(where it runs under emulation — expect slower solves). McNeel publishes the
`rhino3d` package for arm64 months behind amd64, so a native arm64 image
would quietly get an older Rhino. Override with `PLATFORM=linux/arm64` only
if you have checked that the arm64 package has what you need.

## Limiting CPU and Memory

Rhino.Compute has **no built-in core limit**. `--childcount` (our
`CHILD_COUNT`) only sets how many `compute.geometry` workers run, and each
worker is a full headless Rhino that uses many threads — so child count alone
does not cap cores. The cap belongs at the container level:

```bash
# in setup/.env, or inline
CPUS=4 CHILD_COUNT=4 MEMORY=8g ./docker-launch.sh   # cap CPU time
CPUSET=0-3 CHILD_COUNT=4 ./docker-launch.sh          # or pin specific cores
```

| Var      | Docker flag       | Meaning                                  |
| -------- | ----------------- | ---------------------------------------- |
| `CPUS`   | `--cpus`          | CPU-time cap; fractions allowed (`2.5`)  |
| `CPUSET` | `--cpuset-cpus`   | Pin to specific cores (`0-3`, `0,2,4`)   |
| `MEMORY` | `--memory`        | RAM cap (`8g`)                           |

Set one of `CPUS` or `CPUSET`, not both — the launcher warns and prefers
`CPUSET`. Keep `CHILD_COUNT` at or below the allowed core count; the launcher
warns when it isn't, since extra workers mostly add contention.

**Expect `nproc` inside the container to still report the host's full core
count.** Rhino sizes its internal thread pools from the machine's total
processor count, which ignores the cap, so each worker may create more
threads than the cores it is allowed. The kernel confines them, so CPU usage
is capped as intended — the cost is some scheduling overhead, meaning
slightly lower throughput than a physical machine of that size. Verify the
cap is real with:

```bash
docker exec <container> cat /sys/fs/cgroup/cpu.max   # "400000 100000" = 4 cores
```

On Windows, Docker Desktop's own VM has a global CPU/memory ceiling
(Settings → Resources) that applies on top of these per-container limits.

Outside Docker, the same capping is done with IIS app-pool processor affinity
(Windows) or `AllowedCPUs`/`CPUQuota` in a systemd drop-in (Linux) — the
children inherit the parent's limit in both cases.

## Checking Status

`docker start` / `docker restart` print almost nothing — the container can
be "running" while Rhino/Grasshopper are still booting inside it, or it can
crash-loop silently. Use `docker-status.sh` instead of guessing:

```bash
cd setup
./docker-status.sh
```

It reports: whether the daemon is up, whether the container is running,
whether `/healthcheck` actually responds, which Grasshopper plugins loaded
(the other thing that can silently fail — see
[docs/grasshopper-plugins-not-loading-linux.md](../docs/grasshopper-plugins-not-loading-linux.md)),
and the last few log lines either way.

## Quick Start (manual)

### 1. Build the image (one time)

Open PowerShell, **navigate to this `setup` folder**, and run:

```powershell
cd D:\Coding\compute.rhino3d\setup
docker build -t rhino-compute-x9 .
```

> ⚠️ **Important:** run the build from inside the `setup` folder.
> If you run it from the repo root, Docker picks up the old Windows-based
> Dockerfile there instead, and the build will fail.

Alternatively, from the repo root:

```powershell
docker build -t rhino-compute-x9 -f setup/Dockerfile setup
```

If the repo URL in the Dockerfile needs to be changed:

```powershell
docker build -t rhino-compute-x9 `
  --build-arg REPO_URL=https://your-repo-url.git `
  --build-arg BRANCH=x9 .
```

This takes a few minutes the first time. Everything is installed and built automatically.

### 2. Run it

**With a token (required for actual computations):**

```powershell
docker run -p 6500:6500 -e RHINO_TOKEN=your-token-here rhino-compute-x9
```

**Without a token (server starts but computations will fail):**

```powershell
docker run -p 6500:6500 rhino-compute-x9
```

The server listens on `http://localhost:6500` from your Windows machine.

### 3. Connect from your app or Grasshopper

- **From a local app (TypeScript, Python, etc.):** connect to `http://localhost:6500`
- **From Grasshopper/Hops:** set the server to `http://localhost:6500` and the API key to your RHINO_TOKEN value

## Important: File URLs Inside Docker

When your app sends a Grasshopper definition URL to the server, that URL must be
reachable **from inside the container**, not just from Windows.

| From Windows                       | From inside Docker                        |
| ---------------------------------- | ----------------------------------------- |
| `http://localhost:5500`            | Does NOT work (localhost = the container) |
| `http://127.0.0.1:5500`            | Does NOT work (same reason)               |
| `http://host.docker.internal:5500` | WORKS (resolves to your Windows host)     |

So if you are serving .gh files via Live Server on port 5500, use:

```
http://host.docker.internal:5500/path/to/your/definition.gh
```

## Ports

| Port | Purpose                   | Accessible from host?  |
| ---- | ------------------------- | ---------------------- |
| 6500 | Main rhino.compute server | Yes (via -p 6500:6500) |
| 6001 | Child compute.geometry    | No (internal only)     |

The main server on 6500 is bound to `0.0.0.0` so it is reachable from outside
the container. The child process on 6001 stays on localhost inside the container
and is only used internally by the main server.

## Plugin Manifest (packages.json)

`setup/packages.json` declares every plugin the server needs. Like `.env`,
your copy is gitignored (deployments differ in what they load); the tracked
template is `packages.example.json` — copy it once and edit:

```bash
cd setup
cp packages.example.json packages.json
```

`docker-launch.sh` warns if the file is missing, because the container then
starts with **no** plugins and every solve that needs one fails later.

```json
{
  "yak": [
    { "name": "elefront", "version": "5.4.1" },
    { "name": "selva", "version": "0.14.0.0" }
  ],
  "local": ["MyCustomPlugin.gha"]
}
```

- **`yak`** — installed from the Yak server on container start. `version` is
  optional; omit it to always get the latest **release**. Prerelease versions
  (`1.0.0-beta.13`) are only installed when pinned exactly — an unpinned
  entry resolves to the newest stable, which can be a downgrade from a beta
  you were running.
- **`local`** — file/folder names expected in `setup/plugins/` (see
  [Custom Plugins](#custom-plugins-not-on-yak) below). The container warns at
  startup if a declared file is missing.

The manifest is mounted into the container and processed by `start.sh` on
**every container start**. Already-installed packages are skipped, so
restarts stay fast. Because the manifest is applied on start, recreating the
container (`./docker-launch.sh`) automatically reinstalls everything — no
state to lose.

**To change a pinned version:** edit `packages.json`, then recreate the
container (`NO_BUILD=1 ./docker-launch.sh`). A plain `docker restart` skips
packages that are already installed, so it won't pick up version changes.

Check what got installed:

```bash
docker logs rhino-compute-x9 | head -40
docker exec rhino-compute-x9 yak list
```

## Installing Grasshopper Plugins (Yak) — ad hoc

> Prefer declaring packages in `packages.json` (section above) — it survives
> container recreation. Use `docker exec` only for quickly trying a plugin
> out before adding it to the manifest.

`yak-cli` is installed in the image (via the same McNeel apt repo used for
`rhino-compute`). Install plugins into the **running container**:

```bash
docker exec rhino-compute-x9 yak install <plugin-name>
```

(use the container name/ID from `docker ps` if you didn't use `docker-launch.sh`,
which names it `rhino-compute-x9` by default).

Then restart the container so compute.geometry picks up the new plugin:

```bash
docker restart rhino-compute-x9
```

List / remove plugins:

```bash
docker exec rhino-compute-x9 yak list
docker exec rhino-compute-x9 yak uninstall <plugin-name>
```

**Important:** plugins installed this way live inside the container's
writable layer, not the image. If you run `docker-launch.sh` again (or any
`docker rm` + `docker run`), the container is recreated from the image and
the plugins are gone. To make them permanent, either:

- `docker commit rhino-compute-x9 rhino-compute-x9-custom` and use that image
  tag for future runs (`IMAGE_NAME=rhino-compute-x9-custom NO_BUILD=1
  ./docker-launch.sh`), or
- re-run the `yak install` command(s) after each rebuild/relaunch.

Yak installs to `/root/.local/share/mcneel/rhinoceros/packages/9.0/` — the
container runs as root, so this is where the compute service looks for
plugins automatically.

## Custom Plugins (not on Yak)

Put your own packages in `setup/plugins/` — the folder is volume-mounted into
the container at `/plugins` (by `docker-launch.sh`) and loaded automatically
on every container start:

- `*.gha`, `*.dll`, and folders are copied into the Grasshopper Libraries
  folder (`/root/.config/Grasshopper/Libraries/`)
- `*.yak` archives are installed via `yak install`

To add or update a plugin:

```bash
cp /path/to/MyPlugin.gha setup/plugins/
docker restart rhino-compute-x9
```

Because the folder lives on the host, plugins **survive container
recreation** — unlike `docker exec ... yak install`, there is nothing to
redo after re-running `docker-launch.sh`.

### One source per plugin

A plugin comes from **either** `setup/plugins/` **or** a `LOCAL_PLUGINS`
mount — never both. `setup/plugins/` is gitignored, so a copy dropped there
months ago is invisible to `git status` and keeps being loaded long after
you have moved on to live-mounting the same plugin. The two copies have the
same assembly name, Grasshopper loads whichever it scans first, and every
log line looks healthy — until a component fails at solve time with a
`TypeLoadException` for a type that only the stale build referenced.

`start.sh` now defends against this: a live mount replaces any same-named
folder outright rather than merging with it, and the container **refuses to
start** if the same `.gha` is found twice under the Libraries folder. If you
see that error, delete the leftover under `setup/plugins/` and restart.

### Live-mounting a plugin you're developing

Instead of copying build output into `setup/plugins/` after every build, set
`LOCAL_PLUGINS` in `setup/.env` to the build folder(s), comma-separated:

```bash
LOCAL_PLUGINS=/Users/you/coding/my-plugin/Build/MyPlugin.Grasshopper/net7.0
```

`docker-launch.sh` mounts each folder read-only under `/plugins-local/local-<i>-<name>`
(using the parent folder's name when the target is a generic `net7.0`/`bin`/
`Release` folder). The dev loop is then:

```bash
dotnet build            # rebuild your plugin on the host
docker restart rhino-compute-x9   # re-copies plugins into GH Libraries
```

Optionally declare the mount name under `"local"` in `packages.json` so the
container warns if the mount is missing.

> Note on native libraries: a plugin built on Windows or an Apple Silicon Mac
> ships a Windows `.dll` or an arm64 `.so` next to its `.gha`, neither of
> which loads in the amd64 container. As long as the plugin also carries the
> NuGet `runtimes/<rid>/native/` tree (SkiaSharp, HarfBuzzSharp and most
> native-asset packages do), `start.sh` installs the `linux-x64` build on
> every start and nothing needs changing in the plugin. A plugin that
> P/Invokes a Windows-only library with no Linux build will still fail.

## Useful Commands

**Stop the server:**

Press `Ctrl+C` in the terminal where it is running, or from another PowerShell:

```powershell
docker ps                        # find the container ID
docker stop <container_id>
```

**Open a shell inside a running container:**

```powershell
docker ps                        # find the container ID
docker exec -it $(docker ps -q --filter ancestor=rhino-compute-x9) /bin/bash
```

**View logs of a running container:**

```powershell
docker logs -f <container_id>
```

**Rebuild after Dockerfile changes:**

```powershell
docker build -t rhino-compute-x9 .
```

**Save container state (if you made changes inside and want to keep them):**

```powershell
docker ps                        # find the container ID
docker commit <container_id> rhino-compute-x9-custom
```

## Development Workflow

If you want to edit the source code on Windows and build inside the container,
mount a local clone as a volume.

### Step 1: Clone on Windows

```powershell
cd C:\Projects
git clone <REPO_URL> rhino-compute-src
cd rhino-compute-src
git checkout x9
```

### Step 2: Run with volume mount

```powershell
docker run -it -p 6500:6500 `
  -e RHINO_TOKEN=your-token-here `
  -v "C:\Projects\rhino-compute-src:/home/rhino-compute-src" `
  rhino-compute-x9 /bin/bash
```

### Step 3: Build and run inside the container

```bash
cd /home/rhino-compute-src/src
dotnet build compute.sln -c Release
dotnet run --project rhino.compute --configuration Release --no-build -- --urls http://0.0.0.0:6500 --childcount 1 --spawn-on-startup
```

Edit files in VS Code on Windows, build and run inside the container. Changes
are reflected immediately because the volume mount keeps them in sync.

## Troubleshooting

**Solves fail with PayAttentionException / plugins missing from `/plugins/gh/installed`:**
See [docs/grasshopper-plugins-not-loading-linux.md](../docs/grasshopper-plugins-not-loading-linux.md)
— a full write-up of why Grasshopper plugins can silently fail to load on
Linux and how this repo fixes it.

**Container exits at start with "the same Grasshopper assembly is present more than once":**
A plugin exists both in `setup/plugins/` and as a `LOCAL_PLUGINS` mount (or
twice in `setup/plugins/`). Keep one — see [One source per plugin](#one-source-per-plugin).

**A component fails at solve time with `TypeLoadException: Could not load type '...' from assembly '...'` although the plugin reported a clean load:**
The running `.gha` is not the one you built. Almost always a stale copy —
same cause as above; check `docker exec <container> find /root/.config/Grasshopper/Libraries -name '*.gha'`
and compare hashes with your build output. Note that .NET metadata stores
namespace and type name separately, so grepping binaries for the full type
name from the error message finds nothing even when the reference is there.

**"Connection refused" from Windows:**
The server might be listening on localhost inside the container instead of
0.0.0.0. Make sure you are using the start.sh script or passing
`--urls http://0.0.0.0:6500` when running manually.

**Computations fail with PAL_SEHException:**
Most likely the RHINO_TOKEN is not set. Restart with
`-e RHINO_TOKEN=your-token-here`.

**Build fails with NuGet errors:**
The Dockerfile already creates a clean NuGet.Config. If you still get errors,
try `dotnet restore compute.sln` before building.

**Port already in use:**
Change the host port: `-p 7000:6500` and connect to `http://localhost:7000`.

**Git Bash mangles paths:**
Use PowerShell instead, or prefix paths with double slashes in Git Bash
(e.g. `//bin/bash` instead of `/bin/bash`).

**Container disappears after exit:**
If you used `docker run` without removing `--rm` from the command, the container
is deleted on exit. Either drop `--rm` or use `docker commit` to save the state
before exiting.

**"host.docker.internal" not resolving:**
This hostname is specific to Docker Desktop. If you are using Docker Engine on
bare Linux, use the host machine's actual IP address instead.
