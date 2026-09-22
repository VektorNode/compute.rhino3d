# compute.rhino3d — Vektornode / Selva fork

This repo is a **fork** of McNeel's [compute.rhino3d](https://github.com/mcneel/compute.rhino3d).

| Branch | Tracks | Notes |
| --- | --- | --- |
| `8.x.selva` | `upstream/8.x` | Windows/IIS production — this branch |
| `9.x.selva` | `upstream/9.x` | Linux/Docker deployment |

Remotes: `origin` = VektorNode fork, `upstream` = mcneel.

## Rule: document every upstream divergence

**Any change that makes this fork differ from upstream must be documented in `FORK_CHANGES.md`,
on that branch, in the same commit that makes the change.** Both `8.x.selva` and `9.x.selva`
carry their own copy. This applies to small and temporary changes too — those are the ones that
get forgotten and then cost real time during the next upstream merge.

For a source change:

1. Tag it in source: `// VEKTORNODE: <TAG> — <why>`. Use BEGIN/END banners for blocks.
   Tags: `SELVA` (feature), `SELVA FIX` (bug fix vs upstream), `PARAM-ID`, `IO-HANDLERS`,
   `CACHE-ERRORED-SOLVES`.
2. Add or update a numbered section in `FORK_CHANGES.md` covering **what upstream did, why that
   was wrong or insufficient for us, and what we do instead.** The "why" is what has value
   later; the diff already shows the "what".
3. If the change applies to the other branch too, either port it or note that it is
   branch-specific.

Tooling, scripts and docs with no upstream counterpart go under **Non-source divergence**.

When a divergence goes away — upstream adopted it, or we no longer need it — delete its section
in the same commit.

## Auditing the delta

Use `--ignore-all-space`. A plain `git diff` against upstream also reports files that differ
only in line endings, which badly overstates the delta (`* text=auto` stores LF in the index
while a handful of upstream files are CRLF — `compute.geometry/Program.cs` alone reads as 622
changed lines that are purely line endings).

```
git diff --ignore-all-space --stat upstream/8.x 8.x.selva
grep -rn "VEKTORNODE" src/
```

Every file in that diff should map to a section in `FORK_CHANGES.md`, and every `VEKTORNODE`
marker should correspond to one.

## After merging upstream

Re-audit with the commands above: a merge can silently make one of our changes redundant. If
upstream has adopted something we were carrying, drop our version rather than keeping a
duplicate. Update `FORK_CHANGES.md` and bump `<Version>` in
`src/compute.geometry/compute.geometry.csproj` (the only place the version lives).

## Building

```
dotnet build src/rhino.compute/rhino.compute.csproj
dotnet build src/compute.geometry/compute.geometry.csproj
```

Pre-existing warnings: `NU1701` (RhinoCommon/Grasshopper targeting .NET Framework) and
`SYSLIB0050` in `GeometryEndPoint.cs`. Both are expected; don't try to "fix" them.

## Child process pool (rhino.compute)

`rhino.compute` proxies to a pool of `compute.geometry` children. Config comes from
`src/rhino.compute/Config.cs` — flag, then env var, then clamped default. The env vars matter
operationally because they can be changed on a deployed server without editing `web.config`:
`RHINO_COMPUTE_CHILDCOUNT`, `RHINO_COMPUTE_IDLESPAN`, `RHINO_COMPUTE_CHILD_STARTUP_TIMEOUT`.

A cold server can legitimately take minutes for the first child to open its port (it does not
listen until Rhino, Grasshopper and the compute plug-ins have loaded), so a first-request
failure is usually a startup-timeout issue, not a crash.

`/activechildren` reports the ready-pool count and never spawns. To launch, POST
`/launch-children` or `/launch-child`.
