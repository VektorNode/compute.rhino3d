#!/usr/bin/env bash
# ============================================================
# server-init.sh  (on the server, as root)
# Prepares an Ubuntu or Debian host for Rhino.Compute: Docker with log
# rotation, git, rsync, curl and jq, automatic security updates and key-only
# SSH. deploy-server.sh runs it with sudo whenever this file changed since it
# last ran there; it also works as a cloud's first-boot script.
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
apt-get install -y -q git rsync curl jq unattended-upgrades

# OS security updates install themselves daily.
printf 'APT::Periodic::Update-Package-Lists "1";\nAPT::Periodic::Unattended-Upgrade "1";\n' \
    > /etc/apt/apt.conf.d/20auto-upgrades

# Key-only SSH: deploy-server.sh logs in with a key anyway, and password logins are
# what bots brute-force. 00- sorts before cloud images' own drop-ins; sshd keeps the
# first value it reads.
if [ -d /etc/ssh/sshd_config.d ]; then
    printf 'PasswordAuthentication no\nKbdInteractiveAuthentication no\nPermitRootLogin prohibit-password\n' \
        > /etc/ssh/sshd_config.d/00-rhino-compute.conf
    if sshd -t; then
        systemctl reload ssh 2>/dev/null || systemctl reload sshd
    else
        rm /etc/ssh/sshd_config.d/00-rhino-compute.conf
        echo "WARNING: sshd rejected the key-only settings; left SSH as it was." >&2
    fi
fi

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
