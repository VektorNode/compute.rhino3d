# ============================================================
# docker-launch.ps1  (Windows / Docker Desktop)
#
# PowerShell port of docker-launch.sh. Same image, same container,
# same mounts — only the host-side scripting differs.
#
# Usage:
#   .\docker-launch.ps1
#   $env:RHINO_TOKEN="your-token"; .\docker-launch.ps1
#   $env:PORT="7000"; .\docker-launch.ps1
#
# Env vars (or set them in setup\.env):
#   RHINO_TOKEN       - Rhino Core-Hour Billing token (needed for real work)
#   RHINO_COMPUTE_KEY - Optional shared secret; clients send it as RhinoComputeKey
#   IMAGE_NAME        - Docker image tag (default: rhino-compute-x9)
#   CONTAINER_NAME    - Container name (default: rhino-compute-x9)
#   PORT              - Host port to bind (default: 6500)
#   CHILD_COUNT       - Number of compute.geometry children (default: 1)
#   REPO_URL          - Repo to clone inside the image (default: Dockerfile ARG)
#   BRANCH            - Branch to check out inside the image (default: Dockerfile ARG)
#   NO_BUILD          - Skip the image build step if "1" (reuse existing image)
#   FRESH             - No-cache rebuild if "1" (use after pushing source changes)
#   LOCAL_PLUGINS     - Comma-separated host folders to mount as live plugins
#   PLATFORM          - Docker platform (default: linux/amd64)
#   CPUS              - CPU-time cap, e.g. CPUS=4 (docker --cpus). Fractions allowed.
#   CPUSET            - Pin to specific cores instead, e.g. CPUSET=0-3 (--cpuset-cpus)
#   MEMORY            - RAM cap, e.g. MEMORY=8g (docker --memory)
#
# Rhino.Compute has no core limit of its own: --childcount only sets how many
# compute.geometry workers run, and each worker is a headless Rhino that uses
# many threads. Cap cores at the container level (CPUS/CPUSET) and keep
# CHILD_COUNT <= that number.
#
# The container is Linux either way: on Windows this needs Docker Desktop in
# Linux-container mode (the default), backed by WSL2. Note Docker Desktop's
# own VM has a global CPU/memory ceiling (Settings > Resources) that applies
# on top of these per-container limits.
# ============================================================

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Log([string]$m) { Write-Host ""; Write-Host "==> $m" }
function Ok([string]$m)  { Write-Host "    [ok]  $m" }
function Warn([string]$m) { Write-Host "    WARNING: $m" -ForegroundColor Yellow }
function Fail([string]$m) { Write-Host ""; Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# -------------------------------------------------------
# Load setup\.env. Values already set in the shell win —
# .env only fills in what isn't already set.
# -------------------------------------------------------
$Vars = @('RHINO_TOKEN','RHINO_COMPUTE_KEY','IMAGE_NAME','CONTAINER_NAME','PORT',
          'CHILD_COUNT','REPO_URL','BRANCH','NO_BUILD','FRESH','LOCAL_PLUGINS','PLATFORM',
          'CPUS','CPUSET','MEMORY')

$EnvFile = Join-Path $ScriptDir '.env'
if (Test-Path $EnvFile) {
    foreach ($line in Get-Content $EnvFile) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $kv = $t -split '=', 2
        if ($kv.Count -ne 2) { continue }
        $k = $kv[0].Trim()
        if ($Vars -notcontains $k) { continue }
        # Shell already set it? Leave it alone.
        if ([Environment]::GetEnvironmentVariable($k)) { continue }
        $v = $kv[1].Trim().Trim('"').Trim("'")
        Set-Item -Path "Env:$k" -Value $v
    }
}

function EnvOr([string]$name, [string]$default) {
    $v = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $default } else { return $v }
}

$ImageName     = EnvOr 'IMAGE_NAME'     'rhino-compute-x9'
$ContainerName = EnvOr 'CONTAINER_NAME' 'rhino-compute-x9'
$Port          = EnvOr 'PORT'           '6500'
$ChildCount    = EnvOr 'CHILD_COUNT'    '1'
$Platform      = EnvOr 'PLATFORM'       'linux/amd64'
$Token         = EnvOr 'RHINO_TOKEN'    ''
$ComputeKey    = EnvOr 'RHINO_COMPUTE_KEY' ''
$Cpus          = EnvOr 'CPUS'           ''
$CpuSet        = EnvOr 'CPUSET'         ''
$MemoryLimit   = EnvOr 'MEMORY'         ''

Write-Host ""
Write-Host "============================================================"
Write-Host "  Rhino.Compute — Docker launch (Windows)"
Write-Host "============================================================"
if ($Token) { Write-Host "  Token      : set" } else { Write-Host "  Token      : NOT set (server starts, solves will fail)" }
Write-Host "  Image      : $ImageName"
Write-Host "  Container  : $ContainerName"
Write-Host "  Port       : $Port"
Write-Host "  Children   : $ChildCount"
if     ($CpuSet) { Write-Host "  CPU limit  : cores $CpuSet (pinned)" }
elseif ($Cpus)   { Write-Host "  CPU limit  : $Cpus CPUs" }
else             { Write-Host "  CPU limit  : none (all host cores)" }
if ($MemoryLimit) { Write-Host "  Memory     : $MemoryLimit" }
Write-Host "  Platform   : $Platform"
Write-Host "============================================================"

# -------------------------------------------------------
# Resource limits. Rhino.Compute cannot cap cores itself — --childcount only
# sets how many compute.geometry workers run, each a headless Rhino using many
# threads. The cap belongs at the container level; keep CHILD_COUNT at or
# below the allowed core count.
# -------------------------------------------------------
$limitArgs = @()
if ($Cpus -and $CpuSet) {
    Warn "CPUS and CPUSET are both set — using CPUSET ($CpuSet) and ignoring CPUS."
    $Cpus = ''
}
if ($Cpus)        { $limitArgs += @('--cpus', $Cpus) }
if ($CpuSet)      { $limitArgs += @('--cpuset-cpus', $CpuSet) }
if ($MemoryLimit) { $limitArgs += @('--memory', $MemoryLimit) }

# How many cores the cap actually allows, to sanity-check CHILD_COUNT.
$allowed = 0
if ($CpuSet) {
    foreach ($part in ($CpuSet -split ',')) {
        $p = $part.Trim()
        if (-not $p) { continue }
        if ($p -match '^(\d+)-(\d+)$') { $allowed += [int]$Matches[2] - [int]$Matches[1] + 1 }
        else { $allowed += 1 }
    }
} elseif ($Cpus) {
    $allowed = [int][math]::Floor([double]$Cpus)
}
if ($allowed -gt 0 -and [int]$ChildCount -gt $allowed) {
    Warn "CHILD_COUNT=$ChildCount exceeds the $allowed core(s) this container may use."
    Write-Host "             Each child is a full headless Rhino — more workers than cores"
    Write-Host "             mostly adds contention. Consider CHILD_COUNT=$allowed."
}

# -------------------------------------------------------
# Docker CLI + daemon
# -------------------------------------------------------
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Fail "docker not found. Install Docker Desktop: https://www.docker.com/products/docker-desktop/"
}

docker info 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Log "Docker daemon not responding — trying to start Docker Desktop..."
    $dd = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (Test-Path $dd) {
        Start-Process $dd | Out-Null
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Seconds 2
            docker info 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
            Write-Host -NoNewline "."
        }
        Write-Host ""
    }
    docker info 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Fail "Docker daemon is not running. Start Docker Desktop and re-run."
    }
}
Ok "Docker daemon is running"

# Linux containers required — the image is Ubuntu-based.
$osType = (docker info --format '{{.OSType}}' 2>$null)
if ($osType -and $osType -ne 'linux') {
    Fail "Docker is in '$osType' container mode. Right-click the Docker Desktop tray icon and choose 'Switch to Linux containers'."
}

# -------------------------------------------------------
# Build
# -------------------------------------------------------
if ((EnvOr 'NO_BUILD' '') -eq '1') {
    Log "Skipping build (NO_BUILD=1) — reusing existing image"
} else {
    Log "Building image '$ImageName' (this takes a while the first time)"
    $buildArgs = @('build', '--platform', $Platform, '-t', $ImageName)
    if (EnvOr 'REPO_URL' '') { $buildArgs += @('--build-arg', "REPO_URL=$(EnvOr 'REPO_URL' '')") }
    if (EnvOr 'BRANCH'   '') { $buildArgs += @('--build-arg', "BRANCH=$(EnvOr 'BRANCH' '')") }
    # FRESH=1 busts the layer cache — needed to pick up new commits, since the
    # git clone happens inside a cached build layer.
    if ((EnvOr 'FRESH' '') -eq '1') { $buildArgs += '--no-cache' }
    $buildArgs += $ScriptDir

    & docker @buildArgs
    if ($LASTEXITCODE -ne 0) { Fail "Image build failed." }
    Ok "Image built"
}

# -------------------------------------------------------
# Replace any existing container
# -------------------------------------------------------
$existing = docker ps -aq --filter "name=^$ContainerName$"
if ($existing) {
    Log "Removing existing container '$ContainerName'"
    docker rm -f $ContainerName | Out-Null
}

# -------------------------------------------------------
# Mounts
# -------------------------------------------------------
$mounts = @()

$packages = Join-Path $ScriptDir 'packages.json'
if (Test-Path $packages) {
    $mounts += @('-v', "${packages}:/packages.json:ro")
} else {
    # Gitignored, per-deployment (like .env). Without it the container starts
    # cleanly with NO yak packages, and the failure only surfaces later as
    # solves missing components.
    Warn "setup\packages.json not found — no yak packages will be installed."
    Write-Host "             copy packages.example.json to packages.json (then edit) to fix."
}

# Custom fonts — the container ships only Liberation/DejaVu; start.sh runs
# fc-cache over this mount so text-to-curve components can use them.
$fonts = Join-Path $ScriptDir 'fonts'
New-Item -ItemType Directory -Force -Path $fonts | Out-Null
$mounts += @('-v', "${fonts}:/usr/local/share/fonts/custom:ro")

# Live-mounted plugin build folders. Mounted under /plugins-local (NOT inside
# /plugins — that mount is read-only, so Docker cannot nest mountpoints in it).
$localPlugins = EnvOr 'LOCAL_PLUGINS' ''
if ($localPlugins) {
    $idx = 0
    foreach ($raw in ($localPlugins -split ',')) {
        $p = $raw.Trim()
        if (-not $p) { continue }
        if (-not (Test-Path $p)) { Warn "LOCAL_PLUGINS folder not found, skipping: $p"; continue }
        $full = (Resolve-Path $p).Path

        # Name the mount after the folder, walking up past generic build-output
        # names like net8.0 / Release / Debug / bin / obj / Build.
        $walk = $full
        $name = Split-Path $walk -Leaf
        while ($name -match '^(net[0-9].*|Release|Debug|bin|obj|Build)$') {
            $parent = Split-Path $walk -Parent
            if (-not $parent -or $parent -eq $walk) { break }
            $walk = $parent
            $name = Split-Path $walk -Leaf
        }

        # Docker Desktop accepts Windows paths here; the container side stays POSIX.
        $mounts += @('-v', "${full}:/plugins-local/local-$idx-${name}:ro")
        Ok "Live plugin mount: $full -> /plugins-local/local-$idx-$name"
        $idx++
    }
}

# -------------------------------------------------------
# Run
# -------------------------------------------------------
Log "Starting container '$ContainerName'"
$runArgs = @('run', '-d', '--platform', $Platform, '--name', $ContainerName,
             '-p', "${Port}:6500",
             '-e', "RHINO_TOKEN=$Token",
             '-e', "RHINO_COMPUTE_CHILD_COUNT=$ChildCount")
if ($ComputeKey) { $runArgs += @('-e', "RHINO_COMPUTE_KEY=$ComputeKey") }
$runArgs += $limitArgs
$runArgs += $mounts
$runArgs += $ImageName

& docker @runArgs | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "docker run failed." }
Ok "Container started"

# -------------------------------------------------------
# Wait for readiness. "Container started" is not "server ready" —
# Rhino + Grasshopper take a while to boot inside.
# -------------------------------------------------------
Log "Waiting for Rhino.Compute to become ready..."

$headers = @{}
if ($ComputeKey) { $headers['RhinoComputeKey'] = $ComputeKey }

$ready = $false
$elapsed = 0
for ($i = 1; $i -le 90; $i++) {
    $running = docker ps --format '{{.Names}}' | Where-Object { $_ -eq $ContainerName }
    if (-not $running) {
        Write-Host ""
        Write-Host "ERROR: container '$ContainerName' exited unexpectedly during startup." -ForegroundColor Red
        Write-Host "Last log lines:"
        docker logs --tail 30 $ContainerName 2>&1 | ForEach-Object { "    $_" }
        exit 1
    }
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$Port/healthcheck" -Headers $headers `
                               -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        if ($r.StatusCode -eq 200) { $ready = $true; $elapsed = $i * 2; break }
    } catch { }
    Write-Host -NoNewline "."
    Start-Sleep -Seconds 2
}
Write-Host ""

if ($ready) {
    Ok "Rhino.Compute responded to /healthcheck (took ~${elapsed}s)"
} else {
    Warn "Server did not respond within ~180s. It may still be starting"
    Write-Host "             (first boot after a rebuild is slow) — check:"
    Write-Host "                 docker logs -f $ContainerName"
}

Write-Host ""
Write-Host "============================================================"
if ($ready) {
    Write-Host "  Done! Rhino.Compute is up and responding."
} else {
    Write-Host "  Container started, but not confirmed ready yet — see above."
}
Write-Host "============================================================"
Write-Host ""
Write-Host "  Connect from this PC:"
Write-Host "    http://localhost:$Port"
Write-Host ""
Write-Host "  Grasshopper definitions must be reachable FROM INSIDE the"
Write-Host "  container. Use http://host.docker.internal:<port>/file.gh"
Write-Host "  instead of http://localhost or http://127.0.0.1"
Write-Host ""
Write-Host "  Logs:   docker logs -f $ContainerName"
Write-Host "  Stop:   docker stop $ContainerName"
Write-Host ""
