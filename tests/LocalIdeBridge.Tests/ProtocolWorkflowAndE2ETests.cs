using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Cli;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

// GC.GetTotalMemory measures the entire test process, not just this test.
// Prevent allocations from unrelated concurrently running tests skewing the
// bounded-memory regression check.
[CollectionDefinition("BoundedMemorySerial", DisableParallelization = true)]
public sealed class BoundedMemorySerialCollection { }

[Collection("BoundedMemorySerial")]
public sealed class ProtocolWorkflowAndE2ETests
{
    [Fact]
    public async Task McpStreamableHttp_SupportsFullLifecycle_SseMode_SessionDelete_Stateless2026_AndStdio()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();

        // 1. initialize -> returns Mcp-Session-Id header and protocolVersion.
        var initResp = await client.PostAsync("mcp", TestSandbox.JsonBody(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "xUnitMcpClient", version = "1.0" }
            }
        }));
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        Assert.True(initResp.Headers.TryGetValues("Mcp-Session-Id", out var sidValues));
        var mcpSessionId = sidValues!.Single();
        Assert.StartsWith("mcp_", mcpSessionId);

        // 2. notifications/initialized -> 202 Accepted with empty body.
        var noteReq = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new { jsonrpc = "2.0", method = "notifications/initialized" })
        };
        noteReq.Headers.Add("Mcp-Session-Id", mcpSessionId);
        var noteResp = await client.SendAsync(noteReq);
        Assert.Equal(HttpStatusCode.Accepted, noteResp.StatusCode);

        // 3. tools/list discovers all registered tools with JSON schemas.
        var listReq = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new { jsonrpc = "2.0", id = 2, method = "tools/list" })
        };
        listReq.Headers.Add("Mcp-Session-Id", mcpSessionId);
        var listBody = JsonNode.Parse(await (await client.SendAsync(listReq)).Content.ReadAsStringAsync())!;
        var tools = listBody["result"]!["tools"]!.AsArray();
        Assert.True(tools.Count >= 40);

        // 4. tools/call in SSE response streaming mode.
        var sseReq = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "workspace_status", arguments = new { } }
            })
        };
        sseReq.Headers.Add("Mcp-Session-Id", mcpSessionId);
        sseReq.Headers.Accept.Clear();
        sseReq.Headers.Accept.ParseAdd("text/event-stream");
        var sseResp = await client.SendAsync(sseReq);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        Assert.Equal("text/event-stream", sseResp.Content.Headers.ContentType?.MediaType);
        var sseText = await sseResp.Content.ReadAsStringAsync();
        Assert.Contains("event: message", sseText);
        Assert.Contains("\"isError\":false", sseText);

        // 5. resources/list & resources/read (including steering note).
        sb.Host.Database.SetSteeringNote("Focus on Calculator unit tests.");
        var resRead = await client.PostAsync("mcp", TestSandbox.JsonBody(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "resources/read",
            @params = new { uri = "bridge://workspace/status" }
        }));
        Assert.Contains("Focus on Calculator unit tests.", await resRead.Content.ReadAsStringAsync());

        // 6. DELETE /mcp terminates session -> subsequent call with old session ID returns 404 Not Found.
        var delReq = new HttpRequestMessage(HttpMethod.Delete, "mcp");
        delReq.Headers.Add("Mcp-Session-Id", mcpSessionId);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(delReq)).StatusCode);

        var afterDel = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new { jsonrpc = "2.0", id = 5, method = "ping" })
        };
        afterDel.Headers.Add("Mcp-Session-Id", mcpSessionId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(afterDel)).StatusCode);

        // 7. Stateless 2026-07-28 MCP request succeeds without initialize or Mcp-Session-Id.
        var statelessReq = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new
            {
                jsonrpc = "2.0",
                id = 6,
                method = "tools/list",
                @params = new
                {
                    _meta = new Dictionary<string, object>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    }
                }
            })
        };
        statelessReq.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        statelessReq.Headers.Add("Mcp-Method", "tools/list");
        var statelessResp = await client.SendAsync(statelessReq);
        Assert.Equal(HttpStatusCode.OK, statelessResp.StatusCode);
        Assert.False(statelessResp.Headers.Contains("Mcp-Session-Id"));

        // 8. MCP stdio transport works without network.
        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"ping\"}\n"));
        using var outStream = new MemoryStream();
        await sb.Host.Mcp.RunStdioAsync(inStream, outStream, CancellationToken.None);
        var stdioReply = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"id\":99", stdioReply);
    }

    [Fact]
    public async Task HttpApi_OpenApi_AndCliClient_ShareExecutionEngine_AndReturnExpectedExitCodes()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();

        // Verify OpenAPI 3.1.0 contract includes every tool from ToolRegistry.
        var openApiResp = await client.GetAsync("openapi.json");
        Assert.Equal(HttpStatusCode.OK, openApiResp.StatusCode);
        var openApi = JsonNode.Parse(await openApiResp.Content.ReadAsStringAsync())!;
        Assert.Equal("3.1.0", openApi["openapi"]!.GetValue<string>());
        Assert.NotNull(openApi["paths"]!["/api/v1/tools/apply_patch/invoke"]);
        Assert.NotNull(openApi["paths"]!["/api/v1/tools/dotnet_test/invoke"]);

        // CLI client invokes host, discovers tools, creates a file, and produces proper exit codes.
        var key = sb.Host.Credentials.GetSecretForExplicitCopy();
        var ep = sb.Host.BoundLocalUrl;

        Assert.Equal(0, await CliClient.RunAsync(sb.Paths, ["status", "--endpoint", ep, "--key", key, "--json"], CancellationToken.None));
        Assert.Equal(0, await CliClient.RunAsync(sb.Paths, ["tools", "--endpoint", ep, "--key", key, "--json"], CancellationToken.None));
        Assert.Equal(0, await CliClient.RunAsync(sb.Paths,
            ["call", "create_file", "--endpoint", ep, "--key", key, "--arg", "path=from-cli.txt", "--arg", "content=hello cli", "--json"],
            CancellationToken.None));
        Assert.Equal("hello cli", (await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "from-cli.txt"))).Trim());

        // Bad key -> exit code 3; tool failure (not found) -> exit code 4.
        Assert.Equal(3, await CliClient.RunAsync(sb.Paths, ["status", "--endpoint", ep, "--key", "lib_bad", "--json"], CancellationToken.None));
        Assert.Equal(4, await CliClient.RunAsync(sb.Paths,
            ["call", "read_file", "--endpoint", ep, "--key", key, "--arg", "path=missing.txt", "--json"], CancellationToken.None));
    }

    [Fact]
    public async Task WorkflowOrchestrator_CrashRecovery_Reconnect_BoundedMemory_AndCleanShutdown()
    {
        await using var sb = await TestSandbox.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "item.txt"), "hello world\n");

        // 1. Workflow with parallel read-only steps, failed prerequisite skipping downstream step, and on_failure branch!
        var wfRes = await sb.Host.Registry.InvokeAsync("workflow_run", JsonNode.Parse("""
            {
              "name": "prereq-test",
              "steps": [
                { "id": "tree", "tool": "repo_tree", "arguments": { "maxDepth": 2 } },
                { "id": "search", "tool": "text_search", "arguments": { "query": "hello" } },
                { "id": "bad_read", "tool": "read_file", "arguments": { "path": "does_not_exist.txt" }, "dependsOn": ["tree", "search"] },
                { "id": "must_skip", "tool": "create_file", "arguments": { "path": "should_not_exist.txt", "content": "no" }, "dependsOn": ["bad_read"] },
                { "id": "on_fail_report", "tool": "workspace_status", "dependsOn": ["bad_read"], "runIf": "on_failure" }
              ]
            }
            """)!.AsObject());

        Assert.False(wfRes.Success);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "should_not_exist.txt")));
        var steps = JsonSerializer.SerializeToElement(wfRes.Data, AppConfig.JsonOptions).GetProperty("steps");
        Assert.Equal("succeeded", steps[0].GetProperty("status").GetString());
        Assert.Equal("succeeded", steps[1].GetProperty("status").GetString());
        Assert.Equal("failed", steps[2].GetProperty("status").GetString());
        Assert.Equal("skipped", steps[3].GetProperty("status").GetString());
        Assert.Equal("succeeded", steps[4].GetProperty("status").GetString());

        // 2. Async job persists across client disconnect/reconnect.
        string jobId;
        using (var client1 = sb.CreateClient())
        {
            var r = await client1.PostAsync("api/v1/jobs", TestSandbox.JsonBody(new { tool = "repo_tree", arguments = new { } }));
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            jobId = JsonNode.Parse(await r.Content.ReadAsStringAsync())!["data"]!["jobId"]!.GetValue<string>();
        }
        await Task.Delay(250);
        using (var client2 = sb.CreateClient())
        {
            var jr = await client2.GetAsync($"api/v1/jobs/{jobId}/events?afterEventId=0");
            Assert.Equal(HttpStatusCode.OK, jr.StatusCode);
            Assert.Contains("succeeded", await jr.Content.ReadAsStringAsync());
        }

        // 3. Bounded *steady-state* memory growth over 500 synthetic operations.
        // First-use JIT, serializer and SQLite initialization can inflate a cold
        // process-wide heap snapshot without representing retained per-call memory.
        // Warm the complete invocation path; then measure another full batch.
        for (var i = 0; i < 500; i++)
        {
            var warmup = await sb.Host.Registry.InvokeAsync("read_range",
                new JsonObject { ["path"] = "item.txt", ["startLine"] = 1, ["endLine"] = 5 });
            Assert.True(warmup.Success);
        }

        var baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < 500; i++)
        {
            var r = await sb.Host.Registry.InvokeAsync("read_range",
                new JsonObject { ["path"] = "item.txt", ["startLine"] = 1, ["endLine"] = 5 });
            Assert.True(r.Success);
        }
        var afterHeap = GC.GetTotalMemory(forceFullCollection: true);
        var growthMb = (afterHeap - baselineHeap) / (1024.0 * 1024.0);
        Assert.True(growthMb < 8.0,
            $"Expected bounded steady-state live heap growth (<8 MB over 500 calls), measured {growthMb:F2} MB.");

        // 4. Simulate an in-flight non-idempotent job left behind by a crash, verify shutdown & restart recovery!
        var crashedJobId = "job_simulated_crash_01";
        sb.Host.Database.CreateJob(crashedJobId, sb.Host.Database.ActiveSessionId, sb.WorkspaceDir, "git_commit", "{\"message\":\"x\"}", isIdempotent: false);
        sb.Host.Database.UpdateJobStatus(crashedJobId, "running");

        var oldUrl = sb.Host.BoundLocalUrl;
        await sb.Host.DisposeAsync();

        // Listener must be completely unreachable after shutdown.
        await AssertPortClosedAsync(oldUrl);
        Assert.False(File.Exists(sb.Paths.HostLockFile));

        // Restart host on same portable directory -> crashed job marked interrupted_restart, NEVER replayed.
        await using var restarted = new WorkbenchHost(sb.Paths);
        Assert.True(restarted.RecoveredInterruptedItems >= 1);
        var recoveredJob = restarted.Database.GetJob(crashedJobId);
        Assert.NotNull(recoveredJob);
        Assert.Equal("interrupted_restart", recoveredJob!.Status);
    }

    private static async Task AssertPortClosedAsync(string baseUrl)
    {
        var endpoint = new Uri(baseUrl);
        using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(
            () => client.ConnectAsync(endpoint.Host, endpoint.Port));
    }

    [Fact]
    public async Task EndToEnd_Full10StepDisposableCSharpProjectWorkflow_OverMcp()
    {
        await using var sb = await TestSandbox.CreateAsync();

        // Seed a realistic disposable .NET 10 library + self-verifying test project in the workspace.
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "MathLib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "MathOps.cs"), """
            namespace MathLib;

            public static class MathOps
            {
                // Intentional bug for the AI agent to fix: subtracts instead of adding!
                public static int Add(int a, int b) => a - b;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Program.cs"), """
            using MathLib;
            var actual = MathOps.Add(7, 5);
            if (actual != 12)
            {
                Console.Error.WriteLine($"FAIL: expected 12 but got {actual}");
                return 1;
            }
            Console.WriteLine("PASS: MathOps.Add(7, 5) == 12");
            return 0;
            """);

        // Initialize git repo for commit test.
        Assert.True((await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject { ["action"] = "init", ["name"] = "main" })).Success);
        Assert.True((await sb.Host.Registry.InvokeAsync("git_commit",
            new JsonObject { ["message"] = "Seed disposable project", ["stageAll"] = true, ["authorName"] = "Dev", ["authorEmail"] = "dev@local" })).Success);

        using var client = sb.CreateClient();
        int rpcId = 1;

        async Task<JsonObject> McpCallAsync(string method, object @params, string? sessionId = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = TestSandbox.JsonBody(new { jsonrpc = "2.0", id = rpcId++, method, @params })
            };
            if (sessionId is not null) req.Headers.Add("Mcp-Session-Id", sessionId);
            var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        }

        // Step 1: Connect (MCP initialize).
        var initReq = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = TestSandbox.JsonBody(new
            {
                jsonrpc = "2.0", id = rpcId++, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "E2EAgent", version = "1.0" } }
            })
        };
        var initResp = await client.SendAsync(initReq);
        initResp.EnsureSuccessStatusCode();
        var sid = initResp.Headers.GetValues("Mcp-Session-Id").Single();

        // Step 2: Discover tools.
        var list = await McpCallAsync("tools/list", new { }, sid);
        Assert.True(list["result"]!["tools"]!.AsArray().Count >= 40);

        // Step 3: Select workspace.
        var sel = await McpCallAsync("tools/call", new { name = "workspace_select", arguments = new { workspace = "disposable" } }, sid);
        Assert.False(sel["result"]!["isError"]!.GetValue<bool>());

        // Step 4: Read source.
        var read = await McpCallAsync("tools/call", new { name = "read_file", arguments = new { path = "MathOps.cs" } }, sid);
        Assert.False(read["result"]!["isError"]!.GetValue<bool>());
        var sha = read["result"]!["structuredContent"]!["data"]!["sha256"]!.GetValue<string>();

        // Step 5: Search code (semantic symbol_search + text_search).
        var search = await McpCallAsync("tools/call", new { name = "symbol_search", arguments = new { query = "Add" } }, sid);
        Assert.False(search["result"]!["isError"]!.GetValue<bool>());

        // Step 6: Apply unified diff patch fixing the bug.
        var patch = """
            --- a/MathOps.cs
            +++ b/MathOps.cs
            @@ -3,5 +3,5 @@
             public static class MathOps
             {
                 // Intentional bug for the AI agent to fix: subtracts instead of adding!
            -    public static int Add(int a, int b) => a - b;
            +    public static int Add(int a, int b) => a + b;
             }
            """;
        var patched = await McpCallAsync("tools/call", new { name = "apply_patch", arguments = new { patch, expectedSha256 = sha } }, sid);
        Assert.False(patched["result"]!["isError"]!.GetValue<bool>());

        // Step 7: Compile (dotnet_build).
        var build = await McpCallAsync("tools/call", new { name = "dotnet_build", arguments = new { target = "MathLib.csproj" } }, sid);
        Assert.False(build["result"]!["isError"]!.GetValue<bool>());

        // Step 8: Execute verification test run.
        var run = await McpCallAsync("tools/call", new
        {
            name = "run_command",
            arguments = new { command = "dotnet", arguments = new[] { "run", "--no-build", "--project", "MathLib.csproj" } }
        }, sid);
        Assert.False(run["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("PASS: MathOps.Add(7, 5) == 12", run.ToJsonString());

        // Step 9: Create local Git commit.
        var commit = await McpCallAsync("tools/call", new
        {
            name = "git_commit",
            arguments = new { message = "fix: add operands in MathOps.Add", stageAll = true }
        }, sid);
        Assert.False(commit["result"]!["isError"]!.GetValue<bool>());

        // Step 10: Read final workspace & session status (verifying clean tree + recorded commit + audit trail).
        var finalGit = await McpCallAsync("tools/call", new { name = "git_status", arguments = new { recentCommits = 3 } }, sid);
        Assert.False(finalGit["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(0, finalGit["result"]!["structuredContent"]!["data"]!["changedCount"]!.GetValue<int>());
        Assert.Contains("fix: add operands in MathOps.Add", finalGit.ToJsonString());

        var sess = await McpCallAsync("tools/call", new { name = "session_inspect", arguments = new { toolCallLimit = 20 } }, sid);
        Assert.False(sess["result"]!["isError"]!.GetValue<bool>());
        Assert.True(sess["result"]!["structuredContent"]!["data"]!["gitCheckpoints"]!.AsArray().Count >= 2);
    }
}
