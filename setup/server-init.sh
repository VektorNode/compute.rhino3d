#!/usr/bin/env bash
# ============================================================
# server-init.sh  (on the server, as root)
# Prepares an Ubuntu or Debian host for Rhino.Compute: Docker with log
# rotation, plus git, rsync, curl and jq. deploy-server.sh runs it with sudo
# when Docker is missing; it also works as a cloud's first-boot script.
# Safe to run again.
#
# Usage:
#   sudo bash server-init.sh
# ============================================================

set -e

[ "$(id -u)" = 0 ] || { echo "ERROR: run as root (sudo bash server-init.sh)." >&2; exit 1; }
if [ "$(uname -m)" != x86_64 ]; then
    echo "WARNING: $(uname -m) host. McNeel's arm64 Rhino packages lag months behind x86-64."
fi

# A fresh cloud image may still be running its own first-boot package work,
# and apt fails while another apt holds the lock.
if command -v cloud-init >/dev/null; then cloud-init status --wait >/dev/null 2>&1 || true; fi
while fuser /var/lib/dpkg/lock-frontend /var/lib/apt/lists/lock >/dev/null 2>&1; do sleep 5; done

export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q git rsync curl jq

if ! command -v docker >/dev/null; then
    mkdir -p /etc/docker
    # Docker's default json-file log never rotates; read when the daemon first starts.
    [ -f /etc/docker/daemon.json ] || \
        echo '{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "5" } }' \
            > /etc/docker/daemon.json
    curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker

# The user who ran sudo deploys without it from the next login on.
if [ -n "$SUDO_USER" ] && [ "$SUDO_USER" != root ]; then
    usermod -aG docker "$SUDO_USER"
fi

echo "server-init.sh: Docker $(docker --version | cut -d' ' -f3 | tr -d ,) ready."
