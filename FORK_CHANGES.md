# Fork changes — Vektornode / Selva vs. upstream `compute.rhino3d` (8.x)

This is a **fork** of McNeel's [compute.rhino3d](https://github.com/mcneel/compute.rhino3d),
branch `8.x.selva`, tracking `upstream/8.x`.

**Every divergence from upstream must be documented here.** See
[Rule: document every upstream divergence](#rule-document-every-upstream-divergence).
Source changes are also tagged with a `VEKTORNODE:` comment so they can be found by grep.

> **Find every change in source:**
> ```
> grep -rn "VEKTORNODE" src/
> ```
> Block-level changes are wrapped in banners:
> ```
> // ── BEGIN VEKTORNODE: SELVA FIX — <topic> ──
> ...
> // ── END   VEKTORNODE: SELVA FIX — <topic> ──
> ```

Tags used:
- **SELVA** — behavior/feature added for the Selva product.
- **SELVA FIX** — a bug fix relative to upstream (wrapped in BEGIN/END banners).
- **PARAM-ID** — threads the source Grasshopper parameter's Instance Guid through the IO model.
- **IO-HANDLERS** — extra input/output type handlers + schema metadata not in upstream.
- **CACHE-ERRORED-SOLVES** — opt-in caching of errored-but-completed solves.

> **Auditing the delta against upstream:** compare with
> `git diff --ignore-all-space upstream/8.x 8.x.selva`. A plain `git diff` also
> reports files that differ only in line endings — see the note in
> `.gitattributes` for why.

> **Relationship to 9.x:** `9.x.selva` carries its own `FORK_CHANGES.md`. Most items below exist
> on both branches; the Linux/Docker work is 9.x-only. When you change shared behaviour, check
> whether the other branch needs the same change and document it there too.

---

## Changes

### 1. Contextual-geometry struct deserialization (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `DeserializeGeometry` + `TryDeserializeStruct`
**Branch:** `fix/contextual-geometry-struct-deserialization`

Upstream `DeserializeGeometry` assumed geometry inputs always arrive as a Rhino **archive**
dictionary (`{version, archive3dm, ...}`) and called `CommonObject.FromJSON` only. But struct
geometry — **Circle, Arc, Line** — has no archive form; our own output serializer emits it as
**property JSON** (`{"Radius":…,"Plane":…,…}`), which `FromJSON` cannot rehydrate → it returned
null → zero geometry / failed solve (e.g. a `Get Geometry` input fed a Circle).

Fix: try the archive shape first, then fall back to coercing known curve-like structs to a
`Curve` (`Circle`/`Arc` → `ArcCurve`, `Line` → `LineCurve`). Mirrors the existing
`DeserializeCurve` try-then-fallback pattern. **Input-side only** — the output serializer is
left unchanged so consumers that already parse the struct property JSON keep working.
To cover more structs later (Rectangle3d, Box, …) add a line in `DeserializeGeometry`.

### 2. Multipart proxy passthrough (SELVA FIX)
**File:** `src/rhino.compute/ReverseProxy.cs` (~L312)

Upstream read every POST body as a string and re-sent it as `application/json`, destroying the
multipart boundary — so `grasshopper/validate` and other file uploads broke behind IIS. Fix:
detect `multipart/form-data` and stream the body through as-is, preserving boundary + binary
content and forwarding `Content-Length`.

### 3. Schema-extraction endpoints (SELVA)
**Files:** `src/compute.geometry/FixedEndpoints.cs` (~L27), `src/compute.geometry/GrasshopperValidationHelper.cs` (entire file)

Adds the `grasshopper/schema` family of endpoints (definition IO + validation) used by Selva to
introspect a definition's inputs/outputs. Not present in upstream/8.x.

### 4. Headless doc creation default ON (SELVA)
**File:** `src/compute.geometry/Config.cs` (~L110)

Default for headless Rhino document creation flipped to **on** (upstream default is `false`).

### 5. Parameter-Id threading (PARAM-ID)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs` (~L625), `src/compute.geometry/IO/Schema.cs` (L68, L131)

Every emitted `ResthopperObject` is tagged with its source Grasshopper parameter's Instance Guid
(`ResthopperObject.Id` / `IoParamSchema.Id`) so the client can demux outputs by parameter.

### 6. Extra IO handlers + metadata (IO-HANDLERS)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs` (~L719, Color output), `src/compute.geometry/IO/Schema.cs` (L84)

Additional output type handler(s) (e.g. Color) and extra input metadata (UI grouping +
enumerated values) carried on the schema. Not in upstream/8.x.

### 7. Selva serializable-goo SDK seam (SELVA)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` (~L641, and a DEPRECATED block ~L659)

Any Goo implementing `ISelvaSerializableGoo` (matched by interface name) owns its own wire
format. The block at ~L659 is marked **DEPRECATED — delete in a future major** (legacy
serialization of older Selva output Goos).

### 8. Dead-child eviction + proxy retry (SELVA FIX)
**Files:** `src/rhino.compute/ComputeChildren.cs` (`EvictChild`), `src/rhino.compute/ReverseProxy.cs`
(`/child-exiting` endpoint, retry loop in `ProxyRequest`), `src/compute.geometry/Shutdown.cs`
(`NotifyParentExiting`)

A child that had self-exited on idle timeout stayed in the round-robin pool, so every request
routed to it failed with "connection refused" until a manual `/shutdown-children`. Three parts:
a child POSTs `/child-exiting` just before it stops listening; the parent evicts that port from
the pool; and the proxy evicts-and-retries on `HttpRequestError.ConnectionError`. The retry only
fires when the connection was never established, so no solve is ever duplicated.

Also logs the child's response body on any non-success status, so 500s from compute.geometry are
diagnosable from the parent log rather than only forwarded to the caller.

### 9. Solve-result cache purge frees native geometry (SELVA FIX)
**File:** `src/compute.geometry/DataCache.cs` — `PurgeSolveResults`

`resultsCache.Trim(100)` is best-effort (it may leave entries behind) and drops only the managed
reference. URL-data entries hold a `Tuple<JToken, object>` whose `Item2` may be a `GeometryBase`
wrapping unmanaged C++ memory, so the purge did not actually relieve memory pressure. Now
enumerates and removes every key explicitly and disposes any `IDisposable` payload.

### 10. Errored-solve caching, opt-in (CACHE-ERRORED-SOLVES)
**Files:** `src/compute.geometry/IO/Schema.cs`, `src/compute.geometry/ResthopperEndpoints.cs`

By default an errored solve is not served from cache; this adds the opt-in flag plus the
detection of whether a cached solve-result JSON came from an errored-but-completed solve.

### 11. Stable error code on 500s (SELVA)
**File:** `src/compute.geometry/Startup.cs`

Adds a machine-readable `code` to the error body in **both** debug and production. The human
`message` is scrubbed in prod, so the code is the only thing a client can classify on. A
stale-pointer cache miss surfaces as `definition_not_cached`, which the `@selvajs/compute`
client uses to transparently re-upload the definition.

### 12. Larger default max request size (SELVA)
**File:** `src/compute.geometry/Config.cs`

`RHINO_COMPUTE_MAX_REQUEST_SIZE` default raised from 50 MB to 300 MB.

### 13. Live-event callback target as document constants (SELVA)
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

### 14. Warnings reported outside Debug (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `LogRuntimeMessages`

Upstream only collects `GH_RuntimeMessageLevel.Warning` into the response's `warnings[]` when
`Config.Debug` is on, so a production server never reports a warning at all. Selva shows
warnings to the user and lets them decide whether to trust the result, which is meaningless if
the array is always empty. Warnings are now collected unconditionally; the Serilog line for them
stays behind `Debug` so production logs are not flooded. Remarks are unchanged (log only).

---

## Non-source divergence

Repo/tooling files with no upstream counterpart, or deliberately changed:

- `.editorconfig`, `.gitattributes`, `.vscode/settings.json`, `.gitignore` additions — local
  conventions.
- `.github/workflows/workflow_ci.yml` — artifact upload gated on `8.x.selva` instead of `8.x`.
- `script/update_compute_server/` — Selva deployment script + README.
- `TROUBLESHOOTING-DUPLICATE-LANGUAGE.md` — runbook for the RhinoCode duplicate-language 500.
- `src/compute.geometry/compute.geometry.csproj` — `<Version>`.

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
3. If the same behaviour applies to the other branch (`9.x.selva`), either port it or note
   explicitly that it is branch-specific.

For tooling, scripts or docs with no upstream counterpart, add them under
**Non-source divergence**.

When removing a divergence — because upstream adopted it, or we no longer need it — delete its
section here in the same commit.

### After merging upstream

Re-audit, because a merge can silently make one of our changes redundant:

```
git fetch upstream
git diff --ignore-all-space --stat upstream/8.x 8.x.selva
grep -rn "VEKTORNODE" src/
```

Every file in that diff should map to a section above, and every `VEKTORNODE` marker should
correspond to one. If upstream has adopted something we carried, drop our version rather than
keeping a duplicate. Then bump `<Version>` in `src/compute.geometry/compute.geometry.csproj`
(the only place the version lives).
