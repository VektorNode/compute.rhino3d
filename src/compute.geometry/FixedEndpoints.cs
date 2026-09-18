using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Linq;
using GH_IO.Serialization;
using Newtonsoft.Json.Linq;
using Rhino.PlugIns;
using Serilog;

namespace compute.geometry
{
    public static class FixedEndPointsModule
    {
        public static void MapEndpoints(IEndpointRouteBuilder app)
        {
            app.MapGet("", HomePage);
            app.MapGet("version", GetVersion);
            app.MapGet("servertime", ServerTime);
            app.MapGet("plugins/rhino/installed", GetInstalledPluginsRhino);
            app.MapGet("plugins/gh/installed", GetInstalledPluginsGrasshopper);
            app.MapGet("grasshopper/schema", GetGrasshopperSchema);
            app.MapPost("grasshopper/schema", GetGrasshopperSchema);
            app.MapPost("grasshopper/schema/url", GetGrasshopperSchemaFromUrl);
            app.MapPost("cache/purge", PurgeCache);
            app.MapPost("shutdown", ShutdownChild);
        }

        // POST /shutdown — graceful self-shutdown for this compute.geometry child. Auth-gated
        // by ApiKeyMiddleware (POST). Used by rhino.compute when its ApplicationStopping
        // lifecycle hook fires (clean parent exit), and by the /shutdown-children +
        // /recycle-children endpoints in rhino.compute for manual control. The existing
        // 5-second self-monitoring TimerTask in Shutdown.cs remains as the fallback for
        // hard-crash scenarios where the parent dies without running its lifecycle hooks.
        //
        // Responds 202 Accepted immediately and triggers app.StopAsync() on a background
        // task so the response can flush before the host stops.
        static Task ShutdownChild(HttpContext ctx)
        {
            Serilog.Log.Information("Received /shutdown request from {RemoteIp}", ctx.Connection.RemoteIpAddress);
            var lifetime = ctx.RequestServices.GetService<IHostApplicationLifetime>();
            ctx.Response.StatusCode = 202;
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);  // let the response flush before stopping the host
                lifetime?.StopApplication();
            });
            return Task.CompletedTask;
        }

        static void HomePage(HttpContext context)
        {
            context.Response.Redirect("https://www.rhino3d.com/compute");
        }

        static async Task GetVersion(HttpContext ctx)
        {
            var values = new Dictionary<string, string>
            {
                { "rhino", Rhino.RhinoApp.Version.ToString() },
                { "compute", Assembly.GetExecutingAssembly().GetName().Version.ToString() }
            };
            string git_sha = null; // appveyor will replace this
            values.Add("git_sha", git_sha);

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(values);
        }

        static async Task ServerTime(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(DateTime.UtcNow);
        }

        static async Task GetInstalledPluginsRhino(HttpContext ctx)
        {
            var rhPluginInfo = new SortedDictionary<string, string>();
            foreach (var k in Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Keys)
            {
                var info = Rhino.PlugIns.PlugIn.GetPlugInInfo(k);
                //Could also use: info.IsLoaded
                if (info != null && !rhPluginInfo.ContainsKey(info.Name) && !info.ShipsWithRhino)
                {
                    rhPluginInfo.Add(info.Name, info.Version);
                }
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(rhPluginInfo);
        }

        // POST /cache/purge — wipes the solve-results / URL-data cache. Does NOT touch
        // the definition cache, since active clients may hold Pointer references to
        // entries there and a purge would cause subsequent /grasshopper calls with those
        // pointers to fail. Operators expecting memory relief without breaking pointer-
        // based flows should use this endpoint. Auth-gated by ApiKeyMiddleware (POST
        // method requires the RhinoComputeKey header when Config.ApiKey is set).
        static async Task PurgeCache(HttpContext ctx)
        {
            long removed = DataCache.PurgeSolveResults();
            Serilog.Log.Information("Cache purge requested: removed {Count} solve-results / URL-data entries", removed);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { purged = removed });
        }

        static async Task GetInstalledPluginsGrasshopper(HttpContext ctx)
        {
            var ghPluginInfo = new SortedDictionary<string, string>();
            foreach (var obj in Grasshopper.Instances.ComponentServer.ObjectProxies.Where(o => o != null))
            {
                var asm = Grasshopper.Instances.ComponentServer.FindAssemblyByObject(obj.Guid);
                if (asm != null && !string.IsNullOrEmpty(asm.Name) && !asm.IsCoreLibrary && !ghPluginInfo.ContainsKey(asm.Name))
                {
                    var version = (string.IsNullOrEmpty(asm.Version)) ? asm.Assembly.GetName().Version.ToString() : asm.Version;
                    ghPluginInfo.Add(asm.Name, version);
                }
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(ghPluginInfo);
        }

        static async Task GetGrasshopperSchema(HttpContext ctx)
        {
            Log.Debug("grasshopper/schema: method={Method} hasForm={HasForm} fileCount={FileCount}",
                ctx.Request.Method, ctx.Request.HasFormContentType, ctx.Request.HasFormContentType ? ctx.Request.Form.Files.Count : 0);

            if (ctx.Request.Method == "GET" || !ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
            {
                Log.Debug("grasshopper/schema: returning endpoint description (no files uploaded)");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    endpoint = "grasshopper/schema",
                    status = "available",
                    description = "Extracts the embedded schema from Grasshopper definition files (.gh/.ghx). Returns the full schema (inputs, outputs, metadata) when the definition is correctly wired (Context Bake → UI Builder with an embedded schema).",
                    usage = "POST multipart/form-data with one or more .gh or .ghx files."
                }));
                return;
            }

            var results = new List<JObject>();

            foreach (var file in ctx.Request.Form.Files)
            {
                var fileName = !string.IsNullOrEmpty(file.FileName) ? file.FileName : file.Name;
                Log.Debug("grasshopper/schema: processing file={FileName} size={Size}", fileName, file.Length);
                try
                {
                    using var stream = file.OpenReadStream();
                    using var mem = new MemoryStream();
                    stream.CopyTo(mem);

                    var archive = GrasshopperValidationHelper.ArchiveFromBytes(mem.ToArray());
                    results.Add(ExtractSchemaFromArchive(fileName, archive));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "grasshopper/schema: exception processing file={FileName}", fileName);
                    results.Add(GrasshopperValidationHelper.ErrorResult(fileName, ex.Message));
                }
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(results));
        }

        // VEKTORNODE: SELVA — fetch .gh/.ghx from URL(s) (or local path) and extract embedded schema.
        // application/json body: { "urls": ["https://…/file.gh"] } or { "url": "https://…/file.gh" }.
        static async Task GetGrasshopperSchemaFromUrl(HttpContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync();

            JObject json;
            try { json = JObject.Parse(body); }
            catch { json = null; }

            if (json == null)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { error = "Invalid JSON body." }));
                return;
            }

            var urls = new List<string>();
            if (json["urls"] is JArray arr)
                urls.AddRange(arr.Values<string>().Where(u => !string.IsNullOrWhiteSpace(u)));
            else if (json["url"] is JValue single && single.Type == JTokenType.String)
                urls.Add(single.Value<string>());

            if (urls.Count == 0)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { error = "Provide 'url' or 'urls' in the JSON body." }));
                return;
            }

            var results = new List<JObject>();
            foreach (var url in urls)
            {
                try
                {
                    var archive = await GrasshopperValidationHelper.ArchiveFromUrlAsync(url);
                    results.Add(ExtractSchemaFromArchive(url, archive));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "grasshopper/schema/url: exception processing url={Url}", url);
                    results.Add(GrasshopperValidationHelper.ErrorResult(url, ex.Message));
                }
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(results));
        }

        // Shared schema extraction for both the file-upload and URL endpoints.
        static JObject ExtractSchemaFromArchive(string fileName, GH_Archive archive)
        {
            if (archive == null)
            {
                Log.Warning("grasshopper/schema: failed to load archive for file={FileName}", fileName);
                return GrasshopperValidationHelper.ErrorResult(fileName, "Failed to load the Grasshopper document.");
            }

            var doc = GrasshopperValidationHelper.DocumentFromArchive(archive);
            if (doc == null)
            {
                Log.Warning("grasshopper/schema: failed to extract document from archive for file={FileName}", fileName);
                return GrasshopperValidationHelper.ErrorResult(fileName, "Failed to extract definition from archive.");
            }

            Log.Information("grasshopper/schema: document loaded, objectCount={Count}", doc.ObjectCount);
            var allTypes = string.Join(", ", doc.Objects.Select(o => o.GetType().Name).Distinct().OrderBy(n => n));
            Log.Information("grasshopper/schema: component types in document: {Types}", allTypes);

            var schemaComponents = GrasshopperValidationHelper.GetSchemaContextBakeComponents(doc);
            Log.Information("grasshopper/schema: found {Count} schema Context Bake component(s) in file={FileName}", schemaComponents.Count, fileName);

            if (schemaComponents.Count == 0)
            {
                return GrasshopperValidationHelper.ErrorResult(fileName,
                    "This definition has no outputs defined. " +
                    "Add a 'Context Bake' component, connect a 'UI Builder' component to its first input, " +
                    "and make sure the Schema output param is named 'Schema'.");
            }

            var schemas = new JArray();
            foreach (var component in schemaComponents)
            {
                var parent = GrasshopperValidationHelper.GetSchemaParentComponent(component);
                Log.Debug("grasshopper/schema: schema parent component type={Type}", parent?.GetType().Name ?? "null");

                if (!GrasshopperValidationHelper.IsComponentOfType(parent, "GH_UIBuilderComponent"))
                {
                    var error = "The 'Schema' source is not coming from a 'UI Builder' component.";
                    Log.Warning("grasshopper/schema: {Error} (got {Type})", error, parent?.GetType().Name ?? "null");
                    return GrasshopperValidationHelper.ErrorResult(fileName, error);
                }

                var schema = GrasshopperValidationHelper.GetEmbeddedSchema(parent);
                if (schema == null)
                {
                    var error = "The UI Builder component was found but contains no embedded schema. Configure and save your schema inside the UI Builder component.";
                    Log.Warning("grasshopper/schema: {Error}", error);
                    return GrasshopperValidationHelper.ErrorResult(fileName, error);
                }

                var schemaJson = GrasshopperValidationHelper.SchemaToJson(schema);
                if (schemaJson == null)
                {
                    var error = "Could not serialize the embedded schema: this compute server could not reach " +
                        "the Selva plugin's own serializer (UISchemaGoo.ToComputeJson). Serializing it here would " +
                        "emit PascalCase property names that clients read as an empty schema. Check that the " +
                        "installed Selva.gha matches this compute build.";
                    Log.Warning("grasshopper/schema: {Error}", error);
                    return GrasshopperValidationHelper.ErrorResult(fileName, error);
                }

                Log.Debug("grasshopper/schema: extracted schema successfully from file={FileName}", fileName);
                schemas.Add(schemaJson);
            }

            return GrasshopperValidationHelper.SuccessResult(fileName, schemas);
        }
    }
}

