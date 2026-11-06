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

> **Auditing the delta against upstream:** use
> `git diff --ignore-all-space upstream/9.x 9.x.selva`. A plain `git diff` also reports files
> that differ only in line endings, which overstates the delta.

---

## For McNeel: candidates for upstream

What we hit running upstream `9.x` on Linux (Docker) behind `rhino.compute`, and what we changed.
§ numbers point to the detail under [Source changes](#source-changes); the code carries a
`VEKTORNODE: SELVA FIX` marker, and the whole delta is
[`9.x...VektorNode:9.x.selva`](https://github.com/mcneel/compute.rhino3d/compare/9.x...VektorNode:compute.rhino3d:9.x.selva).
An entry goes when its section goes.

**Compute fixes worth taking over**

- **§8 A stopping child stays in the pool.** `SelectChild` prunes only children whose process has
  exited, so one that has closed its listener on idle shutdown (or is alive with a dead listener)
  keeps getting requests, which fail with "connection refused". _Fix:_ when a connection cannot be
  made, the proxy evicts that child and retries on another. Nothing was sent, so no solve runs
  twice.
- **§8 Child errors are missing from the parent log.** A child's non-2xx body reaches only the
  caller. _Fix:_ the parent logs status and (truncated) body.
- **§15 Idle shutdown runs late.** A child asks `/idlespan` once per span, so it outlives the span
  by up to another span: a 5-minute span runs 5–10 minutes. _Fix:_ ask at least every 30 s.
- **§9 `POST /cache/purge` leaves entries behind.** `MemoryCache.Trim(100)` is best-effort.
  _Fix:_ remove every key.
- **§6 Undo recording kills the child on Linux.** With `RHINO_COMPUTE_CREATE_HEADLESS_DOC` on,
  `NewSolution` calls `RhinoDoc.BeginUndoRecord`, which on Linux invokes a GC'd
  `Command.UndoCallback`; on any platform the recorder keeps every solve's state. _Fix:_
  `UndoRecordingEnabled = false` on the headless doc.
- **§10 Plugin load failures are silent on Linux.** Definitions then solve with core components
  only (cause: the first Rhino bug below). _Fix:_ log `ComponentServer.LoadingExceptions`, and
  quarantine GHAs that crash the scan before it runs.
- **§14 Chunked 500s.** `rhino.compute` sends an uncompressed answer chunked, and Node's HTTP
  client hangs on a chunked 500 until its timeout. Chunked is valid HTTP, so this is client
  compatibility. _Fix:_ the proxy sets `Content-Length`.

**Possible opt-ins**

- **§12** Warnings reach `warnings[]` only when `Config.Debug` is on, so a production client never
  sees one. An env flag to return them would cover it.
- **§7** A 500 body has no machine-readable code, and its message is scrubbed outside Debug. A
  `code` field, with a distinct `definition_not_cached` for a pointer that no longer resolves,
  lets a client re-upload the definition instead of matching on text.

**Not fixed here**

- `rhino.compute`'s `/healthcheck` is `AddHealthChecks()` with no checks, so it reports healthy
  while every child dies at startup.

**Rhino / Grasshopper on Linux (outside compute; worked around here)**

- `HostUtils.GetRuntimeSpecificFolder` passes a `null` OS suffix to `string.IndexOf` on Linux and
  throws, for any GHA in a `netX.0` subfolder. `GH_ComponentServer.ExternalFiles` has no per-file
  try/catch, so one such GHA stops every plugin from loading, and the exception goes to a
  `MessageBox` that never shows headless. Write-up:
  [docs/grasshopper-plugins-not-loading-linux.md](docs/grasshopper-plugins-not-loading-linux.md).
- Loading a GHA built against an older RhinoCommon major runs
  `PlugIn.CheckGrasshopperAssemblyCompatibility` → `RunCompat` → `DebugStringSafe` →
  `RhinoApp.InvokeRequired`, a Linux stub that throws `RhinoFailException` and terminates
  compute.geometry. The `rhino3d` build where this starts is named at the `RHINO3D_VERSION` pin in
  [setup/Dockerfile](setup/Dockerfile).
- `Curve.ClosestPoints` reaches the native `RHC_RhinoGetClosestPoint`, a Linux stub (`LINUXFAIL`
  in `RhinoCore_linux_app.cpp`) that calls `std::terminate`: the child dies with no managed
  exception, and the caller gets a 500 "response ended prematurely".
- **§20** GH_IO's XML reader (`GH_Item.ReadXmlData`) decodes every `gh_drawing_bitmap` with
  `new Bitmap(stream)`, and the `System.Drawing.Common` Rhino ships on Linux throws
  `PlatformNotSupportedException`. The binary reader already skips that decode. Every saved `.ghx`
  carries an archive thumbnail, so no `.ghx` loads.

---

## Source changes

### 1. Schema-extraction endpoints (SELVA)

**Files:** `src/compute.geometry/FixedEndpoints.cs`, `src/compute.geometry/GrasshopperValidationHelper.cs` (entire file),
`src/compute.geometry/GrasshopperDefinition.cs` (`ArchiveFromBytes` made `internal`)

Adds the `grasshopper/schema` endpoints Selva uses to read the UI Builder schema embedded in a
`.gh`/`.ghx`, uploaded or fetched from a URL or local path. Not present in upstream. Files load
through upstream's `ArchiveFromBytes`/`ArchiveFromUrl`, so its BOM strip, #20 and `UrlGuard`
(private-URL block, `MaxRequestSize` cap) apply as they do to a solve. Same endpoints as
`8.x.selva`.

### 4. Parameter-Id threading + Selva IO types (PARAM-ID, IO-HANDLERS)

**Files:** `src/compute.geometry/GrasshopperDefinition.cs`, `src/compute.geometry/IO/Schema.cs`

Every emitted `ResthopperObject` is tagged with its source Grasshopper parameter's Instance Guid
so the client can demux outputs by parameter. The tag is set once after upstream's goo `switch`
in `SerializeDataTree`, not passed through each arm, so that switch stays identical to upstream's
and merges cleanly. Adds Selva contextual inputs, hierarchical group names, enumerated values,
and per-parameter declared defaults on the schema.

- **Output types.** Upstream's switch silently drops any goo it does not list. We add
  `GH_Colour`, any goo implementing `ISelvaSerializableGoo` (matched by interface name, so compute
  needs no reference to a Selva assembly), and a deprecated name-match arm for Selva plugin builds
  that predate that interface.
- **Contextual inputs.** Upstream's `SetValues` assigns a contextual param only when its
  `TypeName` is one it lists, and silently drops the rest. A default arm gives any contextual
  param whose `AssignContextualDataTree` takes `DataTree<GH_String>` its values as text, through
  the same `DeserializeText` as `Text`; the param parses them itself. Selva's Color, File, Image
  and Value List params work this way, so a new Selva input type needs no fork change. A
  deprecated fallback calls `SetValues(List<string>)` for Selva value lists from plugin builds
  that predate the text-tree signature.
- **Output Id.** An output's Id is its top-level object's Instance Guid, i.e. the Context Bake
  component Selva keys outputs by; a floating `RH_OUT` param is its own. `/io` and the solve carry
  the same Id.
- **`/io` wire names.** Upstream's PascalCase is kept; only the fields we add are camelCase
  (`id`, `groupName`, `values`), as on `8.x.selva`.

### 6. Undo recording forced off server-side (SELVA FIX)

**File:** `src/compute.geometry/ResthopperEndpoints.cs`

Only when `RHINO_COMPUTE_CREATE_HEADLESS_DOC` is on (upstream's default, off, is kept). With an active
doc, `GH_Document.NewSolution` calls `RhinoDoc.BeginUndoRecord`, which in headless Linux Rhino invokes
a GC'd `Command.UndoCallback` delegate and kills the child, and the recorder retains every solve's
state. Undo is turned off on the document the solve creates.

### 7. Stable error code on 500s (SELVA)

**Files:** `src/compute.geometry/Startup.cs`, `src/compute.geometry/ResthopperEndpoints.cs` (`DefinitionNotCachedException`)

Adds a machine-readable `code` to the error body in **both** debug and production. The human
`message` is scrubbed in prod, so the code is the only thing a client can classify on. A
stale-pointer cache miss (a `pointer` that no longer resolves and no `algo` to fall back on)
surfaces as a 500 with `definition_not_cached`, which the `@selvajs/compute` client uses to
transparently re-upload the definition. Upstream answers every failed definition load with a
400 `BadRequestException`; that stays for everything except the stale pointer, and its body
carries `code: "bad_request"`.

### 8. Dead-child eviction + proxy retry (SELVA FIX)

**Files:** `src/rhino.compute/ComputeChildren.cs` (`EvictChild`), `src/rhino.compute/ReverseProxy.cs`
(retry loop)

`SelectChild` prunes only exited processes, so a child that had closed its listener (idle
shutdown, or alive with a dead listener) stayed in the pool and every request routed to it failed
with "connection refused" until a manual `/shutdown-children`. The proxy now evicts that child and
retries on another on `HttpRequestError.ConnectionError`, which fires only when no connection was
made, so no solve runs twice. The evicted child is killed unless another request still holds a
lease on it. Each attempt takes and releases its own `AcquireChild` lease, and the request body,
which upstream already reads in full, is read once in the handler so every attempt re-sends the
same bytes. Non-success child responses are logged with their body, so 500s from compute.geometry
are diagnosable from the parent log.

### 9. Solve-result cache purge frees native geometry (SELVA FIX)

**File:** `src/compute.geometry/DataCache.cs` — `PurgeSolveResults`

`resultsCache.Trim(100)` is best-effort and may leave entries behind. Every key is now removed.
Payloads are not disposed: a concurrent solve may still use the cached geometry.

### 10. Linux Grasshopper plugin loading — diagnostics + quarantine (SELVA FIX)

**Files:** `src/compute.geometry/LinuxGhPlugins.cs` (entire file), `src/compute.geometry/Startup.cs`
(one call after `RunHeadless`)

Upstream logs each loaded assembly. Two problems remain, both silent:

- **Load errors.** GH reports a library load failure in a `MessageBox` that never shows headless,
  so definitions solved with core components only and the cause surfaced much later as "no
  outputs". `ComponentServer.LoadingExceptions` is now logged.
- **Rhino 9 WIP bug.** `PlugIn.GetMultiTargetPath` → `HostUtils.GetRuntimeSpecificFolder` throws
  on Linux (the OS suffix is null, and `string.IndexOf(null)` throws) for any plugin laid out with
  TFM subfolders (`MyPlugin/net7.0/MyPlugin.gha`). `GH_ComponentServer.ExternalFiles` has no
  per-file try/catch, so **one** such file aborted the whole scan and no plugins loaded at all.
  Offenders are quarantined with a `.no9` marker (GH skips marked files) and warned about loudly.

Note: external libraries are loaded by touching the `Instances.ComponentServer` getter, which runs
GH's own guarded once-only load. Do **not** call `LoadExternalFiles()` as well — that loads every
assembly twice and floods the component server with object-ID conflicts.

### 11. Live-event callback target as document constants (SELVA)

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

### 12. Warnings reported outside Debug (SELVA FIX)

**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `LogRuntimeMessages`

Upstream only collects `GH_RuntimeMessageLevel.Warning` into the response's `warnings[]` when
`Config.Debug` is on, so a production server never reports a warning at all. Selva shows
warnings to the user and lets them decide whether to trust the result, which is meaningless if
the array is always empty. Outside Debug the warnings are added to the response ahead of
upstream's block, which is left as it is (it adds them itself in Debug and logs them per
`RHINO_COMPUTE_LOG_LEVEL`). Remarks are unchanged (log only).

### 13. Solve outcome returned as a `selva` block (SELVA)

**Files:** `src/compute.geometry/IO/Schema.cs` (`Schema.Selva`),
`src/compute.geometry/ResthopperEndpoints.cs` (`SelvaOutcome` next to the #11 constants),
`src/compute.geometry/GrasshopperDefinition.cs` (`Solve`, `ReadSelvaOutcome`)

Compute flattens runtime messages to strings, so Selva used to recover "a Message component
refused this solve" and "the author wrote this" from markers in the text, and could not report an
aborted solve or a remark at all. Now the Selva plugin writes its verdict,
`{ diagnostics, blocked, aborted }`, as JSON into a `SelvaOutcome` document constant at
`SolutionEnd`, and compute returns it verbatim as `selva: { outcome }` in the solve response.
Compute never interprets the messages; the plugin decides.

- `SelvaOutcome` is defined empty on **every** request, like the #11 constants, so a cached
  document cannot return the previous solve's verdict.
- When the outcome says `blocked` or `aborted`, `values` is returned empty and the
  "No outputs found" check is skipped: the client discards those outputs, and serializing them
  cost megabytes of display data for nothing. `errors`/`warnings` and the HTTP status are
  unchanged, so a client that ignores `selva` still sees the markers.
- No Selva plugin, an older one, or a definition without a Selva component: no constant is
  written, no `selva` block, behaviour as before.

### 14. Proxied responses carry a Content-Length (SELVA FIX)

**File:** `src/rhino.compute/ReverseProxy.cs` — `ReverseProxyHandler`

The parent is what a caller sees, and it writes the child's answer with `WriteAsync`, so an answer
that is not gzipped goes out chunked. The Node client fails to read a chunked 500 and hangs until
its timeout. The proxy sets `ContentLength` before writing; with gzip, upstream's compression
middleware replaces it with the compressed length.

### 15. Idle shutdown on time (SELVA FIX)

**File:** `src/compute.geometry/Shutdown.cs` — `TimerTask`

Upstream asks the parent for its idle time once per `idlespan`, so a child outlives the span by
up to a whole span, and core-hour billing runs for all of it: a 5-minute span billed 5–10. The
child now asks at least every `MaxSpanCheckSeconds` (30), so it exits within 30 s of the span.

### 16. Solve timing reaches the caller (SELVA)

**Files:** `src/compute.geometry/ResthopperEndpoints.cs` — `GrasshopperSolveHelper`, `Grasshopper`;
`src/compute.geometry/GrasshopperDefinition.cs` — `Solve`, `ProfileJson`;
`src/compute.geometry/Config.cs` — `RHINO_COMPUTE_PROFILE_TOP`;
`src/rhino.compute/ReverseProxy.cs` — `ForwardTiming`

Upstream's child sends `Server-Timing: decode, solve, encode`, but the proxy copies only
`Content-Type`, so a caller behind `rhino.compute` never sees it; and `solve` lumps the
Grasshopper run together with harvesting the outputs. The proxy now forwards the child's
`Server-Timing` and `X-Compute-Profile` and adds its own `proxy_init` (startup), `proxy_read`
(request body), `proxy_wait` (getting a child, a spawn included) and `proxy_child` (the child's
round trip). The child adds `gh` (`NewSolution`) and `harvest` beside `solve`, which stays their
sum for clients that read only the three upstream keys; a cache hit answers `cache;desc="hit"`.
`X-Compute-Profile` names the solve's slowest objects by `ProcessorTime` as escaped-ASCII JSON,
`RHINO_COMPUTE_PROFILE_TOP` of them (default 5, `0` turns it off).

---

### 17. Raw JSON on a contextual Text input passes through (SELVA FIX)

**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `DeserializeText`, `IsJsonContainer`

Upstream reads a contextual Text (and Color, File) value as a JSON string literal and, when that
throws, falls back to `Regex.Unescape` on the raw text. Selva hosts send JSON objects as the text
itself (`{"process":"powder",…}`, vektor-fab's `Abschlüsse`), so they always hit the fallback, and
`Regex.Unescape` rewrites their escapes: a `\"` inside a string becomes a bare quote and the JSON
breaks; `\\` and `\n` change meaning. A value that parses as a JSON object or array is now passed
through unchanged. A JSON string literal still unwraps as before; anything else (an escaped,
unquoted payload that is not valid JSON) still goes through `Regex.Unescape`.
Same change as `8.x.selva` #16.

### 20. A `.ghx` loads on Linux (SELVA FIX)

**Files:** `src/compute.geometry/LinuxGhx.cs` (entire file); `src/compute.geometry/GrasshopperDefinition.cs` —
`ArchiveFromUrl`, `ArchiveFromXml`

On Linux every `.ghx` failed to load, in solves and in `grasshopper/schema` alike, with
"System.Drawing.Common is not supported on non-Windows platforms": GH_IO decodes the archive's
`Thumbnail` chunk while parsing (see the Linux list above). The Linux build now removes that chunk
before GH_IO parses the XML. Nothing reads it when the `Definition` chunk is extracted. Local `.ghx`
paths go through the same step instead of `GH_Archive.ReadFromFile`, which parses the file itself.
A bitmap a component stores inside the definition still throws; no Selva component stores one.
The Windows build is unchanged.

### 21. Limits a client can read (SELVA)

**Files:** `src/rhino.compute/ReverseProxy.cs` — `MapEndpoints`; `src/compute.geometry/FixedEndpoints.cs` — `GetLimits`

Upstream gives a client no way to learn the server's timeout or body cap, so Selva carried its own
copies of both, and an operator had to keep two sets of numbers in step. A copy set higher than the
server's ended in a bare 413 or 502. `GET /limits` returns `timeoutSeconds` (`RHINO_COMPUTE_TIMEOUT`
or `--timeout`), `maxRequestBytes` (`RHINO_COMPUTE_MAX_REQUEST_SIZE` or `--max-request-size`) and
`signalsStaleDefinition` (a stale definition pointer answers `definition_not_cached`, see
§7). `rhino.compute` answers it itself, because its own timeout and body cap are what
a request meets first; a child reached directly answers with its own config.

`deploymentId` is new on every process start and, in `rhino.compute`, on `/shutdown-children` or
`/recycle-children`: the ways a plugin is redeployed. Selva drops cached solve results when it
changes. A plugin swapped on disk while children sit idle-exited is not seen.

---

## Non-source divergence

Tooling and docs with no upstream counterpart:

- `setup/` — Docker/Linux deployment: `Dockerfile`, `.dockerignore`, `docker-launch.{ps1,sh}`,
  `docker-status.sh`, `start.sh`, `deploy-server.sh`,
  `env-lib.sh` (parses `.env` instead of sourcing it, so `setup/.env` can be a symlink to a file
  shared with other tools), `server-init.sh`, Multipass launchers and guides, `testing/` (startup-timing and comparison
  scripts), `plugins/`, `.env.example`, `packages.example.json`. The `Dockerfile` sets
  `RHINO_COMPUTE_MAX_REQUEST_SIZE=314572800` (formerly a source default); unlike the source
  default, it also raises rhino.compute's own limit.
- `docs/quick-start-docker.md`, `docs/grasshopper-plugins-not-loading-linux.md`.
- `CLAUDE.md` — agent instructions for this fork.
- `.gitattributes`, `.gitignore` additions — local conventions.
- `src/compute.geometry/compute.geometry.csproj` — `<Version>`, bumped on every upstream merge.

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
