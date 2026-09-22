# compute.rhino3d — Vektornode / Selva fork

This repo is a **fork** of McNeel's [compute.rhino3d](https://github.com/mcneel/compute.rhino3d).
This branch is `9.x.selva`, tracking `upstream/9.x`.

Remotes: `origin` = VektorNode fork, `upstream` = mcneel.

## Rule: document every upstream divergence

**Any change that makes this fork differ from upstream must be documented in `FORK_CHANGES.md`,
in the same commit that makes the change.** This applies to small and temporary changes too —
those are the ones that get forgotten and then cost real time during the next upstream merge.

For a source change:

1. Tag it in source: `// VEKTORNODE: <TAG> — <why>`. Use BEGIN/END banners for blocks.
   Tags: `SELVA` (feature), `SELVA FIX` (bug fix vs upstream), `PARAM-ID`, `IO-HANDLERS`,
   `CACHE-ERRORED-SOLVES`.
2. Add or update a numbered section in `FORK_CHANGES.md` covering **what upstream did, why that
   was wrong or insufficient for us, and what we do instead.** The "why" is what has value
   later; the diff already shows the "what".

Tooling, scripts and docs with no upstream counterpart go under **Non-source divergence**.

When a divergence goes away — upstream adopted it, or we no longer need it — delete its section
in the same commit.

## Auditing the delta

Use `--ignore-all-space`. A plain `git diff` against upstream also reports files that differ
only in line endings, which badly overstates the delta (`* text=auto` stores LF in the index
while a handful of upstream files are CRLF).

```
git diff --ignore-all-space --stat upstream/9.x 9.x.selva
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
