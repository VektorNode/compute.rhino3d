using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Collections.Generic;
using System.Drawing;

using Rhino.Geometry;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

using Resthopper.IO;
using Newtonsoft.Json;
using System.Linq;
using Serilog;
using Serilog.Events;
using System.Reflection;

namespace compute.geometry
{
    class GrasshopperDefinition
    {
        static readonly Dictionary<string, FileSystemWatcher> filewatchers = new Dictionary<string, FileSystemWatcher>();
        static readonly HashSet<string> watchedFiles = new HashSet<string>();
        // Requests register files concurrently, and each watcher reads the set from its own thread.
        static readonly object watcherLock = new object();
        static uint watchedFileRuntimeSerialNumber = 1;
        public static uint WatchedFileRuntimeSerialNumber
        {
            get { return watchedFileRuntimeSerialNumber; }
        }
        static void RegisterFileWatcher(string path)
        {
            if (!File.Exists(path))
                return;

            path = Path.GetFullPath(path);
            lock (watcherLock)
            {
                if (!watchedFiles.Add(path.ToLowerInvariant()))
                    return;

                string directory = Path.GetDirectoryName(path);
                if (filewatchers.ContainsKey(directory) || !Directory.Exists(directory))
                    return;

                var fsw = new FileSystemWatcher(directory);
                fsw.NotifyFilter = NotifyFilters.Attributes |
                    NotifyFilters.CreationTime |
                    NotifyFilters.FileName |
                    NotifyFilters.LastAccess |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.Security;
                fsw.Changed += Fsw_Changed;
                fsw.EnableRaisingEvents = true;
                filewatchers[directory] = fsw;
            }
        }

        private static void Fsw_Changed(object sender, FileSystemEventArgs e)
        {
            string path = e.FullPath.ToLowerInvariant();
            lock (watcherLock)
            {
                if (watchedFiles.Contains(path))
                    watchedFileRuntimeSerialNumber++;
            }
        }

        public static void LogDebug(string message) { Log.Debug(message); }
        public static void LogError(string message) { Log.Error(message); }

        public static GrasshopperDefinition FromUrl(string url, bool cache)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            GrasshopperDefinition rc = DataCache.GetCachedDefinition(url);

            if (rc != null)
            {
                LogDebug("Using cached definition");
                return rc;
            }

            if (Guid.TryParse(url, out Guid componentId))
            {
                rc = Construct(componentId);
            }
            else
            {
                var archive = ArchiveFromUrl(url, out byte[] contents);
                if (archive == null)
                    return null;

                rc = Construct(archive);
                rc.sourceBytes = contents;
                rc.CacheKey = url;
                rc.IsLocalFileDefinition = !UrlGuard.IsWebUrl(url) && File.Exists(url);
            }
            if (cache)
            {
                DataCache.SetCachedDefinition(url, rc, null);
                rc.InDataCache = true;
            }
            return rc;
        }

        public static GrasshopperDefinition FromBase64String(string data, bool cache)
        {
            string cacheKey = string.IsNullOrWhiteSpace(data) ? null : DataCache.CreateCacheKey(data);
            if (cache)
            {
                // Clients that send the definition with every request would otherwise build, and leak, one per request.
                var loaded = DataCache.GetLoadedDefinition(cacheKey);
                if (loaded != null)
                {
                    LogDebug("Using cached definition");
                    return loaded;
                }
            }

            var archive = ArchiveFromBase64String(data, out byte[] contents);
            if (archive == null)
                return null;

            var rc = Construct(archive);
            if (rc != null)
            {
                rc.sourceBytes = contents;
                rc.CacheKey = cacheKey;
                if (cache)
                {
                    DataCache.SetCachedDefinition(rc.CacheKey, rc, data);
                    rc.InDataCache = true;
                }
            }
            return rc;
        }

        // Grasshopper's document server isn't safe from two threads, and nested Hops calls load definitions alongside
        // other solves. Only its calls are locked: reading a definition can make Hops fetch its remote definition, and
        // that request may need this lock in the same or another process.
        static readonly object documentServerLock = new object();

        private static GrasshopperDefinition Construct(Guid componentId)
        {
            var component = Grasshopper.Instances.ComponentServer.EmitObject(componentId) as GH_Component;
            if (component == null)
                return null;

            var definition = new GH_Document();
            definition.AddObject(component, false);

            try
            {
                // raise DocumentServer.DocumentAdded event (used by some plug-ins)
                lock (documentServerLock)
                    Grasshopper.Instances.DocumentServer.AddDocument(definition);
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception in DocumentAdded event handler");
            }

            GrasshopperDefinition rc = new GrasshopperDefinition(definition, null);
            rc.sourceComponentId = componentId;
            rc.singularComponent = component;
            foreach (var input in component.Params.Input)
            {
                rc.input[input.NickName] = new InputGroup(input);
            }
            foreach (var output in component.Params.Output)
            {
                rc.output[output.NickName] = output;
                rc.outputTypeNames[output.NickName] = output.TypeName;
            }
            return rc;
        }
        private static void AddInput(IGH_Param param, string name, ref GrasshopperDefinition rc)
        {
            if (rc.input.ContainsKey(name))
            {
                string msg = "Multiple input parameters with the same name were detected. Parameter names must be unique.";
                rc.HasErrors = true;
                rc.ErrorMessages.Add(msg);
                LogError(msg);
            }
            else
                rc.input[name] = new InputGroup(param);
        }
        private static void AddOutput(IGH_Param param, string name, ref GrasshopperDefinition rc)
        {
            if (rc.output.ContainsKey(name))
            {
                string msg = "Multiple output parameters with the same name were detected. Parameter names must be unique.";
                rc.HasErrors = true;
                rc.ErrorMessages.Add(msg);
                LogError(msg);
            }
            else
            {
                rc.output[name] = param;
                rc.outputTypeNames[name] = param.TypeName;
            }
        }

        private static GrasshopperDefinition Construct(GH_Archive archive)
        {
            string icon = null;
            var chunk = archive.GetRootNode.FindChunk("Definition");
            if (chunk != null)
            {
                chunk = chunk.FindChunk("DefinitionProperties");
                if (chunk != null)
                {
                    string s = String.Empty;
                    if (chunk.TryGetString("IconImageData", ref s))
                    {
                        icon = s;
                    }
                }
            }

            var definition = new GH_Document();
            if (!archive.ExtractObject(definition, "Definition"))
                throw new Exception("Unable to extract definition from archive");

            try
            {
                // raise DocumentServer.DocumentAdded event (used by some plug-ins)
                lock (documentServerLock)
                    Grasshopper.Instances.DocumentServer.AddDocument(definition);
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception in DocumentAdded event handler");
            }

            GrasshopperDefinition rc = new GrasshopperDefinition(definition, icon);
            foreach (var obj in definition.Objects)
            {
                IGH_ContextualParameter contextualParam = obj as IGH_ContextualParameter;
                if (contextualParam != null)
                {
                    IGH_Param param = obj as IGH_Param;
                    if (param != null && !param.Locked)
                    {
                        AddInput(param, param.NickName, ref rc);
                    }
                    continue;
                }

                Type objectClass = obj.GetType();
                var className = objectClass.Name;
                if (className == "ContextBakeComponent")
                {
                    var contextBaker = obj as GH_Component;
                    if (contextBaker != null && !contextBaker.Locked)
                    {
                        IGH_Param param = contextBaker.Params.Input[0];
                        AddOutput(param, param.NickName, ref rc);
                    }
                }

                if (className == "ContextPrintComponent")
                {
                    var contextPrinter = obj as GH_Component;
                    if (contextPrinter != null && !contextPrinter.Locked)
                    {
                        IGH_Param param = contextPrinter.Params.Input[0];
                        AddOutput(param, param.NickName, ref rc);
                    }
                }

                var group = obj as GH_Group;
                if (group == null)
                    continue;

                string nickname = group.NickName;
                var groupObjects = group.Objects();
                if (nickname.Contains("RH_IN") && groupObjects.Count > 0)
                {
                    var param = groupObjects[0] as IGH_Param;
                    if (param != null && !param.Locked)
                    {
                        AddInput(param, nickname, ref rc);
                    }
                }

                if (nickname.Contains("RH_OUT") && groupObjects.Count > 0)
                {
                    if (groupObjects[0] is IGH_Param param)
                    {
                        AddOutput(param, nickname, ref rc);
                    }
                    else if (groupObjects[0] is GH_Component component)
                    {
                        int outputCount = component.Params.Output.Count;
                        for (int i = 0; i < outputCount; i++)
                        {
                            if (1 == outputCount)
                            {
                                AddOutput(component.Params.Output[i], nickname, ref rc);
                            }
                            else
                            {
                                string itemName = $"{nickname} ({component.Params.Output[i].NickName})";
                                AddOutput(component.Params.Output[i], itemName, ref rc);
                            }
                        }
                    }
                }
            }
            return rc;
        }

        private GrasshopperDefinition(GH_Document definition, string icon)
        {
            Definition = definition;
            iconString = icon;
            FileRuntimeCacheSerialNumber = watchedFileRuntimeSerialNumber;
        }

        // A cached definition is shared by every request for it, but a solve sets its inputs and reads its outputs, so
        // two solves at once (nested Hops calls solve alongside other solves) would overwrite each other's. The first
        // solve takes this instance; any other solves a copy built from the same source, released afterwards.
        public GrasshopperDefinition CheckOut()
        {
            if (Interlocked.CompareExchange(ref checkedOut, 1, 0) == 0)
                return this;
            GrasshopperDefinition copy = null;
            if (sourceBytes != null)
            {
                var archive = ArchiveFromBytes(sourceBytes);
                if (archive != null)
                    copy = Construct(archive);
            }
            else if (sourceComponentId != Guid.Empty)
            {
                copy = Construct(sourceComponentId);
            }
            if (copy == null)
                throw new Exception("Unable to load another copy of the grasshopper definition");
            copy.CacheKey = CacheKey;
            copy.IsLocalFileDefinition = IsLocalFileDefinition;
            copy.isCopy = true;
            return copy;
        }

        public void CheckIn()
        {
            if (!isCopy)
            {
                Interlocked.Exchange(ref checkedOut, 0);
                if (retired && Interlocked.CompareExchange(ref checkedOut, 2, 0) == 0)
                    Release();
                return;
            }
            Release();
        }

        // The definition cache dropped this instance: released now, or by the solve using it when it checks it in.
        public void Retire()
        {
            retired = true;
            if (Interlocked.CompareExchange(ref checkedOut, 2, 0) == 0)
                Release();
        }

        void Release()
        {
            lock (documentServerLock)
                Grasshopper.Instances.DocumentServer.RemoveDocument(Definition);
            Definition.Dispose();
        }

        // The file's bytes rather than the parsed archive, which takes several times the memory.
        byte[] sourceBytes;
        Guid sourceComponentId;
        // 0 free, 1 a solve is using it, 2 released.
        int checkedOut;
        volatile bool retired;
        bool isCopy;

        public GH_Document Definition { get; }
        public bool InDataCache { get; set; }
        public bool HasErrors { get; private set; } // default: false
        public bool IsLocalFileDefinition { get; set; } // default: false
        public uint FileRuntimeCacheSerialNumber { get; private set; }
        public string CacheKey { get; set; }
        string iconString;
        GH_Component singularComponent;
        Dictionary<string, InputGroup> input = new Dictionary<string, InputGroup>();
        Dictionary<string, IGH_Param> output = new Dictionary<string, IGH_Param>();
        // Read at load: TypeName enumerates a parameter's data, which another request's solve may be changing.
        Dictionary<string, string> outputTypeNames = new Dictionary<string, string>();
        public List<string> ErrorMessages = new List<string>();

        public GH_Path GetPath(string p)
        {
            string tempPath = p.Trim('{', '}');
            int[] pathIndices = tempPath.Split(';').Select(Int32.Parse).ToArray();
            return new GH_Path(pathIndices);
        }

        public void SetInputs(Schema inputSchema)
        {
            // Collect names of inputs that were actually updated vs ones whose value was already
            // current, and log each group as a single summary line at the end. With many
            // parameters and one changed value, per-input messages drowned out anything useful.
            // Both wire formats (Grasshopper and the legacy DataTree<ResthopperObject>) feed
            // into the same two lists so the log output is consistent across them.
            var updatedInputs = new List<string>();
            var skippedInputs = new List<string>();

            if (inputSchema.DataFormat == SchemaDataFormat.Grasshopper)
            {
                foreach (var entry in inputSchema.GrasshopperValues.Values)
                {
                    if (!input.TryGetValue(entry.Key, out var inputGroup))
                    {
                        continue;
                    }
                    updatedInputs.Add(entry.Key);
                    inputGroup.Param.ClearData();
                    inputGroup.Param.ExpireSolution(false); // mark param as expired but don't recompute just yet!
                    inputGroup.Param.AddVolatileDataTree(entry.Value);
                }
            }
            else
            {
                foreach (var tree in inputSchema.Values)
                {
                    if (!input.TryGetValue(tree.ParamName, out var inputGroup))
                    {
                        continue;
                    }

                    if (inputGroup.AlreadySet(tree))
                    {
                        skippedInputs.Add(tree.ParamName);
                        continue;
                    }

                    updatedInputs.Add(tree.ParamName);
                    try
                    {
                        SetValues(inputGroup, tree);
                    }
                    catch (Exception ex) when (BadRequestException.IsBadValue(ex))
                    {
                        throw new BadRequestException($"Couldn't read the value of input '{tree.ParamName}': {ex.Message}", ex);
                    }
                    // Only once its values were used, so a request that failed isn't later skipped as already set.
                    inputGroup.CacheTree(tree);
                }
            }

            if (inputSchema.DataFormat == SchemaDataFormat.Grasshopper)
            {
                // The Grasshopper wire format ships a full archive of model objects on every
                // solve, so per-input set/skip accounting isn't meaningful here — every entry
                // we processed got applied. Emit a count-only heartbeat so the line is honest
                // (we can't tell what the user actually changed without diffing the archive
                // against the previous solve, which isn't worth the cost).
                if (updatedInputs.Count > 0)
                {
                    string valueWord = updatedInputs.Count == 1 ? "value" : "values";
                    LogDebug($"Applied {updatedInputs.Count} input parameter {valueWord}");
                }
            }
            else
            {
                if (updatedInputs.Count > 0)
                {
                    string valueWord = updatedInputs.Count == 1 ? "value" : "values";
                    string inputWord = updatedInputs.Count == 1 ? "input" : "inputs";
                    LogDebug($"Setting {valueWord} for {updatedInputs.Count} {inputWord}: {string.Join(", ", updatedInputs)}");
                }
                if (skippedInputs.Count > 0)
                {
                    string inputWord = skippedInputs.Count == 1 ? "input" : "inputs";
                    LogDebug($"Skipping {skippedInputs.Count} unchanged {inputWord}: {string.Join(", ", skippedInputs)}");
                }
            }
        }

        void SetValues(InputGroup inputGroup, Resthopper.IO.DataTree<ResthopperObject> tree)
        {
            IGH_ContextualParameter contextualParameter = inputGroup.Param as IGH_ContextualParameter;
            if (contextualParameter != null)
            {
                if (contextualParameter.AtLeast == 0)
                    (contextualParameter as IGH_Param).Optional = true;

                switch (ParamTypeName(inputGroup.Param))
                {
                    case "Boolean":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Boolean(JsonConvert.DeserializeObject<bool>(r.Data)));
                        break;
                    case "Number":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Number(JsonConvert.DeserializeObject<double>(r.Data)));
                        break;
                    case "Integer":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Integer(JsonConvert.DeserializeObject<int>(r.Data)));
                        break;
                    case "Point":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Point(JsonConvert.DeserializeObject<Point3d>(r.Data)));
                        break;
                    case "Plane":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Plane(JsonConvert.DeserializeObject<Plane>(r.Data)));
                        break;
                    case "Line":
                        BuildAndAssignContextualTree(contextualParameter, tree, r => new GH_Line(JsonConvert.DeserializeObject<Line>(r.Data)));
                        break;
                    case "Text":
                        BuildAndAssignContextualTree(contextualParameter, tree, DeserializeText);
                        break;
                    case "Geometry":
                        BuildAndAssignContextualTree(contextualParameter, tree, DeserializeGeometry);
                        break;
                    ////////////////////////////////////////////////
                    /// VEKTORNODE: IO-HANDLERS — Selva Specific Contextual Inputs
                    /// (Color / File / ValueList — none of these exist upstream).
                    ////////////////////////////////////////////////
                    // Color and File arrive as (possibly escaped) JSON strings; DeserializeText
                    // already does the JsonConvert-with-Regex.Unescape fallback these need.
                    case "Color":
                        BuildAndAssignContextualTree(contextualParameter, tree, DeserializeText);
                        break;
                    case "File":
                        BuildAndAssignContextualTree(contextualParameter, tree, DeserializeText);
                        break;
                    // ValueList assigns via SetValues (a flat string list), not a contextual
                    // data tree, so it can't use BuildAndAssignContextualTree.
                    case "ValueList":
                        {
                            var stringList = new List<string>();
                            foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                            {
                                for (int i = 0; i < entree.Value.Count; i++)
                                {
                                    ResthopperObject restobj = entree.Value[i];
                                    string data;
                                    try
                                    { data = JsonConvert.DeserializeObject<string>(restobj.Data) ?? restobj.Data.Trim('"'); }
                                    catch { data = restobj.Data.Trim('"'); }
                                    stringList.Add(data);
                                }
                            }
                            contextualParameter.GetType()
                                .GetMethod("SetValues")?
                                .Invoke(contextualParameter, new object[] { stringList });
                        }
                        break;
                }
                return;
            }

            inputGroup.Param.VolatileData.Clear();
            inputGroup.Param.ExpireSolution(false); // mark param as expired but don't recompute just yet!

            // Note: Param_String reads restobj.Data raw, while GH_Panel goes through
            // JsonConvert.DeserializeObject<string>. This asymmetry is preserved from the
            // original cascade — fixing it would change wire-format behavior for one of them.
            Func<ResthopperObject, IGH_Goo> convert = inputGroup.Param switch
            {
                Param_Point _      => r => new GH_Point(JsonConvert.DeserializeObject<Point3d>(r.Data)),
                Param_Vector _     => r => new GH_Vector(JsonConvert.DeserializeObject<Vector3d>(r.Data)),
                Param_Integer _    => r => new GH_Integer(JsonConvert.DeserializeObject<int>(r.Data)),
                Param_Number _     => r => new GH_Number(JsonConvert.DeserializeObject<double>(r.Data)),
                Param_String _     => r => new GH_String(r.Data),
                Param_Line _       => r => new GH_Line(JsonConvert.DeserializeObject<Line>(r.Data)),
                Param_Curve _      => DeserializeCurve,
                Param_Circle _     => r => new GH_Circle(JsonConvert.DeserializeObject<Circle>(r.Data)),
                Param_Plane _      => r => new GH_Plane(JsonConvert.DeserializeObject<Plane>(r.Data)),
                Param_Rectangle _  => r => new GH_Rectangle(JsonConvert.DeserializeObject<Rectangle3d>(r.Data)),
                Param_Box _        => r => new GH_Box(JsonConvert.DeserializeObject<Box>(r.Data)),
                Param_Surface _    => r => new GH_Surface(JsonConvert.DeserializeObject<Surface>(r.Data)),
                Param_Brep _       => r => new GH_Brep(JsonConvert.DeserializeObject<Brep>(r.Data)),
                Param_Mesh _       => r => new GH_Mesh(JsonConvert.DeserializeObject<Mesh>(r.Data)),
                GH_NumberSlider _  => r => new GH_Number(JsonConvert.DeserializeObject<double>(r.Data)),
                Param_Boolean _ or GH_BooleanToggle _ => r => new GH_Boolean(JsonConvert.DeserializeObject<bool>(r.Data)),
                GH_Panel _         => r => new GH_String(JsonConvert.DeserializeObject<string>(r.Data)),
                _                  => null
            };
            if (convert != null)
                AddTreeData(inputGroup.Param, tree, convert);
        }

        // Shared write path for SetInputs' regular-parameter dispatch. Each Param_X case
        // differs only in how a ResthopperObject is deserialized + wrapped, so the converter
        // delegate captures the per-type logic and this helper handles the path/index walk.
        static void AddTreeData(IGH_Param param, Resthopper.IO.DataTree<ResthopperObject> tree, Func<ResthopperObject, IGH_Goo> convert)
        {
            foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
            {
                GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                for (int i = 0; i < entree.Value.Count; i++)
                    param.AddVolatileData(path, i, convert(entree.Value[i]));
            }
        }

        // Curves arrive in one of two JSON shapes: a Polyline (which we wrap as a PolylineCurve)
        // or a Rhino CommonObject dictionary that FromJSON can re-hydrate into any Curve subtype.
        static IGH_Goo DeserializeCurve(ResthopperObject restobj)
        {
            try
            {
                Polyline data = JsonConvert.DeserializeObject<Polyline>(restobj.Data);
                return new GH_Curve(new PolylineCurve(data));
            }
            catch
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(restobj.Data);
                var c = (Curve)Rhino.Runtime.CommonObject.FromJSON(dict);
                return new GH_Curve(c);
            }
        }

        // Cache the AssignContextualDataTree MethodInfo per contextual-parameter type so we
        // don't pay reflection lookup cost on every input pass through SetInputs.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> assignContextualDataTreeMethods =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo>();

        // Shared write path for SetInputs' contextual-parameter dispatch. Builds a typed
        // Grasshopper.DataTree<T> from the incoming Resthopper tree using the supplied converter,
        // then hands it off to the parameter's AssignContextualDataTree method (cached above).
        // Instance method because GetPath is instance.
        void BuildAndAssignContextualTree<T>(IGH_ContextualParameter param,
                                             Resthopper.IO.DataTree<ResthopperObject> tree,
                                             Func<ResthopperObject, T> convert)
        {
            var inputTree = new Grasshopper.DataTree<T>();
            foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
            {
                GH_Path path = GetPath(entree.Key);
                foreach (var restobj in entree.Value)
                    inputTree.Add(convert(restobj), path);
            }
            var method = assignContextualDataTreeMethods.GetOrAdd(
                param.GetType(),
                t => t.GetMethod("AssignContextualDataTree"));
            method?.Invoke(param, new object[] { inputTree });
        }

        // Text inputs arrive either as JSON-escaped strings or as raw strings with escape
        // sequences; try JSON first, fall back to Regex.Unescape if that fails. Matches the
        // per-item try/catch the original switch had inside the "Text" case.
        static GH_String DeserializeText(ResthopperObject restobj)
        {
            try
            {
                return new GH_String(JsonConvert.DeserializeObject<string>(restobj.Data));
            }
            catch (Exception)
            {
                return new GH_String(System.Text.RegularExpressions.Regex.Unescape(restobj.Data));
            }
        }

        // Geometry contextual inputs (Param_Geometry / GetGeometry parameters) arrive in one of
        // two wire formats depending on the client:
        //   1. CommonObject dictionary-archive JSON — produced by Hops (which calls
        //      GH_Convert.ToGeometryBase before sending) and by other clients that wrap
        //      geometry through CommonObject.ToJSON. Rehydrate via CommonObject.FromJSON.
        //   2. Flat property-bag JSON — produced by non-Hops clients (compute.rhino3d.py,
        //      compute.rhino3d.js, custom callers) when serializing Rhino value-type structs
        //      (Line, Arc, Circle) directly with JsonConvert. These don't derive from
        //      CommonObject and won't rehydrate via FromJSON; deserialize as the raw struct
        //      and wrap in the matching curve type so the downstream pipeline sees a
        //      GeometryBase.
        static IGH_GeometricGoo DeserializeGeometry(ResthopperObject restobj)
        {
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(restobj.Data);
                if (dict != null && Rhino.Runtime.CommonObject.FromJSON(dict) is GeometryBase gb)
                    return GH_Convert.ToGeometricGoo(gb);
            }
            catch (JsonException) { /* not a CommonObject archive — fall through to struct path */ }

            if (TryDeserializeStruct<Circle>(restobj.Data, out var circle) && circle.IsValid)
                return GH_Convert.ToGeometricGoo(new ArcCurve(circle));
            if (TryDeserializeStruct<Arc>(restobj.Data, out var arc) && arc.IsValid)
                return GH_Convert.ToGeometricGoo(new ArcCurve(arc));
            if (TryDeserializeStruct<Line>(restobj.Data, out var line) && line.IsValid)
                return GH_Convert.ToGeometricGoo(new LineCurve(line));

            return null;
        }

        static bool TryDeserializeStruct<T>(string data, out T value) where T : struct
        {
            try { value = JsonConvert.DeserializeObject<T>(data); return true; }
            catch (JsonException) { value = default; return false; }
        }

        public Schema Solve(int rhinoVersion, SchemaDataFormat format)
        {
            HasErrors = false;
            Schema outputSchema = new Schema();
            outputSchema.Algo = string.Empty;

            // solve definition
            Definition.Enabled = true;
            Definition.NewSolution(false, GH_SolutionMode.CommandLine);

            foreach (string msg in ErrorMessages)
            {
                outputSchema.Errors.Add(msg);
            }

            LogRuntimeMessages(Definition.ActiveObjects(), outputSchema);

            // ── BEGIN VEKTORNODE: SELVA — solve outcome ──
            // The plugin decides whether this solve produced a result; compute only carries the
            // verdict and honours it. A blocked or aborted solve returns no values: the client
            // discards them anyway, and serializing them can cost megabytes of display data.
            var selvaOutcome = ReadSelvaOutcome();
            if (selvaOutcome != null)
                outputSchema.Selva = new Newtonsoft.Json.Linq.JObject { ["outcome"] = selvaOutcome };
            bool withholdValues = selvaOutcome != null &&
                ((bool?)selvaOutcome["blocked"] == true || (bool?)selvaOutcome["aborted"] == true);
            if (withholdValues)
            {
                if (outputSchema.Warnings.Count < 1)
                    outputSchema.Warnings = null;
                if (outputSchema.Errors.Count < 1)
                    outputSchema.Errors = null;
                return outputSchema;
            }
            // ── END   VEKTORNODE: SELVA — solve outcome ──

            foreach (var kvp in output)
            {
                var param = kvp.Value;
                if (param == null)
                    continue;

                if (format == SchemaDataFormat.Grasshopper)
                {
                    var data = new GH_Structure<IGH_Goo>();
                    foreach (var item in param.VolatileData.AllData(false))
                    {
                        data.Append(item);
                    }

                    outputSchema.GrasshopperValues.Values.Add(kvp.Key, data);
                }
                else if (format == SchemaDataFormat.Resthopper)
                {
                    // Get data
                    Resthopper.IO.DataTree<ResthopperObject> outputTree = SerializeDataTree(param.VolatileData, param.Attributes.Parent.InstanceGuid, kvp.Key, rhinoVersion) as Resthopper.IO.DataTree<ResthopperObject>;
                    outputSchema.Values.Add(outputTree);
                }
            }

            if (outputSchema.Values.Count < 1 && outputSchema.GrasshopperValues.Values.Count < 1)
                throw new System.Exceptions.PayAttentionException("Looks like you've missed something..."); // TODO

            // Setting warnings and errors to null ever so slightly shrinks down the json sent back to the client
            if (outputSchema.Warnings.Count < 1)
                outputSchema.Warnings = null;
            if (outputSchema.Errors.Count < 1)
                outputSchema.Errors = null;

            return outputSchema;
        }

        // VEKTORNODE: SELVA — solve outcome. Null when the plugin wrote none (no Selva plugin, an
        // older one, or a definition without a Selva component) or the text is not an object.
        private Newtonsoft.Json.Linq.JObject ReadSelvaOutcome()
        {
            try
            {
                if (!Definition.ConstantServer.TryGetValue("SelvaOutcome", out var value))
                    return null;
                var text = value._String;
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                return Newtonsoft.Json.Linq.JObject.Parse(text);
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                Serilog.Log.Warning("SelvaOutcome constant is not a JSON object; ignored");
                return null;
            }
        }

        private static object SerializeDataTree(IGH_Structure data, Guid paramId, string name, int rhinoVersion = 7)
        {
            // Get data
            var outputTree = new Resthopper.IO.DataTree<ResthopperObject>();
            outputTree.ParamName = name;

            foreach (var path in data.Paths)
            {
                var resthopperObjectList = new List<ResthopperObject>();
                foreach (var goo in data.get_Branch(path))
                {
                    if (goo == null)
                        continue;

                    // GH_Surface unwraps to Brep (its .Value is a Brep), preserved from the original.
                    // Unrecognized goo types are silently skipped, matching the original switch's
                    // implicit fall-through to no-op.
                    ResthopperObject resthopperObject = goo switch
                    {
                        GH_Boolean g           => GetResthopperObject<bool>(g.Value, rhinoVersion),
                        GH_Point g             => GetResthopperObject<Point3d>(g.Value, rhinoVersion),
                        GH_Vector g            => GetResthopperObject<Vector3d>(g.Value, rhinoVersion),
                        GH_Integer g           => GetResthopperObject<int>(g.Value, rhinoVersion),
                        GH_Number g            => GetResthopperObject<double>(g.Value, rhinoVersion),
                        GH_String g            => GetResthopperObject<string>(g.Value, rhinoVersion),
                        GH_SubD g              => GetResthopperObject<SubD>(g.Value, rhinoVersion),
                        GH_Line g              => GetResthopperObject<Line>(g.Value, rhinoVersion),
                        GH_Curve g             => GetResthopperObject<Curve>(g.Value, rhinoVersion),
                        GH_Circle g            => GetResthopperObject<Circle>(g.Value, rhinoVersion),
                        GH_Arc g               => GetResthopperObject<Arc>(g.Value, rhinoVersion),
                        GH_Plane g             => GetResthopperObject<Plane>(g.Value, rhinoVersion),
                        GH_Rectangle g         => GetResthopperObject<Rectangle3d>(g.Value, rhinoVersion),
                        GH_Box g               => GetResthopperObject<Box>(g.Value, rhinoVersion),
                        GH_Surface g           => GetResthopperObject<Brep>(g.Value, rhinoVersion),
                        GH_Brep g              => GetResthopperObject<Brep>(g.Value, rhinoVersion),
                        GH_Mesh g              => GetResthopperObject<Mesh>(g.Value, rhinoVersion),
                        GH_Extrusion g         => GetResthopperObject<Extrusion>(g.Value, rhinoVersion),
                        GH_PointCloud g        => GetResthopperObject<PointCloud>(g.Value, rhinoVersion),
                        GH_InstanceReference g => GetResthopperObject<InstanceReferenceGeometry>(g.Value, rhinoVersion),
                        GH_Hatch g             => GetResthopperObject<Hatch>(g.Value, rhinoVersion),
                        GH_LinearDimension g   => GetResthopperObject<LinearDimension>(g.Value, rhinoVersion),
                        GH_RadialDimension g   => GetResthopperObject<RadialDimension>(g.Value, rhinoVersion),
                        GH_AngularDimension g  => GetResthopperObject<AngularDimension>(g.Value, rhinoVersion),
                        GH_OrdinateDimension g => GetResthopperObject<OrdinateDimension>(g.Value, rhinoVersion),
                        GH_Leader g            => GetResthopperObject<Leader>(g.Value, rhinoVersion),
                        GH_TextEntity g        => GetResthopperObject<TextEntity>(g.Value, rhinoVersion),
                        GH_TextDot g           => GetResthopperObject<TextDot>(g.Value, rhinoVersion),
                        GH_Centermark g        => GetResthopperObject<Centermark>(g.Value, rhinoVersion),
                        // ── BEGIN VEKTORNODE: IO-HANDLERS — Selva output types ──
                        GH_Colour g            => GetResthopperObject<Color>(g.Value, rhinoVersion),

                        // Selva SDK seam: any Goo implementing ISelvaSerializableGoo (matched by
                        // interface name, not assembly reference) owns its compute JSON. New Selva
                        // output Goo types plug in here with zero fork changes. See Selva.GH
                        // ISelvaSerializableGoo. The wire Type stays the unwrapped value's type name
                        // so the client demux is unchanged from the previous allowlist behaviour.
                        IGH_Goo gooObj when TryGetSelvaGooJson(gooObj, out string selvaJson)
                            => BuildSelvaResthopperObject(gooObj, selvaJson),

                        // DEPRECATED — DELETE THIS ENTIRE arm in a future major.
                        // Purpose: serialize Selva output Goos from OLD plugin builds (Selva.GH +
                        // selva-canopy) that predate ISelvaSerializableGoo. New Goos match the marker
                        // arm above and never reach here; this only catches an old .gha solved by a
                        // newer compute (the one update-order we can't control).
                        // Safe to remove once every deployed Selva-family plugin ships the marker
                        // (Selva.GH: all four goos; selva-canopy: PlotlyFigure). Nothing in those
                        // plugins needs deleting alongside it — the marker IS the replacement.
                        IGH_Goo gooObj when gooObj.GetType().FullName != null &&
                                            (gooObj.GetType().FullName.IndexOf("WebDisplay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             gooObj.GetType().FullName.IndexOf("FileDataGoo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             gooObj.GetType().FullName.IndexOf("UISchemaGoo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             gooObj.GetType().FullName.IndexOf("PlotlyFigure", StringComparison.OrdinalIgnoreCase) >= 0)
                            => GetResthopperObject<object>(gooObj.GetType().GetProperty("Value")?.GetValue(gooObj), rhinoVersion),
                        // ── END   VEKTORNODE: IO-HANDLERS — Selva output types ──
                        _                      => null
                    };
                    if (resthopperObject != null)
                    {
                        // VEKTORNODE: PARAM-ID — tag every object with its source parameter so the
                        // client can demux outputs by parameter.
                        resthopperObject.Id = paramId;
                        resthopperObjectList.Add(resthopperObject);
                    }
                }
                // preserve paths when returning data
                outputTree.Add(path.ToString(), resthopperObjectList);
            }
            return outputTree;
        }

        // ============================================================================
        // Selva SDK seam (reflection-based, no plugin assembly reference)
        // ============================================================================


        // True when the type (or any implemented interface) is named exactly `interfaceName`. Matched
        // by simple name so every Selva-family plugin can declare its own copy of the contract interface
        // without a shared assembly. See Selva.GH ISelvaOutput / ISelvaSerializableGoo.
        static bool ImplementsSelvaInterface(Type type, string interfaceName)
        {
            foreach (var i in type.GetInterfaces())
            {
                if (i.Name == interfaceName)
                    return true;
            }

            return false;
        }

        // True when a Goo owns its compute wire format (ISelvaSerializableGoo.ToComputeJson()).
        // Unwraps GH_ObjectWrapper: custom Selva goo arrives wrapped on a generic ContextBake input,
        // so the interface check must run against the inner goo, not the wrapper.
        static bool TryGetSelvaGooJson(IGH_Goo goo, out string json)
        {
            json = null;
            if (goo is GH_ObjectWrapper wrapper && wrapper.Value is IGH_Goo innerGoo)
                goo = innerGoo;

            Type t = goo.GetType();
            if (!ImplementsSelvaInterface(t, "ISelvaSerializableGoo"))
                return false;

            json = t.GetMethod("ToComputeJson")?.Invoke(goo, null) as string;
            return json != null;
        }

        // Wraps a Selva-owned JSON payload in a ResthopperObject, deriving the wire Type from the
        // unwrapped inner goo's Value (mirrors TryGetSelvaGooJson's unwrap) so the client demux is
        // unchanged from the previous allowlist behaviour.
        static ResthopperObject BuildSelvaResthopperObject(IGH_Goo goo, string selvaJson)
        {
            var innerGoo = goo is GH_ObjectWrapper w && w.Value is IGH_Goo ig ? ig : goo;
            var value = innerGoo.GetType().GetProperty("Value")?.GetValue(innerGoo);
            return new ResthopperObject
            {
                Type = (value ?? innerGoo).GetType().FullName,
                Data = selvaJson
            };
        }


        private void LogRuntimeMessages(IEnumerable<IGH_ActiveObject> objects, Schema schema)
        {
            foreach (var obj in objects)
            {
                foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                {
                    string errorMsg = $"{msg}: component \"{obj.Name}\" ({obj.InstanceGuid})";
                    LogError(errorMsg);
                    schema.Errors.Add(errorMsg);
                    HasErrors = true;
                }
                // ── BEGIN VEKTORNODE: SELVA FIX — warnings reported outside Debug ──
                // Upstream returns warnings only when Config.Debug is on, so a production server never
                // reports one. Outside Debug they are added here; the block below stays upstream's.
                if (!Config.Debug)
                {
                    foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                        schema.Warnings.Add($"{msg}: component \"{obj.Name}\" ({obj.InstanceGuid})");
                }
                // ── END   VEKTORNODE: SELVA FIX — warnings reported outside Debug ──
                // Warnings go back to callers only in debug mode; debug logging records them either way.
                if (Config.Debug || Log.IsEnabled(LogEventLevel.Debug))
                {
                    foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                    {
                        string warningMsg = $"{msg}: component \"{obj.Name}\" ({obj.InstanceGuid})";
                        if (Config.Debug)
                        {
                            Log.Warning(warningMsg);
                            schema.Warnings.Add(warningMsg);
                        }
                        else
                        {
                            LogDebug($"Warning in grasshopper component: {warningMsg}");
                        }
                    }
                    foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
                    {
                        LogDebug($"Remark in grasshopper component: \"{obj.Name}\" ({obj.InstanceGuid}): {msg}");
                    }
                }
            }
        }

        static string ParamTypeName(IGH_Param param)
        {
            return param.TypeName;
        }

        public string GetIconAsString()
        {
            if (!string.IsNullOrWhiteSpace(iconString))
                return iconString;

            System.Drawing.Bitmap bmp = null;
            if (singularComponent != null)
            {
                bmp = singularComponent.Icon_24x24;
            }

            if (bmp != null)
            {
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] bytes = ms.ToArray();
                    string rc = Convert.ToBase64String(bytes);
                    iconString = rc;
                    return rc;
                }
            }
            return null;
        }

        public IoResponseSchema GetInputsAndOutputs()
        {
            // Parse input and output names
            List<string> inputNames = new List<string>();
            List<string> outputNames = new List<string>();
            var inputs = new List<InputParamSchema>();
            var outputs = new List<IoParamSchema>();

            var sortedInputs = from x in input orderby x.Value.Param.Attributes.Pivot.Y select x;
            var sortedOutputs = from x in output orderby x.Value.Attributes.Pivot.Y select x;

            foreach (var i in sortedInputs)
            {
                inputNames.Add(i.Key);
                var inputSchema = new InputParamSchema
                {
                    Name = i.Key,
                    ParamType = i.Value.TypeName,
                    Description = i.Value.GetDescription(),
                    AtLeast = i.Value.GetAtLeast(),
                    AtMost = i.Value.GetAtMost(),
                    TreeAccess = i.Value.GetTreeAccess(),
                    Default = i.Value.GetDefault(),
                    Minimum = i.Value.GetMinimum(),
                    Maximum = i.Value.GetMaximum(),
                    // VEKTORNODE: PARAM-ID — inputs carry their Instance Guid like outputs do.
                    Id = i.Value.Param.InstanceGuid.ToString(),
                    // VEKTORNODE: IO-HANDLERS — hierarchical group name + enumerated values.
                    GroupName = i.Value.GetGroupName(),
                    Values = i.Value.GetValues(),
                };
                if (singularComponent != null)
                {
                    inputSchema.Description = i.Value.Param.Description;
                    if (i.Value.Param.Access == GH_ParamAccess.item)
                    {
                        inputSchema.AtMost = inputSchema.AtLeast;
                    }
                }
                inputs.Add(inputSchema);
            }

            foreach (var o in sortedOutputs)
            {
                outputNames.Add(o.Key);
                outputs.Add(new IoParamSchema
                {
                    Name = o.Key,
                    ParamType = outputTypeNames[o.Key],
                    // VEKTORNODE: PARAM-ID — output objects are looked up by Id client-side, so the
                    // schema must carry the same Instance Guid that ResthopperObject.Id is tagged with.
                    Id = o.Value.InstanceGuid.ToString()
                });
            }

            string description = singularComponent == null ?
                Definition.Properties.Description :
                singularComponent.Description;

            return new IoResponseSchema
            {
                Description = description,
                InputNames = inputNames,
                OutputNames = outputNames,
                Inputs = inputs,
                Outputs = outputs
            };
        }

        public static GH_Archive ArchiveFromUrl(string url, out byte[] contents)
        {
            contents = null;
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (File.Exists(url))
            {
                // local file
                var archive = new GH_Archive();
                if (archive.ReadFromFile(url))
                {
                    RegisterFileWatcher(url);
                    try
                    {
                        contents = File.ReadAllBytes(url);
                    }
                    catch (IOException) { }
                    return archive;
                }
                return null;
            }

            if (UrlGuard.IsWebUrl(url))
            {
                // UrlGuard validates scheme + optional private-IP block, then streams the
                // response with a size cap from Config.MaxRequestSize. HttpClientHelper.Client
                // (used by UrlGuard under the hood) has GZip/Deflate decompression enabled.
                // Unwrap the AggregateException that .Result wraps around the real failure so
                // the clean inner message (e.g. "The server responded with 503 ...") surfaces
                // instead of "One or more errors occurred. (...)".
                byte[] byteArray;
                try
                {
                    byteArray = UrlGuard.GetByteArrayAsync(url, Config.MaxRequestSize).Result;
                }
                catch (AggregateException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
                contents = byteArray;

                try
                {
                    var byteArchive = new GH_Archive();
                    if (byteArchive.Deserialize_Binary(byteArray))
                        return byteArchive;
                }
                catch (Exception) { }

                var grasshopperXml = StripBom(System.Text.Encoding.UTF8.GetString(byteArray));
                if (ArchiveFromXml(grasshopperXml) is GH_Archive xmlArchive)
                    return xmlArchive;
            }
            return null;
        }

        public static GH_Archive ArchiveFromBase64String(string blob, out byte[] contents)
        {
            contents = null;
            if (string.IsNullOrWhiteSpace(blob))
                return null;

            contents = Convert.FromBase64String(blob);
            return ArchiveFromBytes(contents);
        }

        static GH_Archive ArchiveFromBytes(byte[] byteArray)
        {
            try
            {
                var byteArchive = new GH_Archive();
                if (byteArchive.Deserialize_Binary(byteArray))
                    return byteArchive;
            }
            catch (Exception) { }

            var grasshopperXml = StripBom(System.Text.Encoding.UTF8.GetString(byteArray));
            return ArchiveFromXml(grasshopperXml);
        }

        // Content that isn't a definition in either format is null, like a binary archive that won't read.
        static GH_Archive ArchiveFromXml(string xml)
        {
            var archive = new GH_Archive();
            try
            {
                return archive.Deserialize_Xml(xml) ? archive : null;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        // strip bom from string -- [239, 187, 191] in byte array == (char)65279
        // https://stackoverflow.com/a/54894929/1902446
        static string StripBom(string str)
        {
            if (!string.IsNullOrEmpty(str) && str[0] == (char)65279)
                str = str.Substring(1);
            return str;
        }

        static ResthopperObject GetResthopperObject<T>(object goo, int rhinoVerion)
        {
            var v = (T)goo;
            ResthopperObject rhObj = new ResthopperObject();
            rhObj.Type = goo.GetType().FullName;

            if (v is GeometryBase geometry)
                rhObj.Data = geometry.ToJSON(new Rhino.FileIO.SerializationOptions() { RhinoVersion = rhinoVerion });
            else
                rhObj.Data = JsonConvert.SerializeObject(v, GeometryResolver.Settings(rhinoVerion));

            return rhObj;
        }

        class InputGroup
        {
            object defaultValue = null;
            public InputGroup(IGH_Param param)
            {
                Param = param;

                param.ClearData();
                param.CollectData();
                defaultValue = SerializeDataTree(param.VolatileData, Param.InstanceGuid, param.Name);
                TypeName = ParamTypeName(param);
            }

            public IGH_Param Param { get; }
            public string TypeName { get; }

            public string GetDescription()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    return contextualParameter.Prompt;
                }
                return null;
            }

            public int GetAtLeast()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    return contextualParameter.AtLeast;
                }
                return 1;
            }

            public int GetAtMost()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    return contextualParameter.AtMost;
                }
                if (Param is GH_NumberSlider)
                    return 1;
                return int.MaxValue;
            }

            public bool GetTreeAccess()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    var result = contextualParameter.GetType().GetProperty("TreeAccess")?.GetValue(contextualParameter, null);
                    if (result != null)
                        return (bool)result;
                }
                return false;
            }

            public object GetDefault()
            {
                // VEKTORNODE: IO-HANDLERS — a contextual parameter may declare its own default
                // (e.g. the pre-selected entry of a ValueList). The upstream fallback below derives
                // the default from the serialized data tree, which has no notion of a selection, so
                // without this a ValueList ships its options with nothing selected.
                if (Param is IGH_ContextualParameter contextualParam)
                {
                    var method = contextualParam.GetType().GetMethod("GetDefaultValue");
                    if (method != null)
                    {
                        var result = method.Invoke(contextualParam, null);
                        if (result != null)
                            return result; // Return the string directly
                    }
                }

                // Fall back to original behavior (serialize data tree)
                return defaultValue;
            }

            public double? GetMinimum()
            {
                var p = Param;
                if (p is IGH_ContextualParameter)
                {
                    var par = p as IGH_ContextualParameter;
                    var pTypeName = TypeName;
                    var pType = par.GetType();
                    var props = pType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);
                    var info = props.FirstOrDefault(x => x.Name == "Minimum");
                    if (info != null)
                    {
                        var val = info.GetValue(par, null);
                        if (val != null)
                        {
                            var min = Convert.ToDouble(val);
                            if (pTypeName == "Integer")
                            {
                                if (min > int.MinValue + Rhino.RhinoMath.Epsilon)
                                    return min;
                            }
                            else if (pTypeName == "Number")
                            {
                                if (min > double.MinValue + Rhino.RhinoMath.Epsilon)
                                    return min;
                            }
                        }
                    }

                    if (p.Sources.Count == 1)
                        p = p.Sources[0];
                }

                if (p is GH_NumberSlider paramSlider)
                    return (double)paramSlider.Slider.Minimum;
                return null;
            }

            public double? GetMaximum()
            {
                var p = Param;
                if (p is IGH_ContextualParameter)
                {
                    var par = p as IGH_ContextualParameter;
                    var pType = par.GetType();
                    var pTypeName = TypeName;
                    var props = pType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);
                    var info = props.FirstOrDefault(x => x.Name == "Maximum");
                    if (info != null)
                    {
                        var val = info.GetValue(par, null);
                        if (val != null)
                        {
                            var max = Convert.ToDouble(val);
                            if (pTypeName == "Integer")
                            {
                                if (max < int.MaxValue - Rhino.RhinoMath.Epsilon)
                                    return max;
                            }
                            else if (pTypeName == "Number")
                            {
                                if (max < double.MaxValue - Rhino.RhinoMath.Epsilon)
                                    return max;
                            }
                        }
                    }

                    if (p.Sources.Count == 1)
                        p = p.Sources[0];
                }

                if (p is GH_NumberSlider paramSlider)
                    return (double)paramSlider.Slider.Maximum;

                return null;
            }

            /// <summary>
            /// Gets the full hierarchical group name for the parameter (e.g., "Layer_1::Layer_2::Layer_3")
            /// </summary>
            /// <returns>
            /// The group name as a string, or null if not found.
            /// </returns>
            public string GetGroupName()
            {
                var document = Param.OnPingDocument();
                if (document == null)
                    return null;

                var allGroups = document.Objects.OfType<GH_Group>().ToList();

                // Find all groups that contain this parameter (directly or through nested groups)
                var allGroupsContainingParam = allGroups
                    .Where(g => !string.IsNullOrEmpty(g.NickName) &&
                                GroupContainsParameterRecursively(g, Param.InstanceGuid, allGroups))
                    .ToList();

                if (!allGroupsContainingParam.Any())
                    return null;

                if (allGroupsContainingParam.Count == 1)
                    return allGroupsContainingParam.First().NickName;

                // Build hierarchy by finding parent-child relationships
                var hierarchy = BuildGroupHierarchy(allGroupsContainingParam);

                return string.Join("::", hierarchy.Select(g => g.NickName));
            }

            public Dictionary<string, string> GetValues()
            {
                if (Param is IGH_ContextualParameter contextualParam)
                {
                    var pType = contextualParam.GetType();
                    var props = pType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    var valuesInfo = props.FirstOrDefault(x => x.Name == "Values");
                    if (valuesInfo != null)
                    {
                        var val = valuesInfo.GetValue(contextualParam, null);
                        if (val is Dictionary<string, string> dict)
                            return dict;
                    }
                }

                return null;
            }

            /// <summary>
            /// Recursively checks if a group contains a parameter (directly or through nested child groups)
            /// </summary>
            private bool GroupContainsParameterRecursively(GH_Group group, Guid paramGuid, List<GH_Group> allGroups)
            {
                // Check if parameter is directly in this group
                if (group.Objects().Any(obj => obj.InstanceGuid == paramGuid))
                    return true;

                // Check if any child groups contain the parameter
                var childGroups = allGroups
                    .Where(g => g.InstanceGuid != group.InstanceGuid &&
                                group.Objects().Any(obj => obj.InstanceGuid == g.InstanceGuid))
                    .ToList();

                foreach (var childGroup in childGroups)
                {
                    if (GroupContainsParameterRecursively(childGroup, paramGuid, allGroups))
                        return true;
                }

                return false;
            }

            private List<GH_Group> BuildGroupHierarchy(List<GH_Group> groups)
            {
                var hierarchy = new List<GH_Group>();
                var remaining = new List<GH_Group>(groups);

                // Start with groups that are not contained in any other group (root groups)
                while (remaining.Any())
                {
                    var rootGroup = remaining.FirstOrDefault(g =>
                        !remaining.Any(other => other != g && GroupContainsGroup(other, g)));

                    if (rootGroup == null)
                    {
                        // Fallback: if we can't determine hierarchy, just return the first group
                        hierarchy.AddRange(remaining);
                        break;
                    }

                    hierarchy.Add(rootGroup);
                    remaining.Remove(rootGroup);

                    // Now find the next level - groups that are directly contained in the rootGroup
                    var childGroups = remaining.Where(g => GroupContainsGroup(rootGroup, g)).ToList();

                    // If there are child groups, continue with the most nested one
                    if (childGroups.Any())
                    {
                        // Remove child groups from remaining as we'll process them next
                        foreach (var child in childGroups)
                            remaining.Remove(child);

                        // Continue with child groups (recursive approach)
                        var childHierarchy = BuildGroupHierarchy(childGroups);
                        hierarchy.AddRange(childHierarchy);
                        break; // We've found our path
                    }
                }

                var filteredHierarchy = hierarchy.Where(g => !g.NickName.Contains("RH_IN")).ToList();

                return filteredHierarchy;
            }

            private bool GroupContainsGroup(GH_Group parentGroup, GH_Group childGroup)
            {
                // Check if parentGroup contains childGroup as one of its objects
                return parentGroup.Objects().Any(obj => obj.InstanceGuid == childGroup.InstanceGuid);
            }

            public bool AlreadySet(Resthopper.IO.DataTree<ResthopperObject> tree)
            {
                if (this.tree == null)
                    return false;

                var oldDictionary = this.tree.InnerTree;
                var newDictionary = tree.InnerTree;

                if (!oldDictionary.Keys.SequenceEqual(newDictionary.Keys))
                {
                    return false;
                }

                foreach (var kvp in oldDictionary)
                {
                    var oldValue = kvp.Value;
                    if (!newDictionary.TryGetValue(kvp.Key, out List<ResthopperObject> newValue))
                        return false;

                    if (!newValue.SequenceEqual(oldValue))
                    {
                        return false;
                    }
                }

                return true;
            }

            public void CacheTree(Resthopper.IO.DataTree<ResthopperObject> tree)
            {
                this.tree = tree;
            }

            Resthopper.IO.DataTree<ResthopperObject> tree;
        }
    }
}
