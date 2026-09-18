using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;

namespace compute.geometry
{
    // VEKTORNODE: SELVA — entire file. Supports the grasshopper/schema endpoints
    // (FixedEndpoints.cs) that extract embedded UI Builder schemas from Selva definitions.
    // Not present in upstream.
    //
    // UNTYPED COUPLING TO THE SELVA PLUGIN. This assembly cannot reference Selva.GH, so the
    // component type and its schema field are matched by literal name via reflection:
    //
    //     "GH_UIBuilderComponent"  — Selva.GH/Features/UIBuilder/Components/GH_UIBuilderComponent.cs
    //     "ContextBakeComponent"   — VektorNode GH library
    //     "_embeddedSchema"        — private field on GH_UIBuilderComponent
    //
    // Renaming any of those on the plugin side compiles clean there and silently breaks schema
    // extraction here. Both lookups deliberately walk the base chain rather than matching the leaf
    // type — see IsComponentOfType and GetEmbeddedSchema for why (OBSOLETE_* subclasses).
    internal static class GrasshopperValidationHelper
    {
        static readonly HttpClient _http = new HttpClient();

        public static async Task<GH_Archive> ArchiveFromUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Local file path
                if (!File.Exists(url))
                    throw new FileNotFoundException($"File not found: {url}");
                var archive = new GH_Archive();
                if (archive.ReadFromFile(url))
                    return archive;
                return null;
            }

            var bytes = await _http.GetByteArrayAsync(url);
            return ArchiveFromBytes(bytes);
        }

        public static GH_Archive ArchiveFromBytes(byte[] byteArray)
        {
            try
            {
                var byteArchive = new GH_Archive();
                if (byteArchive.Deserialize_Binary(byteArray))
                    return byteArchive;
            }
            catch (Exception) { }

            var xmlArchive = new GH_Archive();
            if (xmlArchive.Deserialize_Xml(System.Text.Encoding.UTF8.GetString(byteArray)))
                return xmlArchive;

            return null;
        }

        public static GH_Document DocumentFromArchive(GH_Archive archive)
        {
            var doc = new GH_Document();
            return archive.ExtractObject(doc, "Definition") ? doc : null;
        }

        /// <summary>
        ///     Whether <paramref name="obj" /> is of the named Selva component type, or derives from it.
        ///
        ///     Must walk the base chain, not compare the leaf name: when Selva makes a breaking param
        ///     change it pins the old shape as an OBSOLETE_* subclass of the live component (e.g.
        ///     OBSOLETE_UIBridge_UntilV0_15_4 : GH_UIBuilderComponent) so existing .gh files keep
        ///     loading. Those files deserialize into the subclass, and the GH-side IGH_UpgradeObject
        ///     only runs on an interactive right-click → Upgrade — never here, where documents are
        ///     deserialized headlessly. An exact leaf-name match therefore rejects every definition
        ///     saved before the breaking change, permanently.
        ///
        ///     Name-based because compute cannot reference Selva.GH; `is` is unavailable.
        /// </summary>
        public static bool IsComponentOfType(IGH_DocumentObject obj, string typeName)
        {
            for (var t = obj?.GetType(); t != null; t = t.BaseType)
            {
                if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static List<GH_Component> GetSchemaContextBakeComponents(GH_Document doc)
        {
            // First try to find ContextBakeComponent (requires the plugin to be loaded)
            var viaBake = doc.Objects
                .Where(o => IsComponentOfType(o, "ContextBakeComponent"))
                .OfType<GH_Component>()
                .Where(c => c.Params.Input.Count > 0
                    && c.Params.Input[0].Sources.Any(s => s.NickName == "Schema"))
                .ToList();

            if (viaBake.Count > 0)
                return viaBake;

            // Fallback: find GH_UIBuilderComponent directly (Context Bake plugin may not be installed)
            return doc.Objects
                .Where(o => IsComponentOfType(o, "GH_UIBuilderComponent"))
                .OfType<GH_Component>()
                .ToList();
        }

        public static IGH_DocumentObject GetSchemaParentComponent(GH_Component contextBakeOrUiBuilder)
        {
            // If this is already a GH_UIBuilderComponent, return it directly
            if (IsComponentOfType(contextBakeOrUiBuilder, "GH_UIBuilderComponent"))
                return contextBakeOrUiBuilder;

            var source = contextBakeOrUiBuilder.Params.Input[0].Sources.FirstOrDefault(s => s.NickName == "Schema");
            return source?.Attributes?.GetTopLevel?.DocObject;
        }

        /// <summary>
        ///     Reads the UI Builder's private _embeddedSchema field.
        ///
        ///     Must walk the base chain: GetField with NonPublic|Instance searches only the exact
        ///     type, and private fields on base classes are deliberately excluded from that lookup.
        ///     A definition saved before a breaking param change deserializes into an OBSOLETE_*
        ///     subclass which inherits (but does not declare) the field, so a single-level lookup
        ///     returns null and the schema reads as missing even though it loaded fine.
        /// </summary>
        public static object GetEmbeddedSchema(IGH_DocumentObject uiBuilderComponent)
        {
            for (var t = uiBuilderComponent?.GetType(); t != null; t = t.BaseType)
            {
                var field = t.GetField("_embeddedSchema",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(uiBuilderComponent);
            }

            return null;
        }

        /// <summary>
        ///     Serializes an embedded UISchema to its canonical compute wire shape.
        ///
        ///     Must delegate to the plugin's own ISelvaSerializableGoo.ToComputeJson() rather than
        ///     calling JsonConvert here. Selva internalizes its Newtonsoft into Selva.dll, so the
        ///     [JsonProperty("id")] attributes on UISchema are stamped with a JsonPropertyAttribute
        ///     type from a different assembly identity than the Newtonsoft this assembly loads. Our
        ///     serializer does not recognize them, silently falls back to raw CLR member names, and
        ///     emits PascalCase ("Inputs", "Layout") — which every Selva client reads as a schema
        ///     with no inputs. The plugin's own serializer sees its own attributes and settings.
        ///
        ///     Returns null when the seam is unavailable, so callers can fail loudly instead of
        ///     shipping a mis-cased schema. See Selva.GH ISelvaSerializableGoo / UISchemaGoo.
        /// </summary>
        public static JObject SchemaToJson(object schema)
        {
            if (schema == null)
                return null;

            var json = TryGetSchemaComputeJson(schema);
            if (json == null)
                return null;

            return JObject.Parse(json);
        }

        // Wraps the bare UISchema in the plugin's UISchemaGoo (located in the schema's own assembly,
        // by name — this assembly cannot reference Selva.GH) and asks it for its wire format.
        static string TryGetSchemaComputeJson(object schema)
        {
            var gooType = schema.GetType().Assembly
                              .GetType("Selva.GH.Features.UIBuilder.Goos.UISchemaGoo")
                          ?? AppDomain.CurrentDomain.GetAssemblies()
                              .Select(a => SafeGetType(a, "Selva.GH.Features.UIBuilder.Goos.UISchemaGoo"))
                              .FirstOrDefault(t => t != null);

            if (gooType == null)
                return null;

            var goo = Activator.CreateInstance(gooType, schema);
            return gooType.GetMethod("ToComputeJson")?.Invoke(goo, null) as string;
        }

        static Type SafeGetType(Assembly assembly, string fullName)
        {
            try { return assembly.GetType(fullName); }
            catch { return null; }
        }

        // Serializes a list of schema parameters (inputs or outputs) into a JArray.
        // Each item is reflected to extract all simple-value properties (primitives, strings, enums).
        private static JArray SerializeParamList(System.Collections.IList list)
        {
            var arr = new JArray();
            if (list == null) return arr;
            foreach (var item in list)
                if (item != null) arr.Add(SerializeSchemaParam(item));
            return arr;
        }

        private static JObject SerializeSchemaParam(object param)
        {
            var obj = new JObject();
            foreach (var prop in param.GetType()
                                      .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                      .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                try
                {
                    var val = prop.GetValue(param);
                    if (val == null) continue;
                    var vt = val.GetType();
                    if (vt.IsPrimitive || vt == typeof(string) || vt.IsEnum)
                        obj[prop.Name] = JToken.FromObject(val);
                }
                catch { /* skip unreadable or unsupported properties */ }
            }
            return obj;
        }

        public static JObject ErrorResult(string fileName, string message) => new JObject
        {
            ["fileName"] = fileName,
            ["error"]    = message
        };

        public static JObject SuccessResult(string fileName, JArray schemas) => new JObject
        {
            ["fileName"] = fileName,
            ["schemas"]  = schemas
        };
    }
}
