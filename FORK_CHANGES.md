# Fork changes — Vektornode / Selva vs. upstream `compute.rhino3d` (9.x)

This is a **fork** of McNeel's [compute.rhino3d](https://github.com/mcneel/compute.rhino3d),
branch `9.x.selva`, tracking `upstream/9.x`.

**Every divergence from upstream must be documented here.** See
[Rule: document every upstream divergence](#rule-document-every-upstream-divergence).

Source changes are tagged with a `VEKTORNODE:` comment so they can be found by grep:

```
grep -rn "VEKTORNODE" src/
```

Block-level changes are wrapped in banners:

```
// ── BEGIN VEKTORNODE: SELVA FIX — <topic> ──
...
// ── END   VEKTORNODE: SELVA FIX — <topic> ──
```

Tags used:
- **SELVA** — behavior/feature added for the Selva product.
- **SELVA FIX** — a bug fix relative to upstream (wrapped in BEGIN/END banners).
- **PARAM-ID** — threads the source Grasshopper parameter's Instance Guid through the IO model.
- **IO-HANDLERS** — extra input/output type handlers + schema metadata not in upstream.
- **CACHE-ERRORED-SOLVES** — opt-in caching of errored-but-completed solves.

> **Auditing the delta against upstream:** use
> `git diff --ignore-all-space upstream/9.x 9.x.selva`. A plain `git diff` also reports files
> that differ only in line endings, which overstates the delta.

> **Relationship to 8.x:** `8.x.selva` carries its own `FORK_CHANGES.md`. Items 1–8 below exist
> on both branches; items 9–12 are 9.x-only (Linux/Docker). When you change shared behaviour,
> check whether the other branch needs the same change and document it there too.

---

## Source changes

### 1. Schema-extraction endpoints (SELVA)
**Files:** `src/compute.geometry/FixedEndpoints.cs`, `src/compute.geometry/GrasshopperValidationHelper.cs` (entire file)

Adds the `grasshopper/schema` family of endpoints (definition IO + validation) used by Selva to
introspect a definition's inputs/outputs, including fetching `.gh`/`.ghx` from a URL or local
path and extracting the embedded schema. Not present in upstream.

### 2. UTF-8 BOM handling for `.ghx` (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperValidationHelper.cs`

Rhino writes `.ghx` with a UTF-8 BOM. `Encoding.UTF8.GetString` keeps the BOM as a leading
character, so the XML parse failed on definitions saved from Rhino. The BOM is stripped before
parsing.

### 3. Headless doc creation default ON (SELVA)
**File:** `src/compute.geometry/Config.cs`

`RHINO_COMPUTE_CREATE_HEADLESS_DOC` defaults to **on** (upstream default is `false`).

### 4. Larger default max request size (SELVA)
**File:** `src/compute.geometry/Config.cs`

`RHINO_COMPUTE_MAX_REQUEST_SIZE` default raised from 50 MB to 300 MB.

### 5. Parameter-Id threading + contextual inputs (PARAM-ID, IO-HANDLERS)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs`, `src/compute.geometry/IO/Schema.cs`

Every emitted `ResthopperObject` is tagged with its source Grasshopper parameter's Instance Guid
so the client can demux outputs by parameter. Adds Selva contextual inputs, hierarchical group
names, enumerated values, and per-parameter declared defaults on the schema.

### 6. Errored-solve caching, opt-in (CACHE-ERRORED-SOLVES)
**Files:** `src/compute.geometry/IO/Schema.cs`, `src/compute.geometry/ResthopperEndpoints.cs`

By default an errored solve is not served from cache. Adds the opt-in flag and the detection of
whether a cached solve-result JSON came from an errored-but-completed solve.

### 7. Undo recording forced off server-side (SELVA FIX)
**File:** `src/compute.geometry/ResthopperEndpoints.cs`

An active undo recorder retains every solve's state, so server-side undo must stay off.

### 8. Stable error code on 500s (SELVA)
**File:** `src/compute.geometry/Startup.cs`

Adds a machine-readable `code` to the error body in **both** debug and production. The human
`message` is scrubbed in prod, so the code is the only thing a client can classify on. A
stale-pointer cache miss surfaces as `definition_not_cached`, which the `@selvajs/compute`
client uses to transparently re-upload the definition.

### 9. Multipart proxy passthrough (SELVA FIX)
**File:** `src/rhino.compute/ReverseProxy.cs`

Upstream read every POST body as a string and re-sent it as `application/json`, destroying the
multipart boundary — so `grasshopper/validate` and other file uploads broke behind IIS. Fix:
detect `multipart/form-data` and stream the body through as-is, preserving boundary + binary
content and forwarding `Content-Length`.

### 10. Dead-child eviction + proxy retry (SELVA FIX)
**Files:** `src/rhino.compute/ComputeChildren.cs` (`EvictChild`), `src/rhino.compute/ReverseProxy.cs`
(`/child-exiting`, retry loop), `src/compute.geometry/Shutdown.cs` (`NotifyParentExiting`)

A child that had self-exited on idle timeout stayed in the round-robin pool, so every request
routed to it failed with "connection refused" until a manual `/shutdown-children`. A child now
POSTs `/child-exiting` just before it stops listening; the parent evicts that port; and the proxy
evicts-and-retries on `HttpRequestError.ConnectionError`. The retry only fires when the connection
was never established, so no solve is ever duplicated. Non-success child responses are logged with
their body, so 500s from compute.geometry are diagnosable from the parent log.

### 11. Solve-result cache purge frees native geometry (SELVA FIX)
**File:** `src/compute.geometry/DataCache.cs` — `PurgeSolveResults`

`resultsCache.Trim(100)` is best-effort (it may leave entries behind) and drops only the managed
reference. URL-data entries hold a `Tuple<JToken, object>` whose `Item2` may be a `GeometryBase`
wrapping unmanaged C++ memory, so the purge did not actually relieve memory pressure. Now
enumerates and removes every key explicitly and disposes any `IDisposable` payload.

### 12. Linux Grasshopper plugin loading — diagnostics + quarantine (SELVA FIX, 9.x-only)
**File:** `src/compute.geometry/Startup.cs` (`#if LINUX` block)

Two problems, both previously silent:

- **Silent failure.** Every failure mode in the Linux GH load path was unlogged. A missed load
  meant definitions solved with core components only and no plugin outputs, surfacing much later
  as an opaque "no outputs" failure. Each step is now logged, including load result, loading
  exceptions and the list of loaded libraries.
- **Rhino 9 WIP bug.** `PlugIn.GetMultiTargetPath` → `HostUtils.GetRuntimeSpecificFolder` throws
  on Linux (the OS suffix is null, and `string.IndexOf(null)` throws) for any plugin laid out with
  TFM subfolders (`MyPlugin/net7.0/MyPlugin.gha`). `GH_ComponentServer.ExternalFiles` has no
  per-file try/catch, so **one** such file aborted the whole scan and no plugins loaded at all.
  Offenders are quarantined with a `.no9` marker (GH skips marked files) and warned about loudly.

Note: external libraries are loaded by touching the `Instances.ComponentServer` getter, which runs
GH's own guarded once-only load. Do **not** call `LoadExternalFiles()` as well — that loads every
assembly twice and floods the component server with object-ID conflicts.

### 13. `System.Management` package reference (9.x-only)
**File:** `src/compute.geometry/compute.geometry.csproj`

Added under the Windows-only `ItemGroup`.

---

## Non-source divergence

Tooling and docs with no upstream counterpart:

- `setup/` — Docker/Linux deployment: `Dockerfile`, `docker-launch.{ps1,sh}`, `docker-status.sh`,
  `start.sh`, Multipass launchers and guides, `infrastructure/` (Terraform), `testing/`
  (startup-timing and comparison scripts), `plugins/`, `.env.example`, `packages.example.json`.
- `docs/quick-start-docker.md`, `docs/grasshopper-plugins-not-loading-linux.md`.
- `COMPUTE8_DIFFERENCES.md` — an older, auto-generated comparison of the Compute8 branch against
  `upstream/8.x`. Superseded by this file and by `8.x.selva`'s own `FORK_CHANGES.md`; kept for
  history, not maintained.
- `.gitattributes`, `.gitignore` additions — local conventions.
- `src/compute.geometry/compute.geometry.csproj` — `<Version>` (currently 9.3.0).

### Known cruft

- `src/GrasshopperValidationHelpers.cs` — a 109-line duplicate of
  `src/compute.geometry/GrasshopperValidationHelper.cs` (248 lines), sitting outside the project
  directory so SDK globbing never compiles it. Dead file; safe to delete.

---

## Rule: document every upstream divergence

**Any change that makes this fork differ from upstream must be recorded here, in the same commit
that makes the change.** No exceptions for "small" or "temporary" changes — those are exactly the
ones that get forgotten and then cost an hour during the next upstream merge.

For a source change:

1. Tag it in source with a `VEKTORNODE: <TAG> — <why>` comment (BEGIN/END banners for blocks).
2. Add or update a numbered section here saying **what upstream did, why that was wrong or
   insufficient for us, and what we do instead.** The "why" is the part that has value later;
   the diff already shows the "what".
3. If the same behaviour applies to the other branch (`8.x.selva`), either port it or note
   explicitly that it is branch-specific.

For tooling, scripts or docs with no upstream counterpart, add them under
**Non-source divergence**.

When removing a divergence — because upstream adopted it, or we no longer need it — delete its
section here in the same commit.

### After merging upstream

Re-audit, because a merge can silently make one of our changes redundant:

```
git fetch upstream
git diff --ignore-all-space --stat upstream/9.x 9.x.selva
grep -rn "VEKTORNODE" src/
```

Every file in that diff should map to a section below, and every `VEKTORNODE` marker should
correspond to one. If upstream has adopted something we carried, drop our version rather than
keeping a duplicate.
