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

### 12. Linux Grasshopper plugin loading — diagnostics + quarantine (SELVA FIX)
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

### 13. `System.Management` package reference
**File:** `src/compute.geometry/compute.geometry.csproj`

Added under the Windows-only `ItemGroup`.

### 14. Live-event callback target as document constants (SELVA)
**Files:** `src/compute.geometry/IO/Schema.cs` (`SelvaEventTarget`, `Schema.SelvaEvents`),
`src/compute.geometry/ResthopperEndpoints.cs` (`SelvaEventTargetFor`, the `DefineConstant` block
next to `ComputeRecursionLevel`), `src/compute.geometry/Config.cs` (`EventSinkHosts`)

Upstream has no way for a component to talk to anyone during a solve: the solve is one blocking
`NewSolution` inside one HTTP request, and the parent proxy buffers the whole response. Selva
wants mid-solve events (diagnostics, progress, an abort flag coming back) without streaming
through the proxy. So the solve request may carry
`selvaevents: { url, solveId, token }`, and compute hands the three values to the document as
constants (`SelvaEventUrl`, `SelvaSolveId`, `SelvaEventToken`) the same way it already hands
`ComputeRecursionLevel`. The Selva plugin running inside the solve reads them and does the
networking; compute itself never opens a connection.

Two rules carry the safety:

- The constants are defined on **every** request, as empty strings when the block is absent.
  Cached definitions are live `GH_Document`s reused across requests; a leftover constant would
  send one caller's events to another caller's callback.
- The URL's host must be in `RHINO_COMPUTE_EVENT_SINK_HOSTS`, which defaults to loopback
  (`localhost,127.0.0.1,[::1]`) and nothing else. A same-machine Selva server therefore works
  out of the box, while any sink that is not this host — a LAN address included — stays denied
  until an operator lists it. Setting the variable replaces the default rather than extending
  it. Only an API-key holder can send a solve request, so this is defence in depth against a
  leaked key becoming an SSRF hop.

### 15. Warnings reported outside Debug (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `LogRuntimeMessages`

Upstream only collects `GH_RuntimeMessageLevel.Warning` into the response's `warnings[]` when
`Config.Debug` is on, so a production server never reports a warning at all. Selva shows
warnings to the user and lets them decide whether to trust the result, which is meaningless if
the array is always empty. Warnings are now collected unconditionally; the Serilog line for them
stays behind `Debug` so production logs are not flooded. Remarks are unchanged (log only).

---

## Non-source divergence

Tooling and docs with no upstream counterpart:

- `setup/` — Docker/Linux deployment: `Dockerfile`, `docker-launch.{ps1,sh}`, `docker-status.sh`,
  `start.sh`, Multipass launchers and guides, `infrastructure/` (Terraform), `testing/`
  (startup-timing and comparison scripts), `plugins/`, `.env.example`, `packages.example.json`.
- `docs/quick-start-docker.md`, `docs/grasshopper-plugins-not-loading-linux.md`.
- `COMPUTE8_DIFFERENCES.md` — a stale leftover from an older branch; not maintained.
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
