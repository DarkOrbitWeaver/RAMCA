using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

/// <summary>
/// Real loopback transport tests; no external tokens or valuable workspaces are used.
/// </summary>
public sealed class ModernMcpAndJobResilienceTests
{
    private static JsonObject Request(string method, JsonObject? parameters = null)
    {
        var args = parameters ?? new JsonObject();
        args["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject
            {
                ["name"] = "ramca-regression-test", ["version"] = "1.0"
            }
        };
        return new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = args
        };
    }

    private static HttpRequestMessage Post(JsonObject body, string? routeName = null, string? headerMethod = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", headerMethod ?? body["method"]!.GetValue<string>());
        if (routeName is not null) request.Headers.TryAddWithoutValidation("Mcp-Name", routeName);
        return request;
    }

    [Fact]
    public async Task ModernDiscoverWithoutInitialize_AdvertisesCapabilities()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();
        using var request = Post(Request("server/discover"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("complete", parsed["result"]!["resultType"]!.GetValue<string>());
        Assert.Equal("2026-07-28", parsed["result"]!["supportedVersions"]![0]!.GetValue<string>());
        Assert.Equal("LocalIdeBridge", parsed["result"]!["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ModernToolsListAndCallRequireMatchingRoutingHeaders()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();
        using (var list = Post(Request("tools/list")))
        using (var response = await client.SendAsync(list))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("private", body["result"]!["cacheScope"]!.GetValue<string>());
            var tools = body["result"]!["tools"]!.AsArray();
            Assert.Contains(tools, t => t?["name"]?.GetValue<string>() == "execute_once");
            var command = Assert.Single(tools, t => t?["name"]?.GetValue<string>() == "run_command");
            Assert.True(command!["annotations"]!["destructiveHint"]!.GetValue<bool>());
        }

        using (var call = Post(Request("tools/call", new JsonObject
        {
            ["name"] = "workspace_status", ["arguments"] = new JsonObject()
        }), routeName: "workspace_status"))
        using (var response = await client.SendAsync(call))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("complete", result["result"]!["resultType"]!.GetValue<string>());
            Assert.False(result["result"]!["isError"]!.GetValue<bool>());
        }

        using (var invalid = Post(Request("tools/call", new JsonObject
        {
            ["name"] = "workspace_status", ["arguments"] = new JsonObject()
        }), routeName: "different_tool"))
        using (var response = await client.SendAsync(invalid))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(-32020, result["error"]!["code"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentBoardDiscoveryIsOptIn(bool advertiseBoard)
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Integrations.AdvertiseAgentBoardTools = advertiseBoard);
        using var client = sb.CreateClient();
        using var request = Post(Request("tools/list"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var tools = body["result"]!["tools"]!.AsArray();
        Assert.Equal(advertiseBoard, tools.Any(t =>
            t?["name"]?.GetValue<string>().StartsWith("board_", StringComparison.Ordinal) == true));
        Assert.NotNull(sb.Host.Registry.Find("board_join"));
        Assert.Contains(tools, t => t?["name"]?.GetValue<string>() == "read_context");
    }

    [Fact]
    public async Task CompactDiscoveryKeepsSpecialistsReachableWithoutAdvertisingTheirSchemas()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Integrations.McpToolProfile = "compact");
        using var client = sb.CreateClient();
        using var request = Post(Request("tools/list"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tools = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!["tools"]!.AsArray();
        Assert.Equal(18, tools.Count);
        Assert.DoesNotContain(tools, t => t?["name"]?.GetValue<string>() == "inspect_type");
        var schema = await sb.Host.Registry.InvokeAsync("tool_discover", new JsonObject { ["query"] = "inspect_type" });
        Assert.True(schema.Success, schema.ErrorMessage);
        Assert.Contains("inputSchema", JsonSerializer.Serialize(schema.Data));
        var read = await sb.Host.Registry.InvokeAsync("tool_read", new JsonObject { ["tool"] = "workspace_status" });
        Assert.True(read.Success, read.ErrorMessage);
        var refused = await sb.Host.Registry.InvokeAsync("tool_read", new JsonObject { ["tool"] = "delete_file" });
        Assert.Equal("read_only_required", refused.ErrorCode);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("structured")]
    public async Task CompactResultModePreservesFullSourceExactlyOnce(string mode)
    {
        await using var sb = await TestSandbox.CreateAsync(cfg => cfg.Integrations.McpResultMode = mode);
        const string marker = "unique_source_payload_9382";
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "source.txt"), marker);
        using var client = sb.CreateClient();
        using var request = Post(Request("tools/call", new JsonObject
        {
            ["name"] = "read_file", ["arguments"] = new JsonObject { ["path"] = "source.txt" }
        }), routeName: "read_file");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, raw.Split(marker, StringSplitOptions.None).Length);
        var result = JsonNode.Parse(raw)!["result"]!;
        if (mode == "text")
        {
            Assert.Null(result["structuredContent"]);
            var data = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal(marker, data["data"]!["content"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(marker, result["structuredContent"]!["data"]!["content"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ModernRequestRejectsMissingCapabilityMetadataWithoutExecutingTools()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();
        var bad = Request("tools/call", new JsonObject
        {
            ["name"] = "workspace_status", ["arguments"] = new JsonObject()
        });
        bad["params"]!["_meta"]!.AsObject().Remove("io.modelcontextprotocol/clientCapabilities");
        using var request = Post(bad, routeName: "workspace_status");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BackgroundJobTimeout_IsRecordedAsTimedOut_NotUserCancelled()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        sb.Host.Registry.Register(new LocalIdeBridge.Tools.ToolDefinition(
            Name: "test_wait_for_timeout", Category: "execution", Description: "Disposable timeout test.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.", Parameters: [],
            Handler: async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return LocalIdeBridge.Tools.ToolResult.Ok("Unexpected completion.");
            }));

        var job = sb.Host.Jobs.StartToolJob("test_wait_for_timeout", null, timeoutSeconds: 1);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = sb.Host.Database.GetJob(job.Id)!.Status;
            if (status is "timed_out" or "failed" or "cancelled" or "succeeded") break;
            await Task.Delay(50);
        }
        Assert.Equal("timed_out", sb.Host.Database.GetJob(job.Id)!.Status);
    }

    [Fact]
    public async Task ExecuteOnceContinuesAfterCallerLosesConnection()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var target = Path.Combine(sb.WorkspaceDir, "detached-completion.txt");
        sb.Host.Registry.Register(new LocalIdeBridge.Tools.ToolDefinition(
            Name: "test_delayed_write", Category: "editing", Description: "Disposable delayed edit test.",
            IsReadOnly: false, IsIdempotent: false, SideEffects: "Test-only disposable file.", Parameters: [],
            Handler: async (_, _, ct) =>
            {
                await Task.Delay(300, ct);
                await File.WriteAllTextAsync(target, "completed", ct);
                return LocalIdeBridge.Tools.ToolResult.Ok("Test completed.");
            }));
        using var requestGone = new CancellationTokenSource();
        var id = "test_detached_" + Guid.NewGuid().ToString("N");
        var invocation = sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = id, ["tool"] = "test_delayed_write", ["arguments"] = new JsonObject()
        }, ct: requestGone.Token);
        await Task.Delay(80);
        requestGone.Cancel(); // Equivalent to HTTP client disconnecting mid-call.
        var completed = await invocation;
        Assert.True(completed.Success, completed.ErrorMessage);
        Assert.Equal("completed", await File.ReadAllTextAsync(target));
        var status = await sb.Host.Registry.InvokeAsync("operation_status", new JsonObject { ["operationId"] = id });
        Assert.True(status.Success);
        Assert.Contains("succeeded", JsonSerializer.Serialize(status.Data));
        var replay = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = id, ["tool"] = "test_delayed_write", ["arguments"] = new JsonObject()
        });
        Assert.True(replay.Success);
    }

    [Fact]
    public async Task UndoIsJournaledAndItsOwnUndoRestoresThePriorFile()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var target = Path.Combine(sb.WorkspaceDir, "journaled-undo.txt");
        await File.WriteAllTextAsync(target, "original\n");
        var write = await sb.Host.Registry.InvokeAsync("create_file", new JsonObject
        {
            ["path"] = "journaled-undo.txt", ["content"] = "modified\n", ["overwrite"] = true
        });
        Assert.True(write.Success, write.ErrorMessage);
        var changedId = JsonSerializer.SerializeToElement(write.Data).GetProperty("editId").GetString()!;
        var undo = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject { ["editId"] = changedId });
        Assert.True(undo.Success, undo.ErrorMessage);
        Assert.Equal("original\n", await File.ReadAllTextAsync(target));
        var undoId = JsonSerializer.SerializeToElement(undo.Data).GetProperty("undoEditId").GetString()!;
        Assert.NotNull(sb.Host.Database.GetEdit(undoId));
        var redo = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject { ["editId"] = undoId });
        Assert.True(redo.Success, redo.ErrorMessage);
        Assert.Equal("modified\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PendingTransactionUsesPortableBackupAfterRootRelocation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var full = Path.Combine(sb.WorkspaceDir, "portable-recover.txt");
        var before = "before\n";
        var after = "after\n";
        await File.WriteAllTextAsync(full, after);
        var sessionId = sb.Host.Database.ActiveSessionId;
        var editId = "edit_test_portable";
        var backup = Path.Combine(sb.Paths.GetSessionEditsDir(sessionId), editId + ".bak");
        await File.WriteAllTextAsync(backup, before);
        var directory = Path.Combine(sb.Paths.DataDir, "pending-patches");
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "patch_portable.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            id = "patch_portable", workspace = sb.WorkspaceDir, sessionId, committed = false,
            items = new[] { new
            {
                relativePath = "portable-recover.txt", editId,
                backupPath = "/old/nonexistent/portable/install/path",
                beforeHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(before))),
                afterHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(after)))
            } }
        }, AppConfig.JsonOptions));
        LocalIdeBridge.Tools.EditingTools.RecoverPendingPatchTransactions(sb.Paths, sb.Host.Guard, sb.Host.Database);
        Assert.Equal(before, await File.ReadAllTextAsync(full));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task ModernRequestsRejectIncorrectVersionAndUnboundedBatch()
    {
        await using var sb = await TestSandbox.CreateAsync();
        using var client = sb.CreateClient();
        using (var req = Post(Request("server/discover"), headerMethod: "tools/call"))
        using (var response = await client.SendAsync(req))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var batch = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(new JsonArray(Request("server/discover")).ToJsonString(), Encoding.UTF8, "application/json")
        };
        batch.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        batch.Headers.TryAddWithoutValidation("Mcp-Method", "server/discover");
        using var batchResponse = await client.SendAsync(batch);
        Assert.Equal(HttpStatusCode.BadRequest, batchResponse.StatusCode);
    }
}
