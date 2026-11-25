using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class CodeIntelAndExecutionTests
{
    [Fact]
    public async Task RoslynCodeIntelligence_DistinguishesSemanticAnalysisFromTextSearch()
    {
        await using var sb = await TestSandbox.CreateAsync();

        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Pricing.cs"), """
            namespace Shop;

            public interface IDiscountRule
            {
                decimal Apply(decimal subtotal);
            }

            public sealed class SeasonalDiscount : IDiscountRule
            {
                public decimal Percent { get; }
                public SeasonalDiscount(decimal percent) => Percent = percent;
                public decimal Apply(decimal subtotal) => subtotal * (1m - Percent);
            }

            public sealed class VipDiscount : IDiscountRule
            {
                public decimal Apply(decimal subtotal) => subtotal * 0.80m;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Checkout.cs"), """
            namespace Shop;

            public sealed class CheckoutService
            {
                // Mention SeasonalDiscount in a comment — semantic find_references must NOT count comments!
                public decimal ComputeTotal(decimal amount)
                {
                    IDiscountRule rule = new SeasonalDiscount(0.10m);
                    return rule.Apply(amount);
                }
            }
            """);

        // Project & dependency discovery.
        var proj = await sb.Host.Registry.InvokeAsync("project_discover", new JsonObject());
        Assert.True(proj.Success);
        Assert.Contains("App.csproj", JsonSerializer.Serialize(proj.Data));

        var deps = await sb.Host.Registry.InvokeAsync("dependency_inspect", new JsonObject());
        Assert.True(deps.Success);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions", JsonSerializer.Serialize(deps.Data));

        // Literal & regex search explicitly set isSemantic=false.
        var lit = await sb.Host.Registry.InvokeAsync("text_search", new JsonObject { ["query"] = "SeasonalDiscount" });
        Assert.True(lit.Success);
        Assert.False(JsonSerializer.SerializeToElement(lit.Data).GetProperty("isSemantic").GetBoolean());

        var rx = await sb.Host.Registry.InvokeAsync("regex_search", new JsonObject { ["pattern"] = @"Apply\(decimal\s+\w+\)" });
        Assert.True(rx.Success);
        Assert.True(JsonSerializer.SerializeToElement(rx.Data).GetProperty("count").GetInt32() >= 3);

        // Roslyn symbol_search.
        var sym = await sb.Host.Registry.InvokeAsync("symbol_search", new JsonObject { ["query"] = "Discount" });
        Assert.True(sym.Success);
        Assert.Contains("SeasonalDiscount", JsonSerializer.Serialize(sym.Data));

        // Roslyn semantic go_to_definition from usage in Checkout.cs.
        var def = await sb.Host.Registry.InvokeAsync("go_to_definition", new JsonObject { ["symbolName"] = "SeasonalDiscount" });
        Assert.True(def.Success);
        var defEl = JsonSerializer.SerializeToElement(def.Data);
        Assert.Equal("roslyn-semantic", defEl.GetProperty("engine").GetString());
        Assert.True(defEl.GetProperty("isSemantic").GetBoolean());
        Assert.Equal("Pricing.cs", defEl.GetProperty("definitions")[0].GetProperty("file").GetString());

        // Roslyn semantic find_references finds real code usage in Checkout.cs (line 8), NOT the comment on line 5!
        var refs = await sb.Host.Registry.InvokeAsync("find_references", new JsonObject { ["symbolName"] = "SeasonalDiscount" });
        Assert.True(refs.Success);
        var refsEl = JsonSerializer.SerializeToElement(refs.Data);
        Assert.Equal("roslyn-semantic", refsEl.GetProperty("engine").GetString());
        Assert.Equal(1, refsEl.GetProperty("count").GetInt32());
        Assert.Equal(8, refsEl.GetProperty("references")[0].GetProperty("line").GetInt32());

        // Roslyn semantic find_implementations finds both SeasonalDiscount and VipDiscount.
        var impl = await sb.Host.Registry.InvokeAsync("find_implementations", new JsonObject { ["symbolName"] = "IDiscountRule" });
        Assert.True(impl.Success);
        var implJson = JsonSerializer.Serialize(impl.Data);
        Assert.Contains("Shop.SeasonalDiscount", implJson);
        Assert.Contains("Shop.VipDiscount", implJson);

        // Roslyn type inspection.
        var inspect = await sb.Host.Registry.InvokeAsync("inspect_type", new JsonObject { ["typeName"] = "Shop.SeasonalDiscount" });
        Assert.True(inspect.Success);
        Assert.Contains("Shop.IDiscountRule", JsonSerializer.Serialize(inspect.Data));

        // In-process Roslyn compiler diagnostics reports 0 errors on valid code, then catches an injected error.
        var diagClean = await sb.Host.Registry.InvokeAsync("compiler_diagnostics", new JsonObject { ["mode"] = "roslyn" });
        Assert.True(diagClean.Success);
        Assert.Equal(0, JsonSerializer.SerializeToElement(diagClean.Data).GetProperty("errorCount").GetInt32());

        await sb.Host.Registry.InvokeAsync("create_file",
            new JsonObject { ["path"] = "Broken.cs", ["content"] = "namespace Shop; public class Broken { int x = \"not_an_int\"; }" });
        var diagErr = await sb.Host.Registry.InvokeAsync("compiler_diagnostics", new JsonObject { ["mode"] = "roslyn" });
        Assert.True(diagErr.Success);
        Assert.True(JsonSerializer.SerializeToElement(diagErr.Data).GetProperty("errorCount").GetInt32() >= 1);
        Assert.Contains("CS0029", JsonSerializer.Serialize(diagErr.Data));
    }

    [Fact]
    public async Task SymbolSearchFiltersFilesAndRefreshesSameLengthExternalEdits()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var file = Path.Combine(sb.WorkspaceDir, "Target.cs");
        await File.WriteAllTextAsync(file, "class Alpha { int One; }");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Other.cs"), "class Alpha {}");
        var args = new JsonObject { ["query"] = "*", ["filePattern"] = "Target.cs" };
        var first = await sb.Host.Registry.InvokeAsync("symbol_search", args);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Contains("One", JsonSerializer.Serialize(first.Data));
        Assert.DoesNotContain("Other.cs", JsonSerializer.Serialize(first.Data));
        var stamp = File.GetLastWriteTimeUtc(file);
        await File.WriteAllTextAsync(file, "class Bravo { int Two; }");
        File.SetLastWriteTimeUtc(file, stamp);
        var second = await sb.Host.Registry.InvokeAsync("symbol_search", args);
        Assert.True(second.Success, second.ErrorMessage);
        var json = JsonSerializer.Serialize(second.Data);
        Assert.Contains("Bravo", json);
        Assert.Contains("Two", json);
        Assert.DoesNotContain("Alpha", json);
        Assert.Contains("startLine", json);
    }

    [Fact]
    public async Task MultiLiteralSearchScansOnceAndCentersMatchesOnLongLines()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "extension.js"),
            new string('x', 9000) + " firstMarker " + new string('y', 9000) + " secondMarker");
        var result = await sb.Host.Registry.InvokeAsync("text_search", new JsonObject
        {
            ["queries"] = new JsonArray("firstMarker", "secondMarker"), ["filePattern"] = "*.js"
        });
        Assert.True(result.Success, result.ErrorMessage);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(1, data.GetProperty("filesScanned").GetInt32());
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        foreach (var match in data.GetProperty("matches").EnumerateArray())
            Assert.Contains(match.GetProperty("query").GetString()!, match.GetProperty("text").GetString()!);
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SemanticReferencesIncludeGenericTypeNameSyntax()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "GenericUse.cs"), """
            namespace Demo;
            public sealed class Repository<T> { public T Echo(T item) => item; }
            public sealed class Consumer
            {
                private Repository<int> _repo = new();
                public int Read() => _repo.Echo(1);
            }
            """);
        var references = await sb.Host.Registry.InvokeAsync("find_references",
            new JsonObject { ["symbolName"] = "Repository" });
        Assert.True(references.Success, references.ErrorMessage);
        var data = JsonSerializer.SerializeToElement(references.Data);
        Assert.Equal("roslyn-semantic", data.GetProperty("engine").GetString());
        Assert.True(data.GetProperty("count").GetInt32() > 0,
            "GenericNameSyntax references must not disappear from semantic results.");
        Assert.Contains("GenericUse.cs", JsonSerializer.Serialize(references.Data));
    }

    [Fact]
    public async Task SemanticDisabledSettingIsHonoredByAllSemanticTools()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Integrations.EnableRoslynSemantic = false, startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Demo.cs"),
            "public class Demo { public int M() => 1; }\n");
        var checks = new[]
        {
            ("go_to_definition", new JsonObject { ["symbolName"] = "Demo" }),
            ("find_references", new JsonObject { ["symbolName"] = "Demo" }),
            ("find_implementations", new JsonObject { ["symbolName"] = "Demo" }),
            ("inspect_type", new JsonObject { ["typeName"] = "Demo" }),
            ("compiler_diagnostics", new JsonObject { ["mode"] = "roslyn" })
        };
        foreach (var (name, arguments) in checks)
        {
            var result = await sb.Host.Registry.InvokeAsync(name, arguments);
            Assert.False(result.Success, name);
            Assert.Equal("semantic_disabled", result.ErrorCode);
        }
        var textResult = await sb.Host.Registry.InvokeAsync("text_search",
            new JsonObject { ["query"] = "Demo" });
        Assert.True(textResult.Success, textResult.ErrorMessage);
    }

    [Fact]
    public async Task CommandExecution_HandlesTimeouts_Cancellation_ChildCleanup_AndBoundedLongLogPaging()
    {
        await using var sb = await TestSandbox.CreateAsync();

        // 1. Policy blocks destructive/firewall commands.
        var blocked = await sb.Host.Registry.InvokeAsync("run_command",
            new JsonObject { ["command"] = "netsh advfirewall set allprofiles state off", ["useShell"] = true });
        Assert.False(blocked.Success);
        Assert.Equal("workspace_violation", blocked.ErrorCode);

        // 2. Timeout terminates runaway process and records child PID to verify cleanup.
        var pidFile = Path.Combine(sb.WorkspaceDir, "sleep.pid");
        var sleepScript = OperatingSystem.IsWindows()
            ? $"$PID | Out-File -Encoding ascii '{pidFile}'; Start-Sleep -Seconds 30"
            : $"echo $$ > '{pidFile}'; sleep 30";

        var sw = Stopwatch.StartNew();
        var timedOut = await sb.Host.Registry.InvokeAsync("run_command", new JsonObject
        {
            ["command"] = sleepScript,
            ["useShell"] = true,
            ["timeoutSeconds"] = 1
        });
        sw.Stop();
        Assert.False(timedOut.Success);
        Assert.Equal("timeout", timedOut.ErrorCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, sb.Host.ProcessSupervisor.ActiveProcessCount);

        if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var childPid))
        {
            await Task.Delay(200);
            Assert.False(IsProcessAlive(childPid));
        }

        // 3. Cancelled async command job terminates promptly and cleans up subprocesses.
        var longScript = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var startJob = await sb.Host.Registry.InvokeAsync("job_start", new JsonObject
        {
            ["tool"] = "run_command",
            ["arguments"] = new JsonObject { ["command"] = longScript, ["useShell"] = true, ["timeoutSeconds"] = 60 }
        });
        Assert.True(startJob.Success);
        var jobId = JsonSerializer.SerializeToElement(startJob.Data).GetProperty("jobId").GetString()!;
        await Task.Delay(300);

        var cancel = await sb.Host.Registry.InvokeAsync("job_cancel", new JsonObject { ["jobId"] = jobId });
        Assert.True(cancel.Success);

        for (var i = 0; i < 30; i++)
        {
            var j = sb.Host.Database.GetJob(jobId)!;
            if (j.Status == "cancelled") break;
            await Task.Delay(100);
        }
        var finalJob = sb.Host.Database.GetJob(jobId)!;
        // Include the persisted failure details: a subprocess can fail *before*
        // job_cancel arrives, and a status-only assertion hides the real cause.
        Assert.True(finalJob.Status == "cancelled",
            $"Expected cancelled, got {finalJob.Status}. " +
            $"Error: {finalJob.ErrorMessage ?? "<none>"}. " +
            $"Result: {finalJob.ResultJson ?? "<none>"}. " +
            $"Events: {JsonSerializer.Serialize(sb.Host.Database.ListJobEvents(jobId, 0, 30))}");
        Assert.Equal(0, sb.Host.ProcessSupervisor.ActiveProcessCount);

        // 4. Long command output (3,000 lines) is bounded in response excerpt and fully pageable via read_log.
        var burstScript = OperatingSystem.IsWindows()
            ? "1..3000 | ForEach-Object { Write-Output \"LOG_ENTRY_$_\" }"
            : "seq 1 3000 | sed 's/^/LOG_ENTRY_/'";

        var burst = await sb.Host.Registry.InvokeAsync("run_command",
            new JsonObject { ["command"] = burstScript, ["useShell"] = true });
        Assert.True(burst.Success);
        var burstEl = JsonSerializer.SerializeToElement(burst.Data);
        var excerpt = burstEl.GetProperty("outputExcerpt").GetString()!;
        var execId = burstEl.GetProperty("executionId").GetString()!;
        Assert.True(excerpt.Length <= 6500); // Compact excerpt, not enormous raw log!
        Assert.Contains("omitted", excerpt);

        // Retrieve middle range on demand.
        var mid = await sb.Host.Registry.InvokeAsync("read_log",
            new JsonObject { ["execId"] = execId, ["startLine"] = 1500, ["maxLines"] = 10 });
        Assert.True(mid.Success);
        Assert.Contains("LOG_ENTRY_1500", JsonSerializer.Serialize(mid.Data));
    }

    [Fact]
    public async Task DotnetTestTool_ParsesStructuredTrxCountsAndFailureDetails()
    {
        await using var sb = await TestSandbox.CreateAsync();

        // Uses NuGet packages already cached in this machine for instant offline test execution.
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "SampleTests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "CalcTests.cs"), """
            using Xunit;
            public class CalcTests
            {
                [Fact] public void PassingOne() => Assert.Equal(4, 2 + 2);
                [Fact] public void PassingTwo() => Assert.True(10 > 5);
                [Fact(Skip = "Intentional skip")] public void SkippedCase() { }
                [Fact] public void FailingCase() => Assert.Equal(99, 1 + 1);
            }
            """);

        var testRes = await sb.Host.Registry.InvokeAsync("dotnet_test", new JsonObject { ["target"] = "SampleTests.csproj" });
        Assert.False(testRes.Success);
        Assert.Equal("tests_failed", testRes.ErrorCode);

        var el = JsonSerializer.SerializeToElement(testRes.Data, LocalIdeBridge.Core.AppConfig.JsonOptions);
        Assert.Equal(4, el.GetProperty("total").GetInt32());
        Assert.Equal(2, el.GetProperty("passed").GetInt32());
        Assert.Equal(1, el.GetProperty("failed").GetInt32());
        Assert.Equal(1, el.GetProperty("skipped").GetInt32());
        Assert.Contains("FailingCase", el.GetProperty("failures")[0].GetProperty("testName").GetString());
    }

    [Fact]
    public async Task SubprocessHost_ForcedTermination_ClosesListenerAndCleansUpOwnedTunnel()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lib_killtest_{Guid.NewGuid():N}");
        var homeDir = Path.Combine(tempRoot, "home");
        var wsDir = Path.Combine(tempRoot, "ws");
        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(wsDir);

        try
        {
            var paths = new LocalIdeBridge.Core.PortablePaths(homeDir);
            var cfg = new LocalIdeBridge.Core.AppConfig { SetupCompleted = true };
            cfg.AddTrustedWorkspace(wsDir, "ws", setAsDefault: true);
            cfg.Connection.Port = 0;
            cfg.Connection.Mode = "tunnel";
            cfg.Connection.TunnelProvider = "custom";
            if (OperatingSystem.IsWindows())
            {
                cfg.Connection.TunnelExecutable = "powershell.exe";
                cfg.Connection.TunnelArguments = ["-NoProfile", "-Command", "Write-Output 'https://demo-tunnel.example'; Start-Sleep -Seconds 60"];
            }
            else
            {
                cfg.Connection.TunnelExecutable = "/bin/sh";
                cfg.Connection.TunnelArguments = ["-c", "echo https://demo-tunnel.example; sleep 60"];
            }
            cfg.Save(paths);

            var hostDll = Path.Combine(AppContext.BaseDirectory, "LocalIdeBridge.dll");
            var dotnetExe = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var psi = new ProcessStartInfo(dotnetExe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(hostDll);
            psi.ArgumentList.Add("host");
            psi.ArgumentList.Add("--home");
            psi.ArgumentList.Add(homeDir);

            using var hostProc = Process.Start(psi)!;
            string? boundUrl = null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            for (var i = 0; i < 60; i++)
            {
                if (File.Exists(paths.HostLockFile))
                {
                    var parts = (await File.ReadAllTextAsync(paths.HostLockFile)).Trim().Split('|');
                    if (parts.Length >= 2)
                    {
                        try
                        {
                            if ((await http.GetAsync($"{parts[1]}/health")).IsSuccessStatusCode)
                            {
                                boundUrl = parts[1];
                                break;
                            }
                        }
                        catch { }
                    }
                }
                await Task.Delay(100);
            }

            Assert.NotNull(boundUrl);

            // Simulate sudden forced host termination.
            hostProc.Kill(entireProcessTree: true);
            await hostProc.WaitForExitAsync();
            await Task.Delay(150);

            // Remote listener MUST be gone immediately after termination.
            var stoppedEndpoint = new Uri(boundUrl);
            using (var stoppedClient = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork))
            {
                await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(
                    () => stoppedClient.ConnectAsync(stoppedEndpoint.Host, stoppedEndpoint.Port));
            }

            // Verify clean restart on the exact same portable directory works seamlessly.
            cfg.Connection.Mode = "localhost";
            cfg.Connection.TunnelProvider = "none";
            cfg.Save(paths);
            await using var recovered = new LocalIdeBridge.Core.WorkbenchHost(paths, cfg);
            await recovered.StartNetworkListenerAsync();
            Assert.True((await http.GetAsync($"{recovered.BoundLocalUrl}/health")).IsSuccessStatusCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
