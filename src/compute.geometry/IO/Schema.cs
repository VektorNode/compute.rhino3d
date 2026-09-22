using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Rhino.Geometry;

namespace Resthopper.IO
{
    public class Schema
    {
        public Schema() { }

        [JsonProperty(PropertyName = "absolutetolerance", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double AbsoluteTolerance { get; set; } = 0;

        [JsonProperty(PropertyName = "angletolerance", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double AngleTolerance { get; set; } = 0;

        [JsonProperty(PropertyName = "modelunits")]
        public string ModelUnits { get; set; } = Rhino.UnitSystem.Millimeters.ToString();

        // Rhino version of data to be serialized and returned to the client
        [JsonProperty(PropertyName = "dataversion")]
        public int DataVersion { get; set; } = 7;

        [JsonProperty(PropertyName = "algo")]
        public string Algo { get; set; }

        [JsonProperty(PropertyName = "filename")]
        public string FileName { get; set; }

        [JsonProperty(PropertyName = "pointer")]
        public string Pointer { get; set; }

        // If true on input, the solve results are cached based on this schema.
        // When true the cache is searched for already computed results and used
        [JsonProperty(PropertyName = "cachesolve")]
        public bool CacheSolve { get; set; } = false;

        // VEKTORNODE: CACHE-ERRORED-SOLVES — opt-in. By default an errored solve
        // (definition.HasErrors) is never cached, because an error usually means a
        // bad result. But many definitions throw GH errors BY DESIGN (a guarded
        // Python component, a filtered/branch-pruned component) while still
        // producing correct geometry. For those, the author can set this to true so
        // the completed result is cached despite the errors. Only honored together
        // with CacheSolve. Does NOT change the HTTP status (an errored solve still
        // returns 500); it only allows the result into the solve cache.
        [JsonProperty(PropertyName = "cacheerroredsolves")]
        public bool CacheErroredSolves { get; set; } = false;

        // Used for nested calls
        [JsonProperty(PropertyName = "recursionlevel", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int RecursionLevel { get; set; } = 0;

        [JsonProperty(PropertyName = "values")]
        public List<DataTree<ResthopperObject>> Values { get; set; } = new List<DataTree<ResthopperObject>>();

        // Return warnings from GH
        [JsonProperty(PropertyName = "warnings", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public List<string> Warnings { get; set; } = new List<string>();

        // Return errors from GH
        [JsonProperty(PropertyName = "errors", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public List<string> Errors { get; set; } = new List<string>();

        // VEKTORNODE: SELVA — live events. Where a Selva-family plugin running inside this
        // solve may POST mid-solve events. Compute never sends anything itself; it hands the
        // three values to the document as constants (see GrasshopperSolveHelper) and the plugin
        // does the networking. Input-only; never echoed back.
        [JsonProperty(PropertyName = "selvaevents", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public SelvaEventTarget SelvaEvents { get; set; }
    }

    // VEKTORNODE: SELVA — live events.
    public class SelvaEventTarget
    {
        [JsonProperty(PropertyName = "url")]
        public string Url { get; set; }

        [JsonProperty(PropertyName = "solveId")]
        public string SolveId { get; set; }

        [JsonProperty(PropertyName = "token")]
        public string Token { get; set; }
    }

    public class IoQuerySchema
    {
        [JsonProperty(PropertyName = "requestedFile")]
        public string RequestedFile { get; set; }

    }

    public class IoParamSchema
    {
        public string Name { get; set; }
        public string Nickname { get; set; }
        public string ParamType { get; set; }

        // VEKTORNODE: PARAM-ID — Instance Guid of the source Grasshopper parameter, used to
        // associate schema entries and Resthopper objects with their originating param.
        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; }
    }

    public class InputParamSchema : IoParamSchema
    {
        public string Description { get; set; }
        public int AtLeast { get; set; } = 1;
        public int AtMost { get; set; } = int.MaxValue;
        public bool TreeAccess { get; set; } = false;
        public object Default { get; set; } = null;
        public object Minimum { get; set; } = null;
        public object Maximum { get; set; } = null;

        // VEKTORNODE: IO-HANDLERS — extra input metadata (UI grouping + enumerated values).
        [JsonProperty(PropertyName = "groupName")]
        public string GroupName { get; set; } = null;

        [JsonProperty(PropertyName = "values")]
        public Dictionary<string, string> Values { get; set; } = null;
    }

    public class IoResponseSchema
    {
        public string Description { get; set; }
        public string FileName { get; set; }
        public string CacheKey { get; set; }
        public List<string> InputNames { get; set; }
        public List<string> OutputNames { get; set; }
        public string Icon { get; set; }
        public List<InputParamSchema> Inputs { get; set; }
        public List<IoParamSchema> Outputs { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class HttpRecord
    {
        public HttpRecord()
        {

        }
        public string IoRequest { get; set; }
        public string IoResponse { get; set; }
        public string SolveRequest { get; set; }
        public string SolveResponse { get; set; }
        public Schema Schema { get; set; }
        public IoResponseSchema IoResponseSchema { get; set; }
    }

    public class ResthopperObject : IEquatable<ResthopperObject>
    {
        [JsonProperty(PropertyName = "type")]
        public string Type { get; set; }

        [JsonProperty(PropertyName = "data")]
        public string Data { get; set; }

        [JsonIgnore]
        public object ResolvedData { get; set; }

        // VEKTORNODE: PARAM-ID — Instance Guid of the source Grasshopper parameter this object came from.
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        [JsonConstructor]
        public ResthopperObject()
        {
        }

        public ResthopperObject(object obj)
        {
            if (obj is GeometryBase geometry)
            {
                Data = geometry.ToJSON(new Rhino.FileIO.SerializationOptions() { RhinoVersion = 7 });
            }
            else
            {
#if COMPUTE_CORE
                Data = JsonConvert.SerializeObject(obj, compute.geometry.GeometryResolver.Settings);
#else
                Data = JsonConvert.SerializeObject(obj);//, compute.geometry.GeometryResolver.Settings);
#endif
            }
            Type = obj.GetType().FullName;
        }

        public bool Equals(ResthopperObject other)
        {
            return string.Equals(Type, other.Type) && string.Equals(Data, other.Data);
        }
    }
}
