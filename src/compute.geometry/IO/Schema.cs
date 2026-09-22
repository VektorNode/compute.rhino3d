using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Rhino.Geometry;

namespace Resthopper.IO
{
    public enum SchemaDataFormat
    {
        Resthopper = 0,
        Grasshopper = 1
    }
    public class Schema
    {
        public Schema() { }

        [JsonProperty(PropertyName = "absolutetolerance", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double AbsoluteTolerance { get; set; } = 0;

        [JsonProperty(PropertyName = "angletolerance", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double AngleTolerance { get; set; } = 0;

        [JsonProperty(PropertyName = "modelunits")]
        public string ModelUnits { get; set; } = Rhino.UnitSystem.Millimeters.ToString();

        // Rhino version of data that the server is capable of processing
        [JsonProperty(PropertyName = "dataversion")]
        public int DataVersion { get; set; } = 7;

        // Format of the data that the server is capable of processing
        [JsonProperty(PropertyName = "dataformat")]
        public SchemaDataFormat DataFormat { get; set; } = SchemaDataFormat.Resthopper;

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

        [JsonProperty(PropertyName = "values-grasshopper")]
        public GrasshopperValues GrasshopperValues { get; set; } = new GrasshopperValues();

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
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("nickname")]
        public string Nickname { get; set; }

        [JsonProperty("paramType")]
        public string ParamType { get; set; }
        
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public class InputParamSchema : IoParamSchema
    {
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("atLeast")]
        public int AtLeast { get; set; } = 1;

        [JsonProperty("atMost")]
        public int AtMost { get; set; } = int.MaxValue;

        [JsonProperty("treeAccess")]
        public bool TreeAccess { get; set; } = false;

        [JsonProperty("default")]
        public object Default { get; set; } = null;

        [JsonProperty("minimum")]
        public object Minimum { get; set; } = null;

        [JsonProperty("maximum")]
        public object Maximum { get; set; } = null;

        [JsonProperty("groupName")]
        public string GroupName { get; set; } = null;

        [JsonProperty("values")]
        public Dictionary<string, string> Values { get; set; } = null;
    }

    public class IoResponseSchema
    {
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("filename")]
        public string FileName { get; set; }

        [JsonProperty("cachekey")]
        public string CacheKey { get; set; }

        [JsonProperty("inputnames")]
        public List<string> InputNames { get; set; }

        [JsonProperty("outputnames")]
        public List<string> OutputNames { get; set; }

        [JsonProperty("icon")]
        public string Icon { get; set; }

        [JsonProperty("inputs")]
        public List<InputParamSchema> Inputs { get; set; }

        [JsonProperty("outputs")]
        public List<IoParamSchema> Outputs { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonProperty("errors")]
        public List<string> Errors { get; set; } = new List<string>();

        // List of supported data formats from the server
        [JsonProperty(PropertyName = "supporteddataformats")]
        public List<SchemaDataFormat> SupportedDataFormats { get; set; } = new List<SchemaDataFormat>();
    }

    public class HttpRecord
    {
        public HttpRecord()
        {

        }
        [JsonProperty("iorequest")]
        public string IoRequest { get; set; }

        [JsonProperty("ioresponse")]
        public string IoResponse { get; set; }

        [JsonProperty("solverequest")]
        public string SolveRequest { get; set; }

        [JsonProperty("solveresponse")]
        public string SolveResponse { get; set; }

        [JsonProperty("schema")]
        public Schema Schema { get; set; }

        [JsonProperty("ioresponseschema")]
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
