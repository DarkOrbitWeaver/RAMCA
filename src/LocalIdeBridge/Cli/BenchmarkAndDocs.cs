using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Tools;

namespace LocalIdeBridge.Cli;

/// <summary>
/// Executes real, unfabricated resource &amp; scalability benchmarks across 100, 1,000, and 10,000
/// synthetic operations plus startup, idle RAM/CPU, active build overhead, session switching,
/// paginated log retrieval, and shutdown timing.
/// </summary>
public static class BenchmarkRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var outputPath = SetupWizard.GetOpt(args, "--output");
        var benchRoot = Path.Combine(Path.GetTempPath(), $"localidebridge_bench_{Guid.NewGuid():N}");
        var wsRoot = Path.Combine(benchRoot, "workspace");
        Directory.CreateDirectory(wsRoot);

        try
        {
            // Seed a small realistic disposable C# project inside the benchmark workspace.
            File.WriteAllText(Path.Combine(wsRoot, "Sample.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(wsRoot, "Calculator.cs"), """
                namespace Sample;
                public interface ICalculator { int Add(int a, int b); }
                public sealed class Calculator : ICalculator
                {
                    public int Add(int a, int b) => a + b;
                    public int Multiply(int a, int b) => a * b;
                }
                """);
            File.WriteAllText(Path.Combine(wsRoot, "notes.txt"),
                string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Line {i}: benchmark payload data token_{i}")));

            var paths = new PortablePaths(Path.Combine(benchRoot, "portable"));
            var cfg = new AppConfig { SetupCompleted = true };
            cfg.AddTrustedWorkspace(wsRoot, "bench", setAsDefault: true);
            cfg.Connection.Port = 0; // Ephemeral free port
            cfg.Security.RequestsPerMinute = 100_000;
            cfg.Save(paths);

            // 1. Measure cold in-process host initialization + Kestrel listener startup -> HTTP /health 200 OK.
            var coldSw = Stopwatch.StartNew();
            var host = new WorkbenchHost(paths, cfg);
            await host.StartNetworkListenerAsync(ct);
            using (var http = new HttpClient())
            {
                var health = await http.GetAsync($"{host.BoundLocalUrl}/health", ct);
                health.EnsureSuccessStatusCode();
            }
            coldSw.Stop();
            var coldStartMs = coldSw.Elapsed.TotalMilliseconds;

            // Also measure full external OS subprocess cold start if current executable/dll is available.
            var subprocessColdStartMs = await MeasureSubprocessColdStartAsync(benchRoot, ct);

            // 2. Measure idle working set & idle CPU over a 3.0-second quiet window.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(300, ct);

            double idleRssMb, idleManagedMb, idleCpuPercent;
            using (var proc = Process.GetCurrentProcess())
            {
                proc.Refresh();
                idleRssMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                idleManagedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                var cpuBefore = proc.TotalProcessorTime;
                var wallSw = Stopwatch.StartNew();
                await Task.Delay(3000, ct);
                wallSw.Stop();
                proc.Refresh();
                var cpuDeltaMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                idleCpuPercent = (cpuDeltaMs / (wallSw.Elapsed.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
            }

            // 3. Measure synthetic operation scales: 100, 1,000, and 10,000 real tool invocations!
            var scaleResults = new List<object>();
            var completedOps = 0;
            foreach (var targetTotal in new[] { 100, 1_000, 10_000 })
            {
                var delta = targetTotal - completedOps;
                var opSw = Stopwatch.StartNew();
                for (var i = 0; i < delta; i++)
                {
                    var opIdx = completedOps + i;
                    var mod = opIdx % 5;
                    ToolResult r = mod switch
                    {
                        0 => await host.Registry.InvokeAsync("read_range", new JsonObject { ["path"] = "notes.txt", ["startLine"] = 1, ["endLine"] = 25 }, null, ct),
                        1 => await host.Registry.InvokeAsync("text_search", new JsonObject { ["query"] = "token_42", ["maxResults"] = 10 }, null, ct),
                        2 => await host.Registry.InvokeAsync("repo_tree", new JsonObject { ["maxDepth"] = 2, ["maxEntries"] = 50 }, null, ct),
                        3 => await host.Registry.InvokeAsync("replace_range", new JsonObject { ["path"] = "notes.txt", ["startLine"] = 1, ["endLine"] = 1, ["newText"] = $"Line 1: iter={opIdx}" }, null, ct),
                        _ => await host.Registry.InvokeAsync("session_list", new JsonObject { ["limit"] = 10 }, null, ct)
                    };
                    if (!r.Success)
                        throw new InvalidOperationException($"Synthetic op #{opIdx} failed: {r.ErrorMessage}");
                }
                opSw.Stop();
                completedOps = targetTotal;

                using var p = Process.GetCurrentProcess();
                p.Refresh();
                var rssBeforeGc = p.WorkingSet64 / (1024.0 * 1024.0);
                var managedBeforeGc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                var managedAfterGc = GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024.0);
                p.Refresh();
                var rssAfterGc = p.WorkingSet64 / (1024.0 * 1024.0);
                var stateDbMb = new FileInfo(paths.StateDbFile).Length / (1024.0 * 1024.0);

                scaleResults.Add(new
                {
                    cumulativeOperations = targetTotal,
                    batchOperations = delta,
                    batchDurationMs = Math.Round(opSw.Elapsed.TotalMilliseconds, 1),
                    opsPerSecond = Math.Round(delta / opSw.Elapsed.TotalSeconds, 1),
                    workingSetMb = Math.Round(rssBeforeGc, 2),
                    workingSetAfterGcMb = Math.Round(rssAfterGc, 2),
                    managedHeapMb = Math.Round(managedBeforeGc, 2),
                    liveManagedHeapAfterGcMb = Math.Round(managedAfterGc, 2),
                    sqliteStateDbMb = Math.Round(stateDbMb, 2)
                });
            }

            // 4. Measure active dotnet build overhead on the disposable C# project.
            double buildDurationMs, hostRssDuringBuildEndMb, hostManagedAfterBuildMb;
            {
                var bSw = Stopwatch.StartNew();
                var bRes = await host.Registry.InvokeAsync("dotnet_build", new JsonObject { ["target"] = "Sample.csproj" }, null, ct);
                bSw.Stop();
                if (!bRes.Success)
                    throw new InvalidOperationException($"Benchmark build failed: {bRes.ErrorMessage}");
                using var p = Process.GetCurrentProcess();
                p.Refresh();
                buildDurationMs = bSw.Elapsed.TotalMilliseconds;
                hostRssDuringBuildEndMb = p.WorkingSet64 / (1024.0 * 1024.0);
                hostManagedAfterBuildMb = GC.GetTotalMemory(true) / (1024.0 * 1024.0);
            }

            // 5. Measure session creation & switching across 25 sessions.
            double avgSessionSwitchMs;
            double managedAfterSessionSwitchesMb;
            {
                var ids = new List<string>(25);
                for (var i = 0; i < 25; i++)
                    ids.Add(host.Database.CreateAndActivateSession(wsRoot).Id);

                var sSw = Stopwatch.StartNew();
                foreach (var sid in ids)
                {
                    await host.Registry.InvokeAsync("session_switch", new JsonObject { ["action"] = "switch", ["sessionId"] = sid }, null, ct);
                    await host.Registry.InvokeAsync("session_inspect", new JsonObject { ["sessionId"] = sid, ["toolCallLimit"] = 20 }, null, ct);
                }
                sSw.Stop();
                avgSessionSwitchMs = sSw.Elapsed.TotalMilliseconds / ids.Count;
                managedAfterSessionSwitchesMb = GC.GetTotalMemory(true) / (1024.0 * 1024.0);
            }

            // 6. Measure on-demand paginated log retrieval over a 10,000-line execution log.
            double logSliceHeadMs, logSliceTailMs;
            {
                using (var writer = host.Logs.OpenWriter("exec_bench_10k", host.Database.ActiveSessionId, "benchmark"))
                {
                    for (var i = 1; i <= 10_000; i++)
                        writer.WriteLine("out", $"[2026-01-01] Compiler/test output synthetic line #{i:D5} status=OK");
                    writer.Complete();
                }

                var hSw = Stopwatch.StartNew();
                var s1 = host.Logs.ReadRange("exec_bench_10k", 1, 100);
                hSw.Stop();
                var tSw = Stopwatch.StartNew();
                var s2 = host.Logs.ReadRange("exec_bench_10k", 9_901, 100);
                tSw.Stop();
                if (s1.ReturnedLines != 100 || s2.ReturnedLines != 100)
                    throw new InvalidOperationException("Unexpected log slice count.");
                logSliceHeadMs = hSw.Elapsed.TotalMilliseconds;
                logSliceTailMs = tSw.Elapsed.TotalMilliseconds;
            }

            // 7. Measure clean shutdown time + verify listener socket is immediately closed.
            var boundUrl = host.BoundLocalUrl;
            var shutSw = Stopwatch.StartNew();
            await host.DisposeAsync();
            shutSw.Stop();
            var shutdownMs = shutSw.Elapsed.TotalMilliseconds;

            bool listenerClosedAfterShutdown = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                await http.GetAsync($"{boundUrl}/health", ct);
            }
            catch (HttpRequestException)
            {
                listenerClosedAfterShutdown = true;
            }

            var report = new
            {
                measuredAtUtc = DateTimeOffset.UtcNow,
                environment = new
                {
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    dotnetVersion = Environment.Version.ToString(),
                    processorCount = Environment.ProcessorCount,
                    serverGc = System.Runtime.GCSettings.IsServerGC
                },
                startup = new
                {
                    hostInitAndBindMs = Math.Round(coldStartMs, 1),
                    fullSubprocessColdStartToHealthOkMs = subprocessColdStartMs.HasValue ? Math.Round(subprocessColdStartMs.Value, 1) : (double?)null
                },
                idle = new
                {
                    sampleWindowMs = 3000,
                    workingSetMb = Math.Round(idleRssMb, 2),
                    managedHeapMb = Math.Round(idleManagedMb, 2),
                    cpuPercent = Math.Round(idleCpuPercent, 3)
                },
                syntheticOperationScales = scaleResults,
                activeBuildOverhead = new
                {
                    dotnetBuildDurationMs = Math.Round(buildDurationMs, 1),
                    hostWorkingSetAfterBuildMb = Math.Round(hostRssDuringBuildEndMb, 2),
                    hostLiveManagedHeapMb = Math.Round(hostManagedAfterBuildMb, 2)
                },
                sessionSwitching = new
                {
                    sessionsTested = 25,
                    avgSwitchAndInspectMs = Math.Round(avgSessionSwitchMs, 2),
                    liveManagedHeapAfterSwitchesMb = Math.Round(managedAfterSessionSwitchesMb, 2)
                },
                logRetrieval10kLines = new
                {
                    readFirst100LinesMs = Math.Round(logSliceHeadMs, 2),
                    readLast100LinesMs = Math.Round(logSliceTailMs, 2)
                },
                shutdown = new
                {
                    cleanShutdownMs = Math.Round(shutdownMs, 1),
                    listenerClosedImmediately = listenerClosedAfterShutdown
                }
            };

            var json = JsonSerializer.Serialize(report, AppConfig.JsonOptions);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                File.WriteAllText(outputPath, json + Environment.NewLine);
            }
            Console.WriteLine(json);
            return 0;
        }
        finally
        {
            try { Directory.Delete(benchRoot, recursive: true); } catch { }
        }
    }

    private static async Task<double?> MeasureSubprocessColdStartAsync(string benchRoot, CancellationToken ct)
    {
        try
        {
            var entryDll = Path.Combine(AppContext.BaseDirectory, "LocalIdeBridge.dll");
            var entryExe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "LocalIdeBridge.exe" : "LocalIdeBridge");
            var subHome = Path.Combine(benchRoot, "coldstart_home");
            var subWs = Path.Combine(benchRoot, "coldstart_ws");
            Directory.CreateDirectory(subWs);

            var subPaths = new PortablePaths(subHome);
            var subCfg = new AppConfig { SetupCompleted = true };
            subCfg.AddTrustedWorkspace(subWs, "cold", setAsDefault: true);
            subCfg.Connection.Port = 0;
            subCfg.Save(subPaths);

            ProcessStartInfo psi;
            if (File.Exists(entryExe))
            {
                psi = new ProcessStartInfo(entryExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            }
            else if (File.Exists(entryDll) && Environment.ProcessPath is not null)
            {
                psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(entryDll);
            }
            else return null;

            psi.ArgumentList.Add("host");
            psi.ArgumentList.Add("--home");
            psi.ArgumentList.Add(subHome);

            var sw = Stopwatch.StartNew();
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var lockFile = subPaths.HostLockFile;
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (File.Exists(lockFile))
                {
                    var parts = File.ReadAllText(lockFile).Trim().Split('|');
                    if (parts.Length >= 2)
                    {
                        try
                        {
                            var r = await http.GetAsync($"{parts[1]}/health", ct);
                            if (r.IsSuccessStatusCode)
                            {
                                sw.Stop();
                                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } catch { }
                                return sw.Elapsed.TotalMilliseconds;
                            }
                        }
                        catch { }
                    }
                }
                await Task.Delay(15, ct);
            }
            try { proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Generates authoritative human-readable tool documentation directly from ToolRegistry contracts.
/// </summary>
public static class DocGenerator
{
    public static int GenerateToolsMarkdown(string outputFile)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_gendocs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new PortablePaths(tempDir);
            using var hostTask = BuildAndWriteAsync(paths, outputFile);
            hostTask.GetAwaiter().GetResult();
            Console.WriteLine($"Generated authoritative tool reference: {outputFile}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task BuildAndWriteAsync(PortablePaths paths, string outputFile)
    {
        await using var host = new WorkbenchHost(paths);
        var sb = new StringBuilder();
        sb.AppendLine("# LocalIdeBridge — Complete Tool Reference");
        sb.AppendLine();
        sb.AppendLine($"> Auto-generated from the authoritative `ToolRegistry` contracts (`{host.Registry.All.Count}` tools). The exact same `ToolDefinition` objects power MCP `tools/list`, `/api/v1/tools`, `/openapi.json`, and `LocalIdeBridge cli tools`.");
        sb.AppendLine();
        sb.AppendLine("## Standard Response Envelope");
        sb.AppendLine();
        sb.AppendLine("Every tool invocation across MCP (`structuredContent`), HTTPS API (`POST /api/v1/tools/{name}/invoke`), and CLI (`LocalIdeBridge cli call <name> --json`) returns a consistent `ToolResult` contract:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("""
            {
              "success": true,
              "summary": "Concise human- and AI-readable result summary.",
              "data": { },
              "errorCode": null,
              "errorMessage": null,
              "executionId": "exec_20260101_120000_a1b2c3d4"
            }
            """);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Standard Error Codes");
        sb.AppendLine();
        sb.AppendLine("| Error Code | HTTP Status | Meaning |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `unauthorized` | `401` | Missing, invalid, or rotated Bearer / `X-LocalIdeBridge-Key` credential. |");
        sb.AppendLine("| `workspace_violation` | `403` | Path escapes trusted workspace (traversal, junction, external symlink), targets protected `.git/hooks`, or command blocked by policy. |");
        sb.AppendLine("| `authorization_required` | `403` | Action requires explicit local configuration (e.g. `git_push` while `gitPushPolicy='deny'`). |");
        sb.AppendLine("| `host_paused` | `403` | Local user paused write/execution tools in the supervisor TUI. |");
        sb.AppendLine("| `invalid_arguments` / `invalid_patch` / `invalid_regex` / `range_out_of_bounds` | `400` | Schema, range, diff, or regex validation failed. |");
        sb.AppendLine("| `not_found` / `unknown_tool` | `404` | Target file, directory, job, session, edit, log, or tool does not exist. |");
        sb.AppendLine("| `stale_file` / `precondition_failed` / `conflict` / `already_exists` | `409` | Concurrent file edit detected (`expectedSha256` mismatch), unified diff context mismatch, or unsafe revert. |");
        sb.AppendLine("| `timeout` | `408` | Command or workflow step exceeded timeout; owned child tree was terminated. |");
        sb.AppendLine("| `cancelled` | `422` | Cancelled by client, `job_cancel`, `workflow_cancel`, or host shutdown. |");
        sb.AppendLine("| `build_failed` / `tests_failed` / `command_failed` / `workflow_failed` | `422` | Underlying process or workflow finished with non-zero status; structured diagnostics and `executionId` are included in `data`. |");
        sb.AppendLine("| `payload_too_large` | `413` | Request body exceeded `security.maxRequestBodyBytes`. |");
        sb.AppendLine("| `rate_limited` / `auth_rate_limited` | `429` | Per-minute request or failed-auth threshold exceeded. |");
        sb.AppendLine();

        foreach (var group in host.Registry.All.GroupBy(t => t.Category))
        {
            sb.AppendLine($"## Category: `{group.Key}` ({group.Count()} tools)");
            sb.AppendLine();

            foreach (var tool in group)
            {
                sb.AppendLine($"### `{tool.Name}`");
                sb.AppendLine();
                sb.AppendLine(tool.Description);
                sb.AppendLine();
                sb.AppendLine($"- **Read-only:** `{(tool.IsReadOnly ? "true" : "false")}` | **Idempotent:** `{(tool.IsIdempotent ? "true" : "false")}`");
                sb.AppendLine($"- **Side effects:** {tool.SideEffects}");
                sb.AppendLine();

                if (tool.Parameters.Count == 0)
                {
                    sb.AppendLine("**Parameters:** None (`{}`).");
                }
                else
                {
                    sb.AppendLine("| Parameter | Type | Required | Default | Description |");
                    sb.AppendLine("| --- | --- | --- | --- | --- |");
                    foreach (var p in tool.Parameters)
                    {
                        var def = p.Default is null ? "—" : $"`{JsonSerializer.Serialize(p.Default)}`";
                        var enumHint = p.EnumValues is { Length: > 0 } ? $" Allowed: {string.Join(", ", p.EnumValues.Select(e => $"`{e}`"))}." : "";
                        sb.AppendLine($"| `{p.Name}` | `{p.Type}` | {(p.Required ? "Yes" : "No")} | {def} | {p.Description}{enumHint} |");
                    }
                }

                // Compact invocation example.
                var sampleArgs = new JsonObject();
                foreach (var p in tool.Parameters.Where(x => x.Required))
                {
                    sampleArgs[p.Name] = p.Type switch
                    {
                        "integer" => 1,
                        "boolean" => true,
                        "array" => new JsonArray(),
                        "object" => new JsonObject(),
                        _ => p.EnumValues?.FirstOrDefault() ?? $"<{p.Name}>"
                    };
                }
                sb.AppendLine();
                sb.AppendLine($"**Example (`POST /api/v1/tools/{tool.Name}/invoke` or MCP `tools/call` arguments):**");
                sb.AppendLine("```json");
                sb.AppendLine(sampleArgs.ToJsonString(AppConfig.JsonOptions));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        await File.WriteAllTextAsync(outputFile, sb.ToString());
    }
}
