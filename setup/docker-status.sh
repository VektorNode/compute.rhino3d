#!/usr/bin/env bash
# ============================================================
# docker-status.sh  (macOS / Linux)
# Reports what's actually happening with the Rhino.Compute container —
# useful after `docker start`/`docker restart`, which print almost nothing.
#
# Usage:
#   ./docker-status.sh
#   CONTAINER_NAME=my-container ./docker-status.sh
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ -f "$SCRIPT_DIR/.env" ]; then
    set -a
    # shellcheck disable=SC1091
    source "$SCRIPT_DIR/.env"
    set +a
fi

CONTAINER_NAME="${CONTAINER_NAME:-rhino-compute-x9}"
PORT="${PORT:-6500}"

log() { echo ""; echo "==> $1"; }
ok()  { echo "    ✓  $1"; }
bad() { echo "    ✗  $1"; }

if ! command -v docker &>/dev/null; then
    echo "ERROR: docker not found."
    exit 1
fi

if ! docker info &>/dev/null; then
    bad "Docker daemon is not running"
    echo "    Start OrbStack / Docker Desktop, then re-run this script."
    exit 1
fi
ok "Docker daemon is running"

if ! docker ps -a --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}\$"; then
    bad "No container named '$CONTAINER_NAME' exists"
    echo "    Run ./docker-launch.sh to create and start it."
    exit 1
fi

STATUS="$(docker inspect -f '{{.State.Status}}' "$CONTAINER_NAME")"
STARTED_AT="$(docker inspect -f '{{.State.StartedAt}}' "$CONTAINER_NAME")"

log "Container '$CONTAINER_NAME'"
echo "    Status     : $STATUS"
echo "    Started at : $STARTED_AT"

if [ "$STATUS" != "running" ]; then
    bad "Container is not running (status: $STATUS)"
    echo ""
    echo "  Start it with:"
    echo "    docker start $CONTAINER_NAME   (then re-run this script)"
    echo "  Or recreate it from scratch:"
    echo "    ./docker-launch.sh"
    echo ""
    echo "  Last log lines:"
    docker logs --tail 20 "$CONTAINER_NAME" 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | sed 's/^/    /'
    exit 1
fi
ok "Container is running"

# -------------------------------------------------------
# Poll the healthcheck endpoint — this is the real signal,
# since "running" only means the process hasn't exited yet.
# -------------------------------------------------------
HEALTH_HEADER=()
[ -n "$RHINO_COMPUTE_KEY" ] && HEALTH_HEADER=(-H "RhinoComputeKey: $RHINO_COMPUTE_KEY")

log "Checking http://localhost:${PORT}/healthcheck"

code="$(curl -s -o /dev/null -w '%{http_code}' "${HEALTH_HEADER[@]}" "http://localhost:${PORT}/healthcheck" 2>/dev/null || true)"

case "$code" in
    200)
        ok "Server is up and healthy"
        ;;
    401)
        bad "Server responded 401 Unauthorized"
        echo "    RHINO_COMPUTE_KEY is set on the server but missing/wrong here."
        echo "    Set RHINO_COMPUTE_KEY in setup/.env to match, or check your client."
        ;;
    "")
        bad "No response — server may still be starting up"
        echo "    Rhino/Grasshopper can take a while to initialize after a (re)start."
        echo "    Watch progress with:"
        echo "        docker logs -f $CONTAINER_NAME"
        ;;
    *)
        bad "Server responded with HTTP $code"
        echo "    Check logs for details:"
        echo "        docker logs -f $CONTAINER_NAME"
        ;;
esac

# -------------------------------------------------------
# Memory. A child whose cache has spilled into swap makes every solve slow
# while the healthcheck stays green.
# -------------------------------------------------------
log "Memory"
mem_limit="$(docker inspect -f '{{.HostConfig.Memory}}' "$CONTAINER_NAME")"
mem_swap="$(docker inspect -f '{{.HostConfig.MemorySwap}}' "$CONTAINER_NAME")"
if [ "$mem_limit" = "0" ]; then
    bad "No memory cap — the solve-result caches can grow into swap (set MEMORY, see setup/README.md)"
elif [ "$mem_swap" = "$mem_limit" ]; then
    ok "Memory cap $((mem_limit / 1024 / 1024))m, no swap"
else
    bad "Memory cap $((mem_limit / 1024 / 1024))m, but swap is allowed — recreate with ./docker-launch.sh"
fi
restarts="$(docker inspect -f '{{.RestartCount}}' "$CONTAINER_NAME")"
[ "$restarts" -gt 0 ] && bad "Restarted $restarts time(s) since it was created — check docker logs"
docker exec "$CONTAINER_NAME" sh -c '
    for pid in $(pgrep -f "[c]ompute\.geometry -port:"); do
        port=$(tr "\0" " " < /proc/$pid/cmdline | grep -o -- "-port:[0-9]*" | head -1 | cut -d: -f2)
        rss=$(awk "/^VmRSS/ {print int(\$2 / 1024)}" /proc/$pid/status)
        swap=$(awk "/^VmSwap/ {print int(\$2 / 1024)}" /proc/$pid/status)
        echo "    child $port: ${rss}m RAM, ${swap}m swap"
    done' 2>/dev/null || bad "Could not read the children's memory"

# -------------------------------------------------------
# Loaded Grasshopper plugins — the other thing that fails silently.
# -------------------------------------------------------
if [ "$code" = "200" ]; then
    log "Loaded Grasshopper plugins"
    plugins="$(curl -s "${HEALTH_HEADER[@]}" "http://localhost:${PORT}/plugins/gh/installed" 2>/dev/null || true)"
    if [ -z "$plugins" ] || [ "$plugins" = "{}" ]; then
        bad "No plugins reported — this usually means something went wrong loading them"
        echo "    See docs/grasshopper-plugins-not-loading-linux.md"
    else
        echo "$plugins" | tr ',' '\n' | sed 's/[{}"]//g' | sed 's/^/    /'
    fi
fi

echo ""
echo "  Recent logs (last 15 lines):"
docker logs --tail 15 "$CONTAINER_NAME" 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | sed 's/^/    /'
echo ""
