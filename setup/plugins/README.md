# Custom Plugins

Drop your custom Grasshopper packages in this folder. It is mounted into the
container at `/plugins` and loaded automatically on every container start:

| You drop in...            | What happens                                          |
| ------------------------- | ----------------------------------------------------- |
| `MyPlugin.gha`            | Copied to the GH Libraries folder                     |
| `MyPlugin.dll` (deps)     | Copied to the GH Libraries folder                     |
| `MyPluginFolder/`         | Copied recursively to the GH Libraries folder         |
| `myplugin-1.0.0.yak`      | Installed via `yak install`                           |

To update a plugin: replace the file here, then

```bash
docker restart rhino-compute-x9
```

No image rebuild needed. Contents of this folder (except this README) are
gitignored.

**Do not keep a plugin here that you also live-mount via `LOCAL_PLUGINS`.**
Because the folder is gitignored, a copy left here is invisible to git and
keeps being loaded — with the same assembly name as your live build — long
after you stopped updating it. The container refuses to start when it finds
the same `.gha` twice; if you hit that, the leftover is in here.

Tip: declare the files you drop here in `setup/packages.json` under `"local"` —
the container then warns at startup if one is missing, so the manifest stays
the single source of truth for everything the server needs.
