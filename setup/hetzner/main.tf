# ============================================================
# Rhino.Compute on Hetzner Cloud
#
# One x86 server with dedicated vCPUs, a private network for the app servers
# that call it, and a firewall that keeps port 6500 off the internet.
# Secrets never pass through here: ../deploy-server.sh writes them over SSH,
# so they stay out of the state file and the server's metadata.
#
#   export HCLOUD_TOKEN=...      # Hetzner Console → project → Security → API tokens
#   terraform init
#   terraform apply
# ============================================================

terraform {
  required_version = ">= 1.5.0"
  required_providers {
    hcloud = {
      source  = "hetznercloud/hcloud"
      version = "~> 1.45"
    }
  }
}

provider "hcloud" {}

variable "name" {
  description = "Prefix for every resource"
  type        = string
  default     = "rhino-compute"
}

variable "location" {
  description = "Hetzner location (fsn1, nbg1, hel1 are in the EU)"
  type        = string
  default     = "fsn1"
}

variable "server_type" {
  description = "Must be x86 with dedicated vCPUs (CCX line): Rhino bills per core, and McNeel's arm64 packages lag months behind"
  type        = string
  default     = "ccx23" # 4 dedicated vCPU, 16 GB
}

variable "ssh_public_key_path" {
  description = "Public key installed for the deploy user"
  type        = string
  default     = "~/.ssh/id_ed25519.pub"
}

variable "ssh_allowed_ips" {
  description = "Source ranges allowed to reach SSH"
  type        = list(string)
  default     = ["0.0.0.0/0", "::/0"]
}

variable "compute_client_ips" {
  description = "Public IPs allowed on port 6500. Leave empty when every client is on the private network; anything listed talks plain HTTP over the internet."
  type        = list(string)
  default     = []
}

variable "private_ip" {
  description = "The server's address on the private network; clients use http://<this>:6500"
  type        = string
  default     = "10.0.1.10"
}

locals {
  network_zone = {
    fsn1 = "eu-central"
    nbg1 = "eu-central"
    hel1 = "eu-central"
    ash  = "us-east"
    hil  = "us-west"
    sin  = "ap-southeast"
  }[var.location]
  ssh_public_key = trimspace(file(pathexpand(var.ssh_public_key_path)))
}

resource "hcloud_ssh_key" "deploy" {
  name       = "${var.name}-deploy"
  public_key = local.ssh_public_key
}

resource "hcloud_network" "private" {
  name     = "${var.name}-net"
  ip_range = "10.0.0.0/16"
}

resource "hcloud_network_subnet" "private" {
  network_id   = hcloud_network.private.id
  type         = "cloud"
  network_zone = local.network_zone
  ip_range     = "10.0.1.0/24"
}

# Hetzner firewalls filter the public interface only; the private network is open.
resource "hcloud_firewall" "compute" {
  name = "${var.name}-fw"

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "22"
    source_ips = var.ssh_allowed_ips
  }

  rule {
    direction  = "in"
    protocol   = "icmp"
    source_ips = ["0.0.0.0/0", "::/0"]
  }

  dynamic "rule" {
    for_each = length(var.compute_client_ips) > 0 ? [1] : []
    content {
      direction  = "in"
      protocol   = "tcp"
      port       = "6500"
      source_ips = var.compute_client_ips
    }
  }
}

resource "hcloud_server" "compute" {
  name         = var.name
  server_type  = var.server_type
  location     = var.location
  image        = "ubuntu-24.04"
  ssh_keys     = [hcloud_ssh_key.deploy.id]
  firewall_ids = [hcloud_firewall.compute.id]
  user_data    = templatefile("${path.module}/cloud-init.yaml", { ssh_public_key = local.ssh_public_key })

  public_net {
    ipv4_enabled = true
    ipv6_enabled = true
  }

  network {
    network_id = hcloud_network.private.id
    ip         = var.private_ip
  }

  depends_on = [hcloud_network_subnet.private]

  # cloud-init runs on first boot only, and a changed key or user_data would
  # otherwise replace the server.
  lifecycle {
    ignore_changes = [user_data, ssh_keys]
  }
}

output "deploy_host" {
  description = "DEPLOY_HOST for setup/.env.server"
  value       = "deploy@${hcloud_server.compute.ipv4_address}"
}

output "compute_url" {
  description = "Rhino.Compute URL for clients on the private network"
  value       = "http://${var.private_ip}:6500"
}

output "network_name" {
  description = "Attach app servers to this network to reach compute_url"
  value       = hcloud_network.private.name
}
