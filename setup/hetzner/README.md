# Rhino.Compute on Hetzner Cloud

Terraform for one Rhino.Compute server, plus [`../deploy-server.sh`](../deploy-server.sh)
to deploy to it from your machine. The server runs the same Docker image as your
local container (see [setup/README.md](../README.md)).

What `terraform apply` creates:

- an x86 server with dedicated vCPUs (`ccx23`: 4 vCPU, 16 GB), Ubuntu 24.04, Docker,
  and a `deploy` user that logs in with your SSH key (root login and passwords are off)
- a private network, on which the server is `10.0.1.10`
- a firewall that allows SSH and keeps port 6500 off the public internet

No secret passes through Terraform. `deploy-server.sh` writes them to the server
over SSH, so they are in neither the state file nor the server's metadata.

## First time

You need a Hetzner Cloud project, [Terraform](https://developer.hashicorp.com/terraform/install),
a Rhino core-hour billing token, and an SSH key at `~/.ssh/id_ed25519.pub`
(`ssh-keygen -t ed25519` if there is none; another path goes in `terraform.tfvars`).

1. **Provision.** Create an API token (Hetzner Console → the project → Security →
   API tokens, Read & Write), then:

   ```bash
   cd setup/hetzner
   export HCLOUD_TOKEN=...
   terraform init
   terraform apply
   ```

   It prints `deploy_host`, `compute_url` and `network_name`. Optional settings
   are in [terraform.tfvars.example](terraform.tfvars.example).

2. **Configure the deploy.**

   ```bash
   cd ..
   cp .env.server.example .env.server
   ```

   Set `DEPLOY_HOST` to `deploy_host`, your `RHINO_TOKEN`, and a
   `RHINO_COMPUTE_KEY` from `openssl rand -hex 32`.

3. **Deploy.**

   ```bash
   ./deploy-server.sh
   ```

   The first run waits for the server's first boot to finish and builds the image,
   which takes several minutes.

4. **Connect the app.** Attach the app's server to the network named
   `network_name` (Hetzner Console → Networks, or create the server with that
   network), then give the app `compute_url` as its Compute URL and the same
   `RHINO_COMPUTE_KEY`. A client outside Hetzner can be let in with
   `compute_client_ips`, but then it talks plain HTTP over the internet.

## What `deploy-server.sh` does

1. Refuses to run unless `setup/` and `src/` are committed and the branch is pushed:
   the server builds the image from GitHub.
2. Lists each plugin with the repo, branch and commit its folder belongs to, and
   asks before shipping one from a dirty tree or a branch other than `main`.
3. Syncs the plugin builds, `packages.json`, `setup/plugins/` and `setup/fonts/`,
   and writes the server's `setup/.env` from `.env.server`.
4. Runs `docker-launch.sh` on the server, waits for `/healthcheck`, and prints
   `docker-status.sh` (which plugins loaded, memory).

`~/plugins/DEPLOYED` on the server records what the last deploy shipped.

## After a change

| You changed                              | Run                                      |
| ---------------------------------------- | ---------------------------------------- |
| Compute source, `start.sh`, `Dockerfile` | push, then `./deploy-server.sh`          |
| A plugin                                 | rebuild it, then `./deploy-server.sh`    |
| `packages.json` or `.env.server`         | `./deploy-server.sh`                     |
| Server size                              | `server_type` in `terraform.tfvars`, then `terraform apply` (the server restarts; its disk cannot shrink back) |

`./deploy-server.sh status`, `logs` and `ssh` look at the running server.

## Idle shutdown and billing

The server itself is billed by Hetzner whether it is busy or not. Rhino bills per
core while a `compute.geometry` child runs, and `IDLE_SPAN` makes the children exit
that many seconds after the last request; the next solve starts them again.

Every request a child answers resets that clock: `/healthcheck`, `/version`, and
`/plugins/...` as well as solves. A monitor that probes one of them keeps Rhino
billing around the clock. Probe `/activechildren?initialize=false` instead, which
the parent answers without waking a child.

## Tear down

```bash
cd setup/hetzner && terraform destroy
```
