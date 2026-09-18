# Case Study: Grasshopper Plugins Silently Not Loading on Linux

**Date:** July 2026
**Affects:** Rhino.Compute (9.x branch) on Linux — Docker containers and VMs
**Symptom:** Solves fail with `PayAttentionException` ("Looks like you've missed something...") even though all plugins are installed. No error messages anywhere.

## What happened

We set up Rhino.Compute in a Docker container on Linux. Yak packages
(Selva among others) installed fine, and a few in-house `.gha` builds
were copied into the Grasshopper Libraries folder. The server started
without complaints.

But every solve failed with `PayAttentionException`. That exception is
thrown by our fork when a solve produces **zero outputs** — and checking
`GET /plugins/gh/installed` returned `{}`: Grasshopper had loaded **no
plugins at all**. Not the yak ones, not ours. Silently.

## Why it happened

Three separate problems were stacked on top of each other:

### 1. Nobody ever tells Grasshopper to load plugins on Linux

On Windows, Grasshopper's editor-initialization pipeline calls the
library scan that loads all `.gha` files. Headless on Linux, compute
calls `RunHeadless()` — but that method literally just sets a flag:

```csharp
internal static void RunHeadless()
{
    AutoShowBanner = false;
    AutoHideBanner = false;
    m_headless = true;
}
```

(Correction from deeper digging: Grasshopper *does* run the plugin scan
lazily — the `Instances.ComponentServer` getter loads external files
exactly once on first access. But any exception in that load is swallowed
into a `MessageBox` that never shows headless, so when the scan crashed
(next section), it failed invisibly and GH carried on with core
components only. Compute now triggers this load explicitly at startup and
logs every loaded plugin and load error, so silence is no longer
possible. Important: touch the `ComponentServer` getter — do NOT call
`LoadExternalFiles()` yourself on top of it, or every assembly loads
twice and thousands of component-ID conflicts follow.)

### 2. A Rhino bug on Linux made the scan crash

The scan died inside Rhino itself (`HostUtils.GetRuntimeSpecificFolder`):

```csharp
string suffix = RunningOnWindows ? "-windows" : (RunningOnOSX ? "-macos" : null);
...
int num2 = name.IndexOf(suffix);   // throws ArgumentNullException on Linux!
```

On Linux the suffix is `null`, and `string.IndexOf(null)` throws. This
code runs for any plugin stored in a target-framework subfolder — the
standard multi-target layout:

```
MyPlugin/
  net7.0/
    MyPlugin.gha    ← this layout crashes the resolver on Linux
```

Worse: Grasshopper's scan loop has **no per-file error handling**, so a
single plugin in this layout aborted the entire scan. That's why *zero*
plugins loaded even though most of them were fine.

### 3. Plugin loading requires a license

Bonus finding: the GHA loading path runs a license check. Without a
valid `RHINO_TOKEN`, the child process dies with
`Rhino.Runtime.NotLicensedException` the moment it touches plugin
loading — another way to lose plugins that has nothing to do with the
plugins themselves.

## The fixes

All in this repo:

1. **Explicit plugin load** — `src/compute.geometry/Startup.cs` now
   calls `GH_ComponentServer.LoadExternalFiles()` after `RunHeadless()`
   on Linux, then logs every loaded plugin (`Loaded Grasshopper plugin:
   ...`) and every load error. Silence is no longer an option.

2. **Quarantine instead of collapse** — before the scan, compute probes
   each `.gha` with the same path resolution Rhino will use. Files that
   would crash it get a `.no9` marker (Grasshopper's official
   "skip this file" convention) and a loud log warning. One broken
   plugin can no longer take down all the others.

3. **Flatten multi-target layouts** — `setup/start.sh` flattens
   `MyPlugin/net7.0/*.gha` to `MyPlugin/*.gha` when staging plugins
   (both the mounted plugin folders and yak packages), so they load
   normally despite the Rhino bug.

### Bonus: script components (C#/Python) can 500 right after a container start

Separate but related: definitions containing script components
(RhinoCodePluginGH) can fail with `NullReferenceException` in
`Rhino.Runtime.Code.Languages.LanguageRegistryQuery.WherePasses` when
several requests arrive in parallel right after a (re)start. Script
languages initialize lazily on the first scripted definition, and that
init races itself under concurrent requests. Once the languages finish
loading (seconds), subsequent requests succeed — a retry recovers.

We chose to keep the code close to upstream and NOT ship a workaround.
If it bites, the known fix is a one-time startup warm-up via
`RhinoCodePlatform.Rhino3D.Registrar.StartScriptingLanguages(LanguageSpec.CSharp/Python3, true)`
(the same blocking init path the script components use; ~0.3s for C#,
~10s for Python) — call it after Grasshopper loads in
`Startup.RhinoCoreStartup`.

## The opposite failure: the *wrong* plugin loads (September 2026)

The mirror image of this case study is worth one paragraph, because it
looks like a plugin bug and is not. `setup/plugins/` is gitignored, so a
copy of a plugin dropped there stays invisible to `git status` and keeps
being staged on every start — even after you moved to live-mounting the
same plugin via `LOCAL_PLUGINS`. Both copies carry the same assembly
name; Grasshopper loads whichever it scans first, and the startup log
looks identical either way. The symptom is a component that "loaded
fine" but throws `TypeLoadException: Could not load type '...' from
assembly '...'` at solve time, referencing a type that only the stale
build knew about. Hashing the `.gha` you *expect* to be loaded shows it
matches your build, because it does — the other one is what runs.

`start.sh` now replaces a same-named folder outright when staging a live
mount, and refuses to start if the same `.gha` appears twice. Rule of
thumb: one source per plugin — see "One source per plugin" in
[setup/README.md](../setup/README.md).

## How to tell if you're hitting this

- Solves return 500 with `PayAttentionException` but `/io` works
- `GET /plugins/gh/installed` returns `{}` or is missing plugins you installed
- Startup log jumps from `(3/4) Loading grasshopper` to `(4/4)` in the
  same second, with no `Loaded Grasshopper plugin:` lines
- Child process dies with `NotLicensedException` → check `RHINO_TOKEN`

## Debugging tips that worked

- `GET /plugins/gh/installed` (with the `RhinoComputeKey` header) shows
  what Grasshopper actually loaded — trust this over "the files are on disk"
- `RHINO_COMPUTE_DEBUG=true` enables debug logging (e.g. the resolved
  Rhino system directory)
- `ilspycmd` (a dotnet tool) decompiles Grasshopper.dll / RhinoCommon.dll
  right inside the container — that's how the `IndexOf(null)` bug was found

## Upstream

The `string.IndexOf(null)` crash in `HostUtils.GetRuntimeSpecificFolder`
is a Rhino 9 WIP bug (observed in 9.0.26185.1000, Linux) and should be
reported to McNeel: multi-target plugin folder layouts are unusable on
Linux until it's fixed.
