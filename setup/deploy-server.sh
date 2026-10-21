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
# The server: any Ubuntu or Debian host you can reach over SSH as a user with
# sudo (x86-64 for current Rhino builds). The first deploy runs server-init.sh
# there to install Docker.
#
# setup/.env.server (gitignored; template .env.server.example):
#   DEPLOY_HOST    - ssh destination, e.g. deploy@203.0.113.10
#   DEPLOY_DIR     - checkout on the server, relative to its home (default: compute.rhino3d)
#   DEPLOY_PLUGINS - comma-separated local plugin build folders to ship
#                    (default: LOCAL_PLUGINS from setup/.env)
#   DEPLOY_TLS_DOMAIN - serve https://<this name> through Caddy with a Let's Encrypt
#                    certificate. The name must resolve to the server and ports 80
#                    and 443 must be open to the internet. Compute then listens on
#                    localhost only. Unset removes the proxy.
#   DEPLOY_ALLOW_IPS - with DEPLOY_TLS_DOMAIN: comma-separated addresses or CIDR
#                    ranges allowed through the proxy; everyone else gets 403.
#   Every other line becomes the server's setup/.env (RHINO_TOKEN, IDLE_SPAN, ...).
#
# YES=1 skips the question asked when a plugin comes from a dirty tree or a
# branch other than its repo's default.
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
DEPLOY_TLS_DOMAIN="$(env_value "$SERVER_ENV" DEPLOY_TLS_DOMAIN)"
DEPLOY_ALLOW_IPS="$(env_value "$SERVER_ENV" DEPLOY_ALLOW_IPS)"
CONTAINER_NAME="$(env_value "$SERVER_ENV" CONTAINER_NAME)"; CONTAINER_NAME="${CONTAINER_NAME:-rhino-compute-x9}"
PORT="$(env_value "$SERVER_ENV" PORT)"; PORT="${PORT:-6500}"
TLS_CONTAINER=rhino-compute-tls
REPO_URL="$(env_value "$SERVER_ENV" REPO_URL)"
REPO_URL="${REPO_URL:-$(sed -n 's/^ARG REPO_URL=//p' "$SCRIPT_DIR/Dockerfile")}"
BRANCH="$(env_value "$SERVER_ENV" BRANCH)"
BRANCH="${BRANCH:-$(sed -n 's/^ARG BRANCH=//p' "$SCRIPT_DIR/Dockerfile")}"

[ -n "$DEPLOY_HOST" ] || die "DEPLOY_HOST is not set in setup/.env.server."

ssh_() { ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "$DEPLOY_HOST" "$@"; }

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
        default="$(git -C "$p" symbolic-ref -q --short refs/remotes/origin/HEAD 2>/dev/null | sed 's|^origin/||')"
        case "$branch" in "${default:-main}"|"${default:-master}") ;; *) suspect=1 ;; esac
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
    read -r -p "    A plugin is not from a clean default branch. Deploy anyway? [y/N] " answer
    [ "$answer" = "y" ] || [ "$answer" = "Y" ] || die "aborted."
fi
printf '%s' "$manifest" > "$STAGE/DEPLOYED"

# -------------------------------------------------------
# Server checkout + files
# -------------------------------------------------------
log "Preparing $DEPLOY_HOST"
# A fresh server can take a few minutes to accept logins, and refuses the key
# until its first boot has installed it, so only an unknown host fails at once.
for i in $(seq 1 30); do
    err="$(ssh_ true 2>&1)" && break
    err="$(echo "$err" | tail -1)"
    case "$err" in *"Could not resolve hostname"*) die "$err (is DEPLOY_HOST an alias missing from ~/.ssh/config?)" ;; esac
    [ "$i" = 30 ] && die "cannot log in to $DEPLOY_HOST: $err"
    echo "    waiting for SSH: $err"
    sleep 10
done
# server-init.sh runs again whenever it changed since it last ran there, so existing
# servers pick up new host settings.
init_sum="$(cksum < "$SCRIPT_DIR/server-init.sh" | cut -d' ' -f1)"
if [ "$(ssh_ 'cat /var/lib/rhino-compute-init 2>/dev/null' || true)" != "$init_sum" ] \
        || ! ssh_ "docker info >/dev/null 2>&1"; then
    echo "    running server-init.sh with sudo"
    ssh_ "sudo -n bash -s" < "$SCRIPT_DIR/server-init.sh" \
        || die "server-init.sh failed on $DEPLOY_HOST (the SSH user needs passwordless sudo)."
    ssh_ "echo $init_sum | sudo -n tee /var/lib/rhino-compute-init >/dev/null"
    # A new login picks up the docker group server-init.sh granted.
    ssh_ "docker info >/dev/null 2>&1" || die "Docker is still not usable by $DEPLOY_HOST after server-init.sh."
fi
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
    # Behind the proxy, plain HTTP on PORT must not reach the internet.
    [ -n "$DEPLOY_TLS_DOMAIN" ] && echo "BIND=127.0.0.1"
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

# -------------------------------------------------------
# HTTPS, so the RhinoComputeKey never crosses a network in clear text. The
# certificate lives in a volume and survives redeploys, which keeps Let's
# Encrypt's rate limits out of reach.
# -------------------------------------------------------
if [ -n "$DEPLOY_TLS_DOMAIN" ]; then
    log "HTTPS for $DEPLOY_TLS_DOMAIN"
    allow=""
    if [ -n "$DEPLOY_ALLOW_IPS" ]; then
        allow="	@blocked not remote_ip ${DEPLOY_ALLOW_IPS//,/ }
	respond @blocked 403"
    fi
    # The admin API is off: on the host network any local process could reconfigure it.
    cat <<EOF | ssh_ "mkdir -p caddy && cat > caddy/Caddyfile"
{
	admin off
}
$DEPLOY_TLS_DOMAIN {
	header {
		Strict-Transport-Security "max-age=31536000"
		-Server
	}
$allow
	reverse_proxy localhost:$PORT
}
EOF
    ssh_ "set -e
        docker pull -q caddy:2 >/dev/null
        docker rm -f $TLS_CONTAINER >/dev/null 2>&1 || true
        docker run -d --name $TLS_CONTAINER --restart unless-stopped --network host \
            -v rhino-compute-tls:/data -v \$HOME/caddy/Caddyfile:/etc/caddy/Caddyfile:ro \
            caddy:2 >/dev/null"
    key="$(env_value "$SERVER_ENV" RHINO_COMPUTE_KEY)"
    hdr=(); [ -n "$key" ] && hdr=(-H "RhinoComputeKey: $key")
    for i in $(seq 1 24); do
        code="$(curl -s -o /dev/null -w '%{http_code}' -m 10 "${hdr[@]}" "https://$DEPLOY_TLS_DOMAIN/healthcheck" || true)"
        [ "$code" = 200 ] && break
        if [ "$code" = 403 ] && [ -n "$DEPLOY_ALLOW_IPS" ]; then
            echo "    403 from here: this machine is not in DEPLOY_ALLOW_IPS"
            break
        fi
        [ "$i" = 24 ] && die "https://$DEPLOY_TLS_DOMAIN/healthcheck did not answer (last: $code). Does the name resolve to the server, with ports 80 and 443 open? Logs: docker logs $TLS_CONTAINER"
        sleep 5
    done
    ok "https://$DEPLOY_TLS_DOMAIN answers"
else
    ssh_ "docker rm -f $TLS_CONTAINER >/dev/null 2>&1 || true"
fi
ssh_ "docker image prune -f >/dev/null"

echo ""
echo "============================================================"
echo "  Deployed. Running: compute ${remote_head:0:7}, plugins:"
for name in "${plugin_names[@]}"; do echo "    $name"; done
[ -n "$DEPLOY_TLS_DOMAIN" ] && echo "  Clients use: https://$DEPLOY_TLS_DOMAIN"
echo "  Record on the server: ~/plugins/DEPLOYED"
echo "============================================================"
