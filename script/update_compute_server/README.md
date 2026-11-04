# Rhino.Compute server: setup and update (Selva fork)

Runs the `8.x.selva` build of `VektorNode/compute.rhino3d` on Windows Server / IIS (site `Rhino.Compute`,
app pool `RhinoComputeAppPool`, web root `C:\inetpub\wwwroot\aspnet_client\system_web\4_0_30319`).

## First-time setup

Run upstream's bootstrap (`script/production/bootstrap_step-1/`) as Administrator. It installs the
upstream `8.x` build; run the update below once to switch to `8.x.selva`.

Machine environment variables, then reboot:

| Variable | Value | Why |
| --- | --- | --- |
| `RHINO_TOKEN` | Core-hour token | Licensing |
| `RHINO_COMPUTE_KEY` | API key | Auth |
| `RHINO_COMPUTE_CREATE_HEADLESS_DOC` | `true` | Selva relies on a per-request headless doc; upstream defaults to `false` |
| `RHINO_COMPUTE_MAX_REQUEST_SIZE` | `314572800` | Large solve payloads; upstream defaults to 50 MB |

```powershell
[Environment]::SetEnvironmentVariable('RHINO_COMPUTE_CREATE_HEADLESS_DOC', 'true', 'Machine')
[Environment]::SetEnvironmentVariable('RHINO_COMPUTE_MAX_REQUEST_SIZE', '314572800', 'Machine')
```

## Update to the latest `8.x.selva` build

Upstream's updater, pointed at the fork. It backs up, rolls back on failure, keeps the server's
`web.config` and health-checks. Elevated PowerShell on the server:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$url = "https://raw.githubusercontent.com/VektorNode/compute.rhino3d/8.x.selva/script/production/module_update_compute.ps1"
& ([scriptblock]::Create((Invoke-RestMethod $url))) -Repository VektorNode/compute.rhino3d -Branch 8.x.selva
```

Add `-SkipHops` to leave the compute user's Hops alone, `-Force` to reinstall the same build. The log
is `update_compute_log.txt` in the install folder (`C:\Rhino-Compute-Installation`).
