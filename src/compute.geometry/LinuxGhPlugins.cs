using System;
using System.IO;
using Serilog;

namespace compute.geometry
{
    // VEKTORNODE: SELVA FIX — entire file, see FORK_CHANGES §10. Called once on Linux after RunHeadless.
    internal static class LinuxGhPlugins
    {
        // GH loads external libraries once, inside the Instances.ComponentServer getter, and shows any
        // exception in a MessageBox that never appears headless, so a failed load is silent.
        // PlugIn.GetMultiTargetPath throws on Linux for a GHA under a netX.0 subfolder (null OS suffix
        // passed to string.IndexOf), and GH_ComponentServer.ExternalFiles has no per-file try/catch, so
        // one such GHA stops every plugin loading. Those are quarantined with a .no9 marker, which GH skips.
        public static void Load()
        {
            try
            {
                var getMultiTargetPath = typeof(Rhino.PlugIns.PlugIn).GetMethod("GetMultiTargetPath",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                foreach (var folderInfo in Grasshopper.Folders.AssemblyFolders)
                {
                    if (!Directory.Exists(folderInfo.Folder))
                        continue;
                    foreach (var gha in Directory.GetFiles(folderInfo.Folder, "*.gha", folderInfo.SearchOption))
                    {
                        try { getMultiTargetPath?.Invoke(null, new object[] { gha }); }
                        catch
                        {
                            Log.Warning("Quarantining {Gha}: multi-target (netX.0 subfolder) plugin layouts crash Rhino's Linux runtime-folder resolution — flatten the folder so the .gha has no netX.0 parent", gha);
                            try { File.Create(Path.ChangeExtension(gha, ".no9")).Dispose(); }
                            catch (Exception mex) { Log.Warning("Could not write quarantine marker for {Gha}: {Message}", gha, mex.Message); }
                        }
                    }
                }

                // The getter runs GH's guarded once-only load. Calling LoadExternalFiles() as well
                // loads every assembly twice and floods the server with object-ID conflicts.
                foreach (var lex in Grasshopper.Instances.ComponentServer.LoadingExceptions)
                    Log.Warning("Grasshopper library load error: {Name}: {Message}", lex.Name, lex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load Grasshopper external libraries — GH plugins may be unavailable");
            }
        }
    }
}
