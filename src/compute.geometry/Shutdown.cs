using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Hosting;

namespace compute.geometry
{
    class Shutdown
    {
        static System.Threading.Timer timer;
        internal static Dictionary<int, System.Diagnostics.Process> ParentProcesses { get; set; }
        static int parentPort = -1;
        static int idleSpan = -1;
        static DateTime startTime;

        public static void RegisterParentProcess(int processId)
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            if (ParentProcesses == null)
            {
                ParentProcesses = new Dictionary<int, System.Diagnostics.Process>();
            }
            if (process != null)
                ParentProcesses[processId] = process;
        }

        public static void RegisterStartTime(DateTime dateTime)
        {
            startTime = dateTime;
        }

        public static void RegisterParentPort(int port)
        {
            parentPort = port;
        }

        public static void RegisterIdleSpan(int spanSeconds)
        {
            idleSpan = spanSeconds;
        }

        public static void StartTimer(IHost app)
        {
            bool startTimer = false;
            if (timer == null && (ParentProcesses != null))
            {
                startTimer = true;
            }
            if (timer == null && idleSpan > 0 && parentPort > 0)
            {
                startTimer = true;
            }

            if (startTimer)
            {
                timer = new System.Threading.Timer(
                    new System.Threading.TimerCallback(TimerTask), app, 1000, 5000);
            }
        }

        static System.Net.Http.HttpClient httpClient;
        static DateTime lastSpanCheck = DateTime.Now;

        private async static void TimerTask(object timerState)
        {
            bool shutdown = false;

            if (ParentProcesses != null)
            {
                shutdown = true;
                foreach (var process in ParentProcesses.Values)
                {
                    if (!process.HasExited)
                        shutdown = false;
                }
            }

            if (!shutdown && parentPort > 0 && idleSpan > 0)
            {
                // Don't check the server every timer tick. Just check when we start approaching
                // what we think is our span limit.
                var shouldCheckSpan = DateTime.Now - lastSpanCheck;
                //Serilog.Log.Debug($"Idle span value is {idleSpan}. Elapsed time {shouldCheckSpan.TotalSeconds} seconds. Last span check {lastSpanCheck.ToLocalTime()}.");
                if (shouldCheckSpan.TotalSeconds > idleSpan)
                {
                    if (httpClient == null)
                        httpClient = new System.Net.Http.HttpClient();
                    string url = $"http://localhost:{parentPort}/idlespan";
                    lastSpanCheck = DateTime.Now;
                    Serilog.Log.Debug($"The elapsed time has exceeded the idle span limit");
                    Serilog.Log.Debug($"Checking with parent process at {url}.");
                    try
                    {
                        if (!String.IsNullOrEmpty(Config.ApiKey) && !httpClient.DefaultRequestHeaders.Contains("RhinoComputeKey"))
                            httpClient.DefaultRequestHeaders.Add("RhinoComputeKey", Config.ApiKey);
                        HttpResponseMessage response = await httpClient.GetAsync(url);
                        response.EnsureSuccessStatusCode();    
                        string span = response.Content.ReadAsStringAsync().Result;
                        if (!string.IsNullOrEmpty(span))
                        {
                            int serverIdleSpan = int.Parse(span);
                            Serilog.Log.Debug($"Response received. Parent process idle span is {serverIdleSpan} seconds.");
                            if (serverIdleSpan + (serverIdleSpan * 0.1) > idleSpan)
                            {
                                Serilog.Log.Debug("Idle span limit reached");
                                shutdown = true;
                            }
                        }
                        else
                        {
                            Serilog.Log.Debug("Idle span value from parent process was null or empty");
                        }
                    }
                    catch(Exception)
                    {
                        Serilog.Log.Debug($"Failed to get response from {url}");
                    }
                }
            }

            if (shutdown)
            {
                Serilog.Log.Debug("Shutting down child process");
                var elapsedTime = DateTime.Now - startTime;
                Serilog.Log.Information("Total elapsed time for child process is " + string.Format("{0:D2} days, {1:D2} hrs, {2:D2} mins, {3:D2} secs", elapsedTime.Days, elapsedTime.Hours, elapsedTime.Minutes, elapsedTime.Seconds));

                // VEKTORNODE: SELVA FIX — dead-child eviction.
                // Tell the parent we're leaving so it drops us from the round-robin pool now,
                // rather than discovering the dead port on the next client request (which produced
                // the post-idle "connection refused" bursts). Best-effort: if the parent is gone or
                // unreachable, we still proceed to stop.
                await NotifyParentExiting();

                var app = timerState as IHost;
                if (app != null)
                    await app.StopAsync();
            }
        }

        // VEKTORNODE: SELVA FIX — dead-child eviction.
        // Best-effort POST to the parent's /child-exiting endpoint with our own port so the parent
        // evicts us from the pool before we stop listening. Swallows all errors — this is a
        // courtesy notification and must never block or fail the shutdown.
        private static async System.Threading.Tasks.Task NotifyParentExiting()
        {
            if (parentPort <= 0)
                return;
            int myPort = Config.LocalhostPort;
            if (myPort <= 0)
                return;
            try
            {
                if (httpClient == null)
                    httpClient = new System.Net.Http.HttpClient();
                if (!String.IsNullOrEmpty(Config.ApiKey) && !httpClient.DefaultRequestHeaders.Contains("RhinoComputeKey"))
                    httpClient.DefaultRequestHeaders.Add("RhinoComputeKey", Config.ApiKey);
                string url = $"http://localhost:{parentPort}/child-exiting?port={myPort}";
                Serilog.Log.Debug($"Notifying parent of shutdown at {url}");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                await httpClient.PostAsync(url, null, cts.Token);
            }
            catch (Exception)
            {
                Serilog.Log.Debug("Failed to notify parent of child shutdown; parent will evict on next request");
            }
        }
    }
}
