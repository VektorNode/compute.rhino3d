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

> **Auditing the delta against upstream:** compare with
> `git diff --ignore-all-space upstream/8.x 8.x.selva`. A plain `git diff` also
> reports files that differ only in line endings — see the note in
> `.gitattributes` for why.

> **Relationship to 9.x:** `9.x.selva` carries its own `FORK_CHANGES.md`. Most items below exist
> on both branches; the Linux/Docker work is 9.x-only. When you change shared behaviour, check
> whether the other branch needs the same change and document it there too.

---

## Changes

### 2. Proxy forwards request bodies as received (SELVA FIX)
**File:** `src/rhino.compute/ReverseProxy.cs` — `SendProxyRequest`, `ProxyRequest`

Upstream read every POST body as a string and re-sent it as `application/json`, destroying a
multipart boundary, so file uploads (`POST grasshopper/schema`, §3) broke behind the proxy. The
body is now read once in `ProxyRequest` as bytes (so a §8 retry re-sends the same bytes) and
forwarded with the caller's `Content-Type`, `application/json` when it sent none, as upstream 9.x
does. It is read from `Body`, not `BodyReader`: with upstream's gzip request decompression on,
only `Body` is the decompressed stream.

### 3. Schema-extraction endpoints (SELVA)
**Files:** `src/compute.geometry/FixedEndpoints.cs`, `src/compute.geometry/GrasshopperValidationHelper.cs` (entire file),
`src/compute.geometry/GrasshopperDefinition.cs` (`ArchiveFromBytes` made `internal`)

Adds the `grasshopper/schema` family of endpoints used by Selva to read the UI Builder schema
embedded in a definition. Not present in upstream/8.x. Definitions are loaded with upstream's
`ArchiveFromUrl` / `ArchiveFromBytes`, so URL fetches keep `UrlGuard`, `BLOCK_PRIVATE_URLS` and the
`MaxRequestSize` cap.

### 5. Parameter-Id threading (PARAM-ID)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs` (`OutputId`, `SerializeDataTree`, `GetInputsAndOutputs`),
`src/compute.geometry/IO/Schema.cs` (`ResthopperObject.Id`, `IoParamSchema.Id`)

Every emitted `ResthopperObject` and every `/io` entry carries an Id so the client can demux
outputs. An input's Id is its param; an output's Id is the param's top-level object
(`OutputId`): the Context Bake component for a baked output, which is what the Selva client
matches `schema.outputs[].id` on, or the param itself when it floats in an `RH_OUT` group
(`Attributes.Parent` is null there, which used to throw).

### 6. Extra IO handlers + metadata (IO-HANDLERS)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs` (`SetInputs` default arm, `AcceptsTextTree`,
`AssignViaSetValues`, the `GH_Colour` output arm, `InputGroup.GetDefault/GetGroupName/GetValues`),
`src/compute.geometry/IO/Schema.cs` (`GroupName`, `Values`)

- **Contextual inputs of other types.** Upstream drops a contextual param whose TypeName it does
  not list. One whose `AssignContextualDataTree` takes `DataTree<GH_String>` now gets its values
  as text and parses them itself (Selva's Color, File, Image, ValueList), so a new type needs no
  fork change. Older Selva value lists without that overload get a flat string list through
  `SetValues` (deprecated).
- **Color output.** `GH_Colour` is serialized; upstream skips it.
- **Custom defaults.** `/io` reports a contextual param's `GetDefaultValue()` (found by reflection)
  when it has one; upstream reports the collected data tree, which is empty for these params.
- **Input metadata.** `/io` inputs carry `groupName` (the nested named groups around the param,
  `RH_IN` groups excluded, joined with `::`) and `values` (a contextual param's `Values`
  dictionary) for Selva's UI.

### 7. Selva serializable-goo SDK seam (IO-HANDLERS)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — arms in `SerializeDataTree`'s output
switch, helpers after `LogRuntimeMessages`

Any Goo implementing `ISelvaSerializableGoo` (matched by interface name) owns its own wire format.
A second arm, marked **DEPRECATED — delete in a future major**, serializes older Selva output Goos
by type name. Both are arms of upstream's switch, so its loop body is unchanged apart from tagging
the §5 Id once after it.

### 8. Dead-child eviction + proxy retry (SELVA FIX)
**Files:** `src/rhino.compute/ComputeChildren.cs` (`EvictChild`), `src/rhino.compute/ReverseProxy.cs`
(retry loop in `ProxyRequest`)

A child can idle-exit, or lose its listener while its process lives, just as a request picks it;
the request then fails with "connection refused". Upstream (8.x, Oct 2026) narrowed this
(startup health check, exited processes pruned on every pick, `IdleSpan` 0 while a request is in
progress) but a live process with a dead listener passes the prune. The proxy now
evicts-and-retries on `HttpRequestError.ConnectionError` only: the connection was never
established, so no solve is ever duplicated. Each attempt holds upstream's `ChildLease`.
`EvictChild` kills the process only when no other request holds a lease on it; otherwise it only
leaves the pool.

Also logs the child's response body on any non-success status, so 500s from compute.geometry are
diagnosable from the parent log rather than only forwarded to the caller.

### 9. Solve-result cache purge removes every entry (SELVA FIX)
**File:** `src/compute.geometry/DataCache.cs` — `PurgeSolveResults`

`resultsCache.Trim(100)` is best-effort and may leave entries behind, so `POST /cache/purge` did
not reliably empty the cache. Every key is now removed explicitly. Payloads are not disposed: a
concurrent solve may still hold a shallow duplicate of cached geometry.

### 11. Stable error code on 500s (SELVA)
**Files:** `src/compute.geometry/Startup.cs`, `src/compute.geometry/ResthopperEndpoints.cs` (`DefinitionNotCachedException`)

Adds a machine-readable `code` to the error body in **both** debug and production. The human
`message` is scrubbed in prod, so the code is the only thing a client can classify on. A
stale-pointer cache miss (a `pointer` that no longer resolves and no `algo`) surfaces as a 500
with `definition_not_cached`, which the `@selvajs/compute` client uses to transparently re-upload
the definition; it is thrown before upstream's `BadRequestException` for the same case, so the
client re-uploads instead of getting a 400. We also add `code: "bad_request"` to upstream's 400
body in the same handler.

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
  send one caller's events to another caller's callback. Since upstream's `CheckOut`, a concurrent
  solve may run on a fresh copy; the constants are set on whichever instance actually solves.
- The URL's host must be in `RHINO_COMPUTE_EVENT_SINK_HOSTS`, which defaults to loopback
  (`localhost,127.0.0.1,[::1]`) and nothing else. A same-machine Selva server therefore works
  out of the box, while any sink that is not this host — a LAN address included — stays denied
  until an operator lists it. Setting the variable replaces the default rather than extending
  it. Only an API-key holder can send a solve request, so this is defence in depth against a
  leaked key becoming an SSRF hop.

The solve-result cache (`cachesolve`) keys on the raw body, as upstream, so it never hits a live
solve: each carries a new solve id and token. Selva's own scheduler cache, keyed on the inputs,
covers those.

### 14. Warnings reported outside Debug (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `LogRuntimeMessages`

Upstream only collects `GH_RuntimeMessageLevel.Warning` into the response's `warnings[]` when
`Config.Debug` is on, so a production server never reports a warning at all. Selva shows
warnings to the user and lets them decide whether to trust the result, which is meaningless if
the array is always empty. Outside Debug, warnings are added to the response in a separate block
ahead of upstream's, which is left unchanged. Remarks are unchanged (log only).

---

### 15. Solve outcome returned as a `selva` block (SELVA)
**Files:** `src/compute.geometry/IO/Schema.cs` (`Schema.Selva`),
`src/compute.geometry/ResthopperEndpoints.cs` (`SelvaOutcome` next to the #13 constants),
`src/compute.geometry/GrasshopperDefinition.cs` (`Solve`, `ReadSelvaOutcome`)

Compute flattens runtime messages to strings, so Selva used to recover "a Message component
refused this solve" and "the author wrote this" from markers in the text, and could not report an
aborted solve or a remark at all. Now the Selva plugin writes its verdict,
`{ diagnostics, blocked, aborted }`, as JSON into a `SelvaOutcome` document constant at
`SolutionEnd`, and compute returns it verbatim as `selva: { outcome }` in the solve response.
Compute never interprets the messages; the plugin decides.

- `SelvaOutcome` is defined empty on **every** request, like the #13 constants, so a cached
  document cannot return the previous solve's verdict.
- When the outcome says `blocked` or `aborted`, `values` is returned empty and the
  empty-outputs check is skipped: the client discards those outputs, and serializing them
  cost megabytes of display data for nothing. `errors`/`warnings` and the HTTP status are
  unchanged, so a client that ignores `selva` still sees the markers.
- No Selva plugin, an older one, or a definition without a Selva component: no constant is
  written, no `selva` block, behaviour as before.

---

### 16. Raw JSON on a contextual Text input passes through (SELVA FIX)
**File:** `src/compute.geometry/GrasshopperDefinition.cs` — `DeserializeText`, `IsJsonContainer`

Upstream reads a contextual Text (and Color, File) value as a JSON string literal and, when that
throws, falls back to `Regex.Unescape` on the raw text. Selva hosts send JSON objects as the text
itself (`{"process":"powder",…}`, vektor-fab's `Abschlüsse`), so they always hit the fallback, and
`Regex.Unescape` rewrites their escapes: a `\"` inside a string becomes a bare quote and the JSON
breaks; `\\` and `\n` change meaning. A value that parses as a JSON object or array is now passed
through unchanged. A JSON string literal still unwraps as before; anything else (an escaped,
unquoted payload that is not valid JSON) still goes through `Regex.Unescape`.
Same as `9.x.selva` §17.

---

### 17. Proxy answers carry a Content-Length (SELVA FIX)
**File:** `src/rhino.compute/ReverseProxy.cs` — end of `ProxyRequest`

Upstream writes the child's answer with `WriteAsync`, so an answer that is not gzipped goes out
chunked. The Node client fails to read a chunked 500 and hangs until its timeout. The proxy sets
`ContentLength` before writing; when it gzips, upstream's compression middleware replaces it with
the compressed length.

### 18. Undo recording off on the headless doc (SELVA FIX)
**File:** `src/compute.geometry/ResthopperEndpoints.cs` — `GrasshopperSolveHelper`

Only when `RHINO_COMPUTE_CREATE_HEADLESS_DOC` is on (the Selva setup). With an active doc,
`GH_Document.NewSolution` calls `RhinoDoc.BeginUndoRecord`; nothing ever undoes on a server, and
the recorder retains every solve's state (on Linux it also kills the child). Undo is turned off on
the document the solve creates. Same as `9.x.selva` §6.

### 19. Idle shutdown on time (SELVA FIX)
**File:** `src/compute.geometry/Shutdown.cs` — `TimerTask`

Upstream asks the parent for its idle time once per `idlespan`, so a child outlives the span by
up to a whole span, and core-hour billing runs for all of it: a 5-minute span billed 5–10. The
child now asks at least every `MaxSpanCheckSeconds` (30), so it exits within 30 s of the span.
Same as `9.x.selva` §15.

### 20. Limits a client can read (SELVA)
**Files:** `src/rhino.compute/ReverseProxy.cs` — `MapEndpoints`; `src/compute.geometry/FixedEndpoints.cs` — `GetLimits`

Upstream gives a client no way to learn the server's timeout or body cap, so Selva carried its own
copies of both, and an operator had to keep two sets of numbers in step. A copy set higher than the
server's ended in a bare 413 or 502. `GET /limits` returns `timeoutSeconds` (`RHINO_COMPUTE_TIMEOUT`
or `--timeout`), `maxRequestBytes` (`RHINO_COMPUTE_MAX_REQUEST_SIZE` or `--max-request-size`) and
`signalsStaleDefinition` (a stale definition pointer answers `definition_not_cached`, see §11).
`rhino.compute` answers it itself, because its own timeout and body cap are what a request meets
first; a child reached directly answers with its own config. Same as `9.x.selva` §21.

`deploymentId` is new on every process start and, in `rhino.compute`, on `/shutdown-children` or
`/recycle-children`: the ways a plugin is redeployed. Selva drops cached solve results when it
changes. A plugin swapped on disk while children sit idle-exited is not seen.

### 21. A solve with Grasshopper errors keeps its child first (SELVA FIX)
**File:** `src/rhino.compute/ReverseProxy.cs` — `ProxyRequest`

`TakeChild` moves the child it picks to the back of the queue, and upstream moves it to the front
again only on a 200, so a child that fails drops behind the others. A solve that finishes with
Grasshopper errors also answers 500, so with definitions that raise errors by design every solve
rotated to another child, whose cached document holds another caller's inputs or none: a cold
solve each time. A 500 that carries `Server-Timing` came from a finished solve (the exception
handler's 500 has none), so that child now stays first as well. Same as `9.x.state` §18.

### 22. A caller that hangs up stops its solve, opt-in (SELVA)
**Files:** `src/rhino.compute/ReverseProxy.cs` — `SendProxyRequest`, `ProxyRequest`;
`src/compute.geometry/ResthopperEndpoints.cs` — `GrasshopperSolveHelper`;
`src/compute.geometry/GrasshopperDefinition.cs` — `Solve`;
`src/rhino.compute/Config.cs`, `src/compute.geometry/Config.cs` — `AbortOnHangUp`

Behind `RHINO_COMPUTE_ABORT_ON_HANGUP`, off by default, so a stock deployment behaves as upstream: a
client that times out and retries may rely on the abandoned attempt finishing into the solve cache.
Forgetting the inputs and not caching after an abort stay on regardless, since the Selva plugin
aborts solves itself.

Upstream's proxy does not pass a client's disconnect on, so the child finishes every solve its
caller gave up on, holding the solve lock and the CPU. Selva's latest-wins scheduler abandons the
in-flight solve whenever a slider moves on, so the solve the user is waiting for ran beside, or
behind, one nobody wanted. The proxy now forwards the disconnect, and `Solve` turns the request's
cancellation into `RequestAbortSolution`: Grasshopper stops between components (a long component
stops sooner only if it polls the abort flag itself). After any abort, the hang-up's or the Selva
plugin's own, `Solve` forgets the inputs it last set, so the next request sets every one again, and
the result is not cached. A component stopped mid-run reads as computed; without this, the same
inputs again would return its partial outputs as a valid 200. A plugin abort with the caller still
connected answers normally, with `selva.outcome.aborted` (#15). Same as `9.x.state` §19.

### 23. Requests with `X-Session-Id` return to their child (SELVA)
**Files:** `src/rhino.compute/ComputeChildren.cs` — `AcquireChild(sessionId)`, `TakeChild`, `EnsurePoolCapacity`;
`src/rhino.compute/ReverseProxy.cs` — `ProxyRequest`, `SessionIdFrom`; `src/rhino.compute/Config.cs`

Upstream picks a child with no regard for who is asking: the first idle one, which is whichever
finished last. A child's cached `GH_Document` keeps its last inputs and outputs, so a solve there
reruns only what changed, but that warmth holds only until two users overlap. They then trade
children and solve cold. The solve-result cache doesn't help, since a live solve never hits it (§13).

A request with `X-Session-Id` now goes to the child that session last used. The claim is a
preference, not a taint, because the state is only a cache: a request never fails over a claim, and
a child with claims can still serve anyone when nothing else is free. Exclusive claims would strand
new sessions on a full pool and starve nested Hops calls, which carry no session header. Requests
without the header route as before, and with no claims `TakeChild` chooses exactly as it did.

- A claim names a child by port and process id: a replacement reuses the port.
- A new claim prefers the least-claimed idle child, and anonymous requests avoid claimed children
  the same way.
- A claimed child that is busy, even with the session's own earlier request, sends the request to
  another child (`spilled`), and the claim moves there, as that child now has the session's newest
  inputs. A new request never waits for, or cancels, one already running, independent of
  `RHINO_COMPUTE_ABORT_ON_HANGUP` (§22).
- A proxy timeout drops the claim and sends the child to the back of the queue, as it may still be
  running the abandoned solve. So does a 5xx without `Server-Timing`, which is the child failing
  rather than a solve with Grasshopper errors. A `definition_not_cached` 500 is the exception: a
  healthy child answers it after a spill, and the client's re-upload should go to that child. Either
  drop removes the claim only if it still names the child that failed.
- `RHINO_COMPUTE_SESSION_TIMEOUT` (default 3600 s) expires a claim after its last request.
  At `RHINO_COMPUTE_SESSION_MAX_CLAIMS` (default 256, `0` turns routing off), a new claim replaces
  the one seen longest ago.
- Session hits bypass `SelectChild`, so `EnsurePoolCapacity` is split out of it for them to refill the pool.
- Session requests skip `MoveToFrontOfQueue`: at the front, a claimed child would be everyone's
  first choice.
- Each routed request reports `affinity;desc=hit` (or `claimed`, `spilled`, `none`) in
  `Server-Timing`, and logs it at Debug with the port and an 8-character hash of the session id.

Claims live in the `rhino.compute` process, so several hosts would need load-balancer affinity on
the same header. They are for routing only and isolate nothing. Same as `9.x.state` §23.

### 24. Solve mode as a document constant (SELVA)
**Files:** `src/compute.geometry/IO/Schema.cs` (`Schema.SelvaMode`),
`src/compute.geometry/ResthopperEndpoints.cs` (`SelvaMode` next to the #13 and #15 constants)

Every upstream solve makes every output, so a definition that exports files pays for the export on
every edit, though nobody downloads after most of them. A Selva page solves its edits as previews:
the request carries `selvamode: "preview"`, compute hands it to the document as the `SelvaMode`
constant, and the Selva plugin skips the file components that only feed a Download button. The
download is a second request without the field. Compute never interprets the mode.

- `SelvaMode` is defined on **every** request, `"preview"` or empty, like the #13 constants.
- Only the exact value `preview` passes through, so a caller can't put arbitrary text into a
  constant every definition can read.
- `cachesolve` keys on the raw body, so a preview and a full solve never share an entry.

Same as `9.x.state` §24.

### 25. Warm-solve count in `Server-Timing` (SELVA)
**Files:** `src/compute.geometry/GrasshopperDefinition.cs` — `Solve`, `LastRecomputedObjects`;
`src/compute.geometry/ResthopperEndpoints.cs` — `GrasshopperSolveHelper`;
`src/rhino.compute/ReverseProxy.cs` — `ProxyRequest`

The child's `Server-Timing` adds `recomputed;desc="ran/total"`, the active objects the solve ran: a
cached document keeps the data of everything an unchanged input feeds, so a low count is a warm
solve and `total/total` a cold one. It is counted from the expired objects before `NewSolution`,
because afterwards every object reads as computed. Upstream's proxy drops the child's
`Server-Timing`; it now forwards it, with §23's `affinity` entry. Part of `9.x.state` §16.

## Non-source divergence

Repo/tooling files with no upstream counterpart, or deliberately changed:

- `.editorconfig`, `.gitattributes`, `.vscode/settings.json`, `.gitignore` additions — local
  conventions.
- `.github/workflows/workflow_ci.yml` — artifact upload gated on `8.x.selva` instead of `8.x`.
- `script/production/module_update_compute.ps1` — `-Repository`/`-Branch` parameters (defaults
  are upstream's) so upstream's updater deploys `8.x.selva`. Replaced our own 1000-line updater.
- `script/update_compute_server/README.md` — Selva setup/update runbook, including the env vars
  that replaced former source defaults: `RHINO_COMPUTE_CREATE_HEADLESS_DOC=true` and
  `RHINO_COMPUTE_MAX_REQUEST_SIZE=314572800`.
- `TROUBLESHOOTING-DUPLICATE-LANGUAGE.md` — runbook for the RhinoCode duplicate-language 500.
- `src/compute.geometry/compute.geometry.csproj` — `<Version>`.
- `CLAUDE.md` — agent instructions for this fork.

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
