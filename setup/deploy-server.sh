#!/usr/bin/env bash
# ============================================================
# deploy-server.sh  (macOS / Linux)
# Deploys Rhino.Compute to a Linux server over SSH. The server runs the image
# built from the pushed branch, your local packages.json, fonts and plugin
# builds, with its own settings from setup/.env.server.
#
# Usage:
#   ./deploy-server.sh            # sync, rebuild, recreate the container, verify
#   ./deploy-server.sh status     # docker-status.sh on the server
#   ./deploy-server.sh logs       # follow the container log
#   ./deploy-server.sh ssh        # shell on the server
#
# setup/.env.server (gitignored; template .env.server.example):
#   DEPLOY_HOST    - ssh destination, e.g. deploy@203.0.113.10
#   DEPLOY_DIR     - checkout on the server, relative to its home (default: compute.rhino3d)
#   DEPLOY_PLUGINS - comma-separated local plugin build folders to ship
#                    (default: LOCAL_PLUGINS from setup/.env)
#   Every other line becomes the server's setup/.env (RHINO_TOKEN, IDLE_SPAN, ...).
#
# YES=1 skips the question asked when a plugin comes from a dirty tree or a
# branch other than main.
# ============================================================

set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
SERVER_ENV="$SCRIPT_DIR/.env.server"

log()  { echo ""; echo "==> $1"; }
ok()   { echo "    ✓  $1"; }
die()  { echo ""; echo "ERROR: $1" >&2; exit 1; }

[ -f "$SERVER_ENV" ] || die "setup/.env.server not found — cp .env.server.example .env.server and fill it in."

env_value() { # env_value FILE NAME → last NAME= value, surrounding quotes stripped
    [ -f "$1" ] || return 0
    sed -n "s/^[[:space:]]*$2=//p" "$1" | tail -1 | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'$/\1/"
}

DEPLOY_HOST="$(env_value "$SERVER_ENV" DEPLOY_HOST)"
DEPLOY_DIR="$(env_value "$SERVER_ENV" DEPLOY_DIR)"; DEPLOY_DIR="${DEPLOY_DIR:-compute.rhino3d}"
DEPLOY_PLUGINS="$(env_value "$SERVER_ENV" DEPLOY_PLUGINS)"
[ -n "$DEPLOY_PLUGINS" ] || DEPLOY_PLUGINS="$(env_value "$SCRIPT_DIR/.env" LOCAL_PLUGINS)"
CONTAINER_NAME="$(env_value "$SERVER_ENV" CONTAINER_NAME)"; CONTAINER_NAME="${CONTAINER_NAME:-rhino-compute-x9}"
REPO_URL="$(env_value "$SERVER_ENV" REPO_URL)"
REPO_URL="${REPO_URL:-$(sed -n 's/^ARG REPO_URL=//p' "$SCRIPT_DIR/Dockerfile")}"
BRANCH="$(env_value "$SERVER_ENV" BRANCH)"
BRANCH="${BRANCH:-$(sed -n 's/^ARG BRANCH=//p' "$SCRIPT_DIR/Dockerfile")}"

[ -n "$DEPLOY_HOST" ] || die "DEPLOY_HOST is not set in setup/.env.server."

ssh_() { ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new "$DEPLOY_HOST" "$@"; }

case "${1:-deploy}" in
    status) ssh_ "cd $DEPLOY_DIR/setup && ./docker-status.sh"; exit ;;
    logs)   ssh -t "$DEPLOY_HOST" "docker logs -f --tail 100 $CONTAINER_NAME"; exit ;;
    ssh)    ssh -t "$DEPLOY_HOST"; exit ;;
    deploy) ;;
    *)      die "unknown command '$1' (deploy | status | logs | ssh)" ;;
esac

# -------------------------------------------------------
# The server builds from GitHub, so what it runs is what is pushed.
# -------------------------------------------------------
log "Checking that $BRANCH is pushed"
[ -z "$(git -C "$REPO_DIR" status --porcelain -- setup src)" ] \
    || die "uncommitted changes under setup/ or src/. The server builds from GitHub: commit and push first."
git -C "$REPO_DIR" fetch -q origin "$BRANCH"
local_head="$(git -C "$REPO_DIR" rev-parse HEAD)"
remote_head="$(git -C "$REPO_DIR" rev-parse FETCH_HEAD)"
[ "$local_head" = "$remote_head" ] \
    || die "HEAD (${local_head:0:7}) is not origin/$BRANCH (${remote_head:0:7}). Push, or check out $BRANCH."
ok "$BRANCH at ${remote_head:0:7}"

# -------------------------------------------------------
# Plugins. Each folder is shipped under the name docker-launch.sh gives its
# mount (packages.json "local" entries refer to it), so it has to match
# docker-launch.sh's naming: the folder, walking up past build-output names.
# -------------------------------------------------------
mount_name() {
    local walk="$1" name
    name="$(basename "$walk")"
    while true; do
        case "$name" in
            net*|Release|Debug|bin|obj|Build)
                walk="$(dirname "$walk")"
                [ "$walk" = "/" ] && break
                name="$(basename "$walk")" ;;
            *) break ;;
        esac
    done
    echo "$name"
}

log "Plugins"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
plugin_names=()
suspect=0
manifest="deployed $(date -u +%Y-%m-%dT%H:%MZ) from $(hostname -s), compute $BRANCH ${remote_head:0:7}"$'\n'
IFS=',' read -ra _plugin_dirs <<< "$DEPLOY_PLUGINS"
for p in "${_plugin_dirs[@]}"; do
    p="$(echo "$p" | xargs)"
    [ -z "$p" ] && continue
    [ -d "$p" ] || die "plugin folder not found: $p"
    name="$(mount_name "$p")"
    if git -C "$p" rev-parse --git-dir >/dev/null 2>&1; then
        repo="$(basename "$(git -C "$p" rev-parse --show-toplevel)")"
        branch="$(git -C "$p" rev-parse --abbrev-ref HEAD)"
        state="$repo@$branch $(git -C "$p" rev-parse --short HEAD)"
        if [ -n "$(git -C "$p" status --porcelain --untracked-files=no)" ]; then
            state="$state, uncommitted changes"; suspect=1
        fi
        case "$branch" in main|master) ;; *) suspect=1 ;; esac
    else
        state="not in a git repo"
    fi
    echo "    $name  ←  $state"
    manifest+="$name  $state"$'\n'
    ln -s "$p" "$STAGE/$name"
    plugin_names+=("$name")
done
[ ${#plugin_names[@]} -gt 0 ] || echo "    (none — set DEPLOY_PLUGINS or LOCAL_PLUGINS)"
echo "    A build folder holds whatever was last built there; rebuild from the commit shown before deploying."
if [ "$suspect" = "1" ] && [ "$YES" != "1" ]; then
    read -r -p "    A plugin is not from a clean main. Deploy anyway? [y/N] " answer
    [ "$answer" = "y" ] || [ "$answer" = "Y" ] || die "aborted."
fi
printf '%s' "$manifest" > "$STAGE/DEPLOYED"

# -------------------------------------------------------
# Server checkout + files
# -------------------------------------------------------
log "Preparing $DEPLOY_HOST"
ssh_ "cloud-init status --wait >/dev/null 2>&1 || true; command -v docker >/dev/null" \
    || die "docker is not installed on the server (cloud-init failed? see /var/log/cloud-init-output.log)."
ssh_ "set -e
    [ -d $DEPLOY_DIR/.git ] || git clone -q $REPO_URL $DEPLOY_DIR
    cd $DEPLOY_DIR
    git fetch -q origin $BRANCH
    git checkout -q -f -B $BRANCH FETCH_HEAD"
ok "checkout at ${remote_head:0:7}"

# -L ships each staged symlink's target; --delete drops plugins no longer deployed.
rsync -aL --delete "$STAGE/" "$DEPLOY_HOST:plugins/"
[ -f "$SCRIPT_DIR/packages.json" ] && rsync -a "$SCRIPT_DIR/packages.json" "$DEPLOY_HOST:$DEPLOY_DIR/setup/"
rsync -a --delete --exclude README.md "$SCRIPT_DIR/plugins/" "$DEPLOY_HOST:$DEPLOY_DIR/setup/plugins/"
mkdir -p "$SCRIPT_DIR/fonts"
rsync -a --delete "$SCRIPT_DIR/fonts/" "$DEPLOY_HOST:$DEPLOY_DIR/setup/fonts/"
ok "plugins, packages.json and fonts synced"

server_home="$(ssh_ 'echo $HOME')"
server_plugins=""
for name in "${plugin_names[@]}"; do server_plugins+="$server_home/plugins/$name,"; done
{
    grep -vE '^[[:space:]]*(DEPLOY_[A-Z_]*|LOCAL_PLUGINS)=' "$SERVER_ENV" || true
    echo "LOCAL_PLUGINS=${server_plugins%,}"
} | ssh_ "umask 077; cat > $DEPLOY_DIR/setup/.env"
ok "setup/.env written"

# -------------------------------------------------------
# Build + recreate. docker-launch.sh fails on a crash-looping container but only
# warns when the server is slow to answer, so health is checked again below.
# -------------------------------------------------------
log "Building and starting on the server"
ssh_ "cd $DEPLOY_DIR/setup && ./docker-launch.sh"

log "Verifying"
ssh_ "cd $DEPLOY_DIR/setup && set -a && . ./.env && set +a
    hdr=(); [ -n \"\$RHINO_COMPUTE_KEY\" ] && hdr=(-H \"RhinoComputeKey: \$RHINO_COMPUTE_KEY\")
    for i in \$(seq 1 60); do
        [ \"\$(curl -s -o /dev/null -w '%{http_code}' \"\${hdr[@]}\" http://localhost:\${PORT:-6500}/healthcheck)\" = 200 ] && exit 0
        sleep 5
    done
    exit 1" || die "no healthy answer after 5 minutes. ./deploy-server.sh logs"
ok "healthcheck answers"
ssh_ "cd $DEPLOY_DIR/setup && ./docker-status.sh"
ssh_ "docker image prune -f >/dev/null"

echo ""
echo "============================================================"
echo "  Deployed. Running: compute ${remote_head:0:7}, plugins:"
for name in "${plugin_names[@]}"; do echo "    $name"; done
echo "  Record on the server: ~/plugins/DEPLOYED"
echo "============================================================"
