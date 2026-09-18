#!/usr/bin/env bash
# ============================================================
# docker-launch.sh  (macOS / Linux)
# Builds and runs the Rhino.Compute (x9 branch) Docker image.
#
# Usage:
#   ./docker-launch.sh
#   RHINO_TOKEN=your-token ./docker-launch.sh
#   RHINO_TOKEN=your-token PORT=7000 ./docker-launch.sh
#
# Env vars:
#   RHINO_TOKEN   - Rhino Core-Hour Billing token (optional but needed for real work)
#   IMAGE_NAME    - Docker image tag (default: rhino-compute-x9)
#   CONTAINER_NAME- Container name (default: rhino-compute-x9)
#   PORT          - Host port to bind (default: 6500)
#   CHILD_COUNT   - Number of compute.geometry children (default: 1)
#   REPO_URL      - Repo to clone inside the image (default: Dockerfile's ARG default)
#   BRANCH        - Branch to check out inside the image (default: Dockerfile's ARG default)
#   NO_BUILD      - Skip the image build step if set to "1" (reuse existing image)
#   PLATFORM      - Docker platform to build and run for (default: linux/amd64).
#                   Kept explicit because McNeel's arm64 Rhino packages trail the
#                   amd64 ones by months; on Apple Silicon the default builds
#                   under emulation rather than picking up an older Rhino.
#   CPUS          - CPU-time cap, e.g. CPUS=4 (docker --cpus). Fractions allowed.
#   CPUSET        - Pin to specific cores instead, e.g. CPUSET=0-3 (--cpuset-cpus).
#                   Use one or the other; CPUSET also controls WHICH cores.
#   MEMORY        - RAM cap, e.g. MEMORY=8g (docker --memory).
#
# Rhino.Compute has no core limit of its own: --childcount only sets how many
# compute.geometry workers run, and each worker is a headless Rhino that uses
# many threads. Cap cores at the container level (CPUS/CPUSET) and keep
# CHILD_COUNT <= that number. Note Rhino sizes its thread pools from the
# machine's total processor count, ignoring the cap, so each worker may still
# spawn more threads than cores allowed — the kernel confines them, at a small
# scheduling cost.
#   LOCAL_PLUGINS - Comma-separated host folders to mount as live plugins,
#                   e.g. LOCAL_PLUGINS=/path/to/MyPlugin/bin/net7.0
#                   (rebuild plugin + `docker restart` to pick up changes)
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Load setup/.env if present (RHINO_TOKEN=... etc). Env vars already set in
# the shell win — .env only fills in what isn't already set.
if [ -f "$SCRIPT_DIR/.env" ]; then
    for var in RHINO_TOKEN RHINO_COMPUTE_KEY IMAGE_NAME CONTAINER_NAME PORT CHILD_COUNT REPO_URL BRANCH NO_BUILD LOCAL_PLUGINS PLATFORM CPUS CPUSET MEMORY; do
        eval "_prior_${var}=\"\${${var}-}\""
    done

    set -a
    # shellcheck disable=SC1091
    source "$SCRIPT_DIR/.env"
    set +a

    for var in RHINO_TOKEN RHINO_COMPUTE_KEY IMAGE_NAME CONTAINER_NAME PORT CHILD_COUNT REPO_URL BRANCH NO_BUILD LOCAL_PLUGINS PLATFORM CPUS CPUSET MEMORY; do
        eval "if [ -n \"\${_prior_${var}-}\" ]; then ${var}=\"\${_prior_${var}}\"; fi"
    done
fi

IMAGE_NAME="${IMAGE_NAME:-rhino-compute-x9}"
CONTAINER_NAME="${CONTAINER_NAME:-rhino-compute-x9}"
PORT="${PORT:-6500}"
# Always amd64, even on Apple Silicon: the McNeel apt repo publishes rhino3d
# for arm64 months behind amd64, so a native arm64 build silently gets an
# older Rhino (and, at the time of writing, none of the Linux font fixes).
PLATFORM="${PLATFORM:-linux/amd64}"
CHILD_COUNT="${CHILD_COUNT:-1}"

log() { echo ""; echo "==> $1"; }
ok()  { echo "    ✓  $1"; }

# -------------------------------------------------------
# Check Docker CLI is installed
# -------------------------------------------------------
if ! command -v docker &>/dev/null; then
    echo ""
    echo "ERROR: docker not found."
    echo "Install Docker Desktop:  https://www.docker.com/products/docker-desktop/"
    echo "Or OrbStack:             https://orbstack.dev"
    exit 1
fi

# -------------------------------------------------------
# Make sure the Docker daemon is actually running
# (start OrbStack / Docker Desktop if we can find one)
# -------------------------------------------------------
if ! docker info &>/dev/null; then
    log "Docker daemon not running — attempting to start it"

    if [ -d "/Applications/OrbStack.app" ]; then
        open -a OrbStack
    elif [ -d "/Applications/Docker.app" ]; then
        open -a Docker
    else
        echo ""
        echo "ERROR: Docker daemon is not running and no known Docker app"
        echo "(OrbStack or Docker Desktop) was found in /Applications."
        echo "Start your Docker runtime manually and re-run this script."
        exit 1
    fi

    printf "    waiting for daemon"
    for _ in $(seq 1 60); do
        if docker info &>/dev/null; then
            echo ""
            ok "Docker daemon is up"
            break
        fi
        printf "."
        sleep 1
    done

    if ! docker info &>/dev/null; then
        echo ""
        echo "ERROR: Docker daemon still not responding after 60s."
        echo "Open your Docker app manually and re-run this script."
        exit 1
    fi
else
    ok "Docker daemon is running"
fi

echo ""
echo "============================================================"
echo "  Rhino.Compute — Docker Setup"
echo "  Image      : $IMAGE_NAME"
echo "  Container  : $CONTAINER_NAME"
echo "  Port       : $PORT -> 6500"
echo "  Children   : $CHILD_COUNT"
echo "  CPU limit  : $([ -n "$CPUSET" ] && echo "cores $CPUSET (pinned)" || { [ -n "$CPUS" ] && echo "$CPUS CPUs" || echo "none (all host cores)"; })"
[ -n "$MEMORY" ] && echo "  Memory     : $MEMORY"
echo "  Token      : $([ -n "$RHINO_TOKEN" ] && echo "set" || echo "NOT SET (computations will fail)")"
echo "============================================================"
echo ""

# -------------------------------------------------------
# Resource limits. Rhino.Compute itself cannot cap cores — --childcount only
# sets how many compute.geometry workers run, and each is a headless Rhino
# using many threads. The cap belongs at the container level; CHILD_COUNT must
# then stay at or below the allowed core count, or workers contend for cores.
# -------------------------------------------------------
LIMIT_ARGS=()
if [ -n "$CPUS" ] && [ -n "$CPUSET" ]; then
    echo "    WARNING: CPUS and CPUSET are both set — using CPUSET ($CPUSET) and"
    echo "             ignoring CPUS. CPUSET pins which cores; CPUS caps CPU time."
    CPUS=""
fi
[ -n "$CPUS" ]   && LIMIT_ARGS+=(--cpus "$CPUS")
[ -n "$CPUSET" ] && LIMIT_ARGS+=(--cpuset-cpus "$CPUSET")
[ -n "$MEMORY" ] && LIMIT_ARGS+=(--memory "$MEMORY")

# Count the cores the cap actually allows, so we can sanity-check CHILD_COUNT.
_allowed=""
if [ -n "$CPUSET" ]; then
    # "0-3", "0,2,4" or a mix of both
    _allowed=0
    IFS=',' read -ra _parts <<< "$CPUSET"
    for _p in "${_parts[@]}"; do
        case "$_p" in
            *-*) _allowed=$((_allowed + ${_p#*-} - ${_p%-*} + 1)) ;;
            "")  ;;
            *)   _allowed=$((_allowed + 1)) ;;
        esac
    done
elif [ -n "$CPUS" ]; then
    _allowed="${CPUS%%.*}"   # 3.5 CPUs -> treat as 3 whole workers
fi
if [ -n "$_allowed" ] && [ "$_allowed" -gt 0 ] 2>/dev/null && [ "$CHILD_COUNT" -gt "$_allowed" ] 2>/dev/null; then
    echo "    WARNING: CHILD_COUNT=$CHILD_COUNT exceeds the $_allowed core(s) this container"
    echo "             may use. Each child is a full headless Rhino — running more"
    echo "             workers than cores mostly adds contention. Consider CHILD_COUNT=$_allowed."
    echo ""
fi

# -------------------------------------------------------
# Build the image
# -------------------------------------------------------
if [ "$NO_BUILD" = "1" ]; then
    log "Skipping build (NO_BUILD=1) — reusing existing image"
else
    log "Building image '$IMAGE_NAME' (this takes a few minutes the first time...)"

    BUILD_ARGS=()
    [ -n "$REPO_URL" ] && BUILD_ARGS+=(--build-arg "REPO_URL=$REPO_URL")
    [ -n "$BRANCH" ] && BUILD_ARGS+=(--build-arg "BRANCH=$BRANCH")
    # FRESH=1 busts the docker layer cache — needed to pick up new commits,
    # since the git clone happens in a cached build layer
    [ "$FRESH" = "1" ] && BUILD_ARGS+=(--no-cache)

    docker build --platform "$PLATFORM" -t "$IMAGE_NAME" "${BUILD_ARGS[@]}" "$SCRIPT_DIR"
    ok "Image built"
fi

# -------------------------------------------------------
# Stop/remove any existing container with the same name
# -------------------------------------------------------
if docker ps -a --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}\$"; then
    log "Removing existing container '$CONTAINER_NAME'"
    docker rm -f "$CONTAINER_NAME" &>/dev/null
    ok "Old container removed"
fi

# -------------------------------------------------------
# Run the container
# (setup/plugins is mounted read-only; start.sh loads any
#  .gha/.dll/.yak found there on every container start.
#  setup/packages.json declares yak packages to install.)
# -------------------------------------------------------
log "Starting container '$CONTAINER_NAME'"

mkdir -p "$SCRIPT_DIR/plugins"

MOUNT_ARGS=(-v "$SCRIPT_DIR/plugins:/plugins:ro")
if [ -f "$SCRIPT_DIR/packages.json" ]; then
    MOUNT_ARGS+=(-v "$SCRIPT_DIR/packages.json:/packages.json:ro")
else
    # packages.json is gitignored (per-deployment, like .env). Without it the
    # container starts cleanly with NO yak packages and no local-plugin
    # checks, and the failure only shows up later as solves missing components.
    echo "    WARNING: setup/packages.json not found — no yak packages will be installed."
    echo "             cp packages.example.json packages.json  (then edit) to fix."
fi

# Custom fonts (setup/fonts) — needed by text-to-curve components; the container
# ships only Liberation/DejaVu. start.sh runs fc-cache on start.
mkdir -p "$SCRIPT_DIR/fonts"
MOUNT_ARGS+=(-v "$SCRIPT_DIR/fonts:/usr/local/share/fonts/custom:ro")

# Extra live-mounted plugin folders (LOCAL_PLUGINS, comma-separated host paths).
# Mounted under /plugins-local (NOT inside /plugins — that mount is read-only,
# so Docker cannot create nested mountpoints in it). start.sh scans both.
# Mounted read-only; rebuild + `docker restart` to update.
if [ -n "$LOCAL_PLUGINS" ]; then
    idx=0
    IFS=',' read -ra _plugin_dirs <<< "$LOCAL_PLUGINS"
    for p in "${_plugin_dirs[@]}"; do
        p="$(echo "$p" | xargs)"   # trim whitespace
        [ -z "$p" ] && continue
        if [ ! -d "$p" ]; then
            echo "    WARNING: LOCAL_PLUGINS folder not found, skipping: $p"
            continue
        fi
        # Name the mount after the folder, walking up past generic
        # build-output names like net7.0/Release/Debug/bin/Build.
        _walk="$p"
        name="$(basename "$_walk")"
        while true; do
            case "$name" in
                net*|Release|Debug|bin|obj|Build)
                    _walk="$(dirname "$_walk")"
                    [ "$_walk" = "/" ] && break
                    name="$(basename "$_walk")"
                    ;;
                *) break ;;
            esac
        done
        MOUNT_ARGS+=(-v "$p:/plugins-local/local-$idx-$name:ro")
        ok "Live plugin mount: $p -> /plugins-local/local-$idx-$name"
        idx=$((idx + 1))
    done
fi

# Require the RhinoComputeKey header when RHINO_COMPUTE_KEY is set
# (leave unset for an unauthenticated local server)
ENV_ARGS=()
[ -n "$RHINO_COMPUTE_KEY" ] && ENV_ARGS+=(-e "RHINO_COMPUTE_KEY=$RHINO_COMPUTE_KEY")

docker run -d \
    --platform "$PLATFORM" \
    --name "$CONTAINER_NAME" \
    -p "${PORT}:6500" \
    -e RHINO_TOKEN="$RHINO_TOKEN" \
    -e RHINO_COMPUTE_CHILD_COUNT="$CHILD_COUNT" \
    "${ENV_ARGS[@]}" \
    "${LIMIT_ARGS[@]}" \
    "${MOUNT_ARGS[@]}" \
    "$IMAGE_NAME" >/dev/null

ok "Container started"

# -------------------------------------------------------
# Wait for the server to actually respond before declaring victory.
# Building + booting Rhino/Grasshopper inside the container can take
# a while — a bare "container started" message is not the same as
# "the server is ready", so poll until it answers or clearly failed.
# -------------------------------------------------------
log "Waiting for Rhino.Compute to become ready..."

HEALTH_HEADER=()
[ -n "$RHINO_COMPUTE_KEY" ] && HEALTH_HEADER=(-H "RhinoComputeKey: $RHINO_COMPUTE_KEY")

ready=0
for i in $(seq 1 90); do
    if ! docker ps --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}\$"; then
        echo ""
        echo "ERROR: container '$CONTAINER_NAME' exited unexpectedly during startup."
        echo "Last log lines:"
        docker logs --tail 30 "$CONTAINER_NAME" 2>&1 | sed 's/^/    /'
        exit 1
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' "${HEALTH_HEADER[@]}" "http://localhost:${PORT}/healthcheck" 2>/dev/null || true)"
    if [ "$code" = "200" ]; then
        ready=1
        break
    fi
    printf "."
    sleep 2
done
echo ""

if [ "$ready" = "1" ]; then
    ok "Rhino.Compute responded to /healthcheck (took ~$((i * 2))s)"
else
    echo ""
    echo "WARNING: server did not respond within ~180s. It may still be"
    echo "starting (first boot after a rebuild can be slow) — check:"
    echo "    docker logs -f $CONTAINER_NAME"
fi

echo ""
echo "============================================================"
if [ "$ready" = "1" ]; then
    echo "  Done! Rhino.Compute is up and responding."
else
    echo "  Container started, but not confirmed ready yet — see above."
fi
echo "============================================================"
echo ""
echo "  Connect from your Mac:"
echo "    http://localhost:${PORT}"
echo ""
echo "  Healthcheck:"
echo "    curl http://localhost:${PORT}/healthcheck"
echo ""
echo "  Check status any time:"
echo "    ./docker-status.sh"
echo ""
echo "  View logs:"
echo "    docker logs -f $CONTAINER_NAME"
echo ""
echo "  Stop the server:"
echo "    docker stop $CONTAINER_NAME"
echo ""
