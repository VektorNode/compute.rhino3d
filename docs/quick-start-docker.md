# Quick Start: Rhino.Compute in Docker (macOS)

Cheat-sheet for the commands you actually run day to day. For explanations
and edge cases, see [setup/README.md](../setup/README.md).

## First-time setup

```bash
cd setup
cp .env.example .env
# edit .env: set RHINO_TOKEN (and RHINO_COMPUTE_KEY if your app needs one)
cp packages.example.json packages.json
# edit packages.json: the yak packages + local plugins this server should load
./docker-launch.sh
```

This builds the image, starts the container, and waits until the server
actually responds before printing success.

## Every day after that

```bash
cd setup
./docker-status.sh          # is it running? healthy? which plugins loaded?
```

If it's not running:

```bash
docker start rhino-compute-x9
./docker-status.sh           # confirm it actually came up — don't trust silence
```

## Changing plugins or packages

Edit `setup/packages.json` (yak versions, local plugin names), then
**recreate** the container — a plain restart skips packages that are
already installed and won't pick up version changes:

```bash
cd setup
NO_BUILD=1 ./docker-launch.sh
```

Custom (non-Yak) plugins come from **one** of two places — pick per plugin,
never both:

- `setup/plugins/` — drop the `.gha`/`.dll` there, then `NO_BUILD=1 ./docker-launch.sh`.
- `LOCAL_PLUGINS` in `.env` — live-mount your build folder, then a plain
  `docker restart rhino-compute-x9` re-copies it on every start.

`setup/plugins/` is gitignored, so an old copy left there is invisible to
git and keeps loading under the same assembly name as your live build. The
container refuses to start if it finds the same `.gha` twice; when it does,
the leftover is in `setup/plugins/`.

## After changing compute.rhino3d source code

The image clones from GitHub — your local edits don't reach it until
pushed:

```bash
git add -A && git commit -m "..."
git push
cd setup
FRESH=1 ./docker-launch.sh   # no-cache rebuild, pulls the new commit
```

`FRESH=1` is required — without it, Docker reuses the cached `git clone`
layer and you get the old code. Expect several minutes (full apt install,
.NET SDK, Rhino libs, clone, compile).

## Common env vars (set in `setup/.env` or inline)

| Var                 | Purpose                                               |
| ------------------- | ----------------------------------------------------- |
| `RHINO_TOKEN`       | Core-hour billing token (required for real solves)    |
| `RHINO_COMPUTE_KEY` | Optional shared secret clients must send as header    |
| `PORT`              | Host port (default `6500`)                            |
| `CHILD_COUNT`       | Number of compute.geometry children (default `1`)     |
| `LOCAL_PLUGINS`     | Comma-separated host folders to live-mount as plugins |
| `NO_BUILD=1`        | Skip image build, just recreate the container         |
| `FRESH=1`           | No-cache rebuild — use after pushing source changes   |
| `PLATFORM`          | Docker platform (default `linux/amd64`, also on Apple Silicon — arm64 Rhino packages lag months behind) |

## Everything runs locally

`docker-launch.sh` builds and runs entirely on your Mac via OrbStack/Docker
Desktop. The only network call during build is `git clone` of the pushed
branch — package installs, compilation, and the running server all happen
in a local container bound to `localhost:$PORT`.

## Useful one-offs

```bash
docker logs -f rhino-compute-x9              # live logs
docker exec rhino-compute-x9 yak list        # installed yak packages
docker stop rhino-compute-x9                 # stop (keeps container)
docker rm -f rhino-compute-x9                # remove entirely
curl http://localhost:6500/healthcheck \
  -H "RhinoComputeKey: $RHINO_COMPUTE_KEY"    # manual healthcheck
```
