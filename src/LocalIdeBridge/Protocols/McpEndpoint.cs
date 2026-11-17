using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;

namespace LocalIdeBridge.Protocols;

/// <summary>
/// Standards-compliant Model Context Protocol (MCP) server implementing both:
/// 1. Streamable HTTP transport at /mcp (POST JSON + SSE streaming, GET SSE with Last-Event-ID,
///    DELETE session termination; compatible with 2025-03-26, 2025-06-18, 2025-11-25, and stateless 2026-07-28).
/// 2. Local stdio transport (newline-delimited JSON-RPC 2.0) for desktop/CLI MCP hosts.
/// All tool execution routes directly through the single shared ToolRegistry.
/// </summary>
public sealed class McpServer
{
    public const string DefaultProtocolVersion = "2025-06-18";
    private static readonly HashSet<string> SupportedVersions = new(StringComparer.Ordinal)
    {
        "2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"
    };

    private sealed record McpSessionInfo(string Id, string ProtocolVersion, DateTimeOffset CreatedAtUtc);

    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly StateDatabase _db;
    private readonly ToolRegistry _registry;
    private readonly ConnectionInstructions _instructions;
    private readonly RedactingLogger _logger;
    private readonly BoundedLruCache<string, McpSessionInfo> _mcpSessions = new(128, 256 * 1024, StringComparer.Ordinal);
    private readonly BoundedRingBuffer<string> _sseNotifications = new(128);
    private readonly object _sseSubLock = new();
    private readonly List<TaskCompletionSource> _sseWaiters = [];

    public McpServer(
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        ToolRegistry registry,
        ConnectionInstructions instructions,
        RedactingLogger logger)
    {
        _config = config;
        _guard = guard;
        _db = db;
        _registry = registry;
        _instructions = instructions;
        _logger = logger;

        _registry.OnToolCompleted += (toolName, result, ms) =>
        {
            var note = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new JsonObject
                {
                    ["level"] = result.Success ? "info" : "error",
                    ["logger"] = "LocalIdeBridge",
                    ["data"] = new JsonObject
                    {
                        ["tool"] = toolName,
                        ["success"] = result.Success,
                        ["durationMs"] = ms,
                        ["summary"] = result.Summary
                    }
                }
            };
            _sseNotifications.Push(note.ToJsonString());
            SignalSseWaiters();
        };
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/mcp", HandlePostAsync);
        app.MapGet("/mcp", HandleGetSseAsync);
        app.MapDelete("/mcp", HandleDeleteSession);
    }

    private async Task HandlePostAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var reqSessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        var reqProtoHeader = ctx.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
        var isStateless2026 = string.Equals(reqProtoHeader, "2026-07-28", StringComparison.Ordinal);

        string body;
        using (var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        {
            body = await sr.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteJsonRpcErrorHttp(ctx, StatusCodes.Status400BadRequest, null, -32700, "Empty JSON-RPC request body.");
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            await WriteJsonRpcErrorHttp(ctx, StatusCodes.Status400BadRequest, null, -32700, $"Parse error: {ex.Message}");
            return;
        }

        // Validate explicit Mcp-Session-Id on non-initialize calls (2025-xx Streamable HTTP compliance).
        var isInitialize = parsed is JsonObject singleObj &&
                           string.Equals(singleObj["method"]?.GetValue<string>(), "initialize", StringComparison.Ordinal);

        if (!isStateless2026 && !isInitialize && !string.IsNullOrWhiteSpace(reqSessionId))
        {
            if (!_mcpSessions.TryGet(reqSessionId, out _))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32001, message = "Unknown or expired Mcp-Session-Id. Send a new initialize request." },
                    id = (object?)null
                }, ct);
                return;
            }
        }

        string? mintedSessionId = null;
        string negotiatedProtocol = reqProtoHeader ?? DefaultProtocolVersion;
        var responses = new List<JsonObject>();

        if (parsed is JsonArray batch)
        {
            if (batch.Count == 0 || batch.Count > 64)
            {
                await WriteJsonRpcErrorHttp(ctx, StatusCodes.Status400BadRequest, null, -32600, "Invalid JSON-RPC batch size.");
                return;
            }
            foreach (var item in batch)
            {
                if (item is not JsonObject msgObj)
                {
                    responses.Add(MakeError(null, -32600, "Batch item must be a JSON object."));
                    continue;
                }
                var (resp, newSid, negVer) = await DispatchMessageAsync(msgObj, isStateless2026, ct);
                if (newSid is not null) { mintedSessionId = newSid; negotiatedProtocol = negVer; }
                if (resp is not null) responses.Add(resp);
            }
        }
        else if (parsed is JsonObject single)
        {
            var (resp, newSid, negVer) = await DispatchMessageAsync(single, isStateless2026, ct);
            if (newSid is not null) { mintedSessionId = newSid; negotiatedProtocol = negVer; }
            if (resp is not null) responses.Add(resp);
        }
        else
        {
            await WriteJsonRpcErrorHttp(ctx, StatusCodes.Status400BadRequest, null, -32600, "JSON-RPC payload must be an object or array.");
            return;
        }

        if (mintedSessionId is not null && !isStateless2026)
            ctx.Response.Headers["Mcp-Session-Id"] = mintedSessionId;
        ctx.Response.Headers["MCP-Protocol-Version"] = negotiatedProtocol;

        // Notifications/responses only -> 202 Accepted with no body per MCP Streamable HTTP spec.
        if (responses.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        // Upgrade response to request-scoped SSE stream when client explicitly prefers SSE.
        var accept = ctx.Request.Headers.Accept.ToString();
        var wantsSseOnly = (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
                            !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)) ||
                           string.Equals(ctx.Request.Query["stream"], "sse", StringComparison.OrdinalIgnoreCase);

        if (wantsSseOnly)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
            var seq = 1;
            foreach (var r in responses)
            {
                var frame = $"event: message\nid: {seq++}\ndata: {r.ToJsonString()}\n\n";
                await ctx.Response.WriteAsync(frame, ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (parsed is JsonArray)
        {
            var outArr = new JsonArray();
            foreach (var r in responses) outArr.Add(r);
            await ctx.Response.WriteAsync(outArr.ToJsonString(), ct);
        }
        else
        {
            await ctx.Response.WriteAsync(responses[0].ToJsonString(), ct);
        }
    }

    private async Task HandleGetSseAsync(HttpContext ctx)
    {
        var reqProto = ctx.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
        if (!_config.Integrations.EnableMcpSseStream ||
            string.Equals(reqProto, "2026-07-28", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var sid = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sid) && !_mcpSessions.TryGet(sid, out _))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = ctx.RequestAborted;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
        await ctx.Response.WriteAsync(": LocalIdeBridge MCP Streamable HTTP connected\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        long cursor = _sseNotifications.LatestSequence;
        if (long.TryParse(ctx.Request.Headers["Last-Event-ID"].FirstOrDefault(), out var lastId) && lastId >= 0)
            cursor = lastId;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pending = _sseNotifications.SnapshotAfter(cursor, 50);
                foreach (var item in pending)
                {
                    await ctx.Response.WriteAsync($"event: message\nid: {item.Sequence}\ndata: {item.Item}\n\n", ct);
                    cursor = item.Sequence;
                }
                if (pending.Count > 0)
                    await ctx.Response.Body.FlushAsync(ct);

                var waiter = RegisterSseWaiter();
                if (waiter is null)
                {
                    await ctx.Response.WriteAsync(": notification stream capacity reached; reconnect later\n\n", ct);
                    break;
                }
                try
                {
                    // A notification may arrive between SnapshotAfter() and subscription.
                    if (_sseNotifications.LatestSequence > cursor)
                        continue;

                    var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                    if (completed != waiter.Task && !ct.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                finally
                {
                    // Timed-out/disconnected requests must not occupy the bounded waiter list.
                    lock (_sseSubLock) _sseWaiters.Remove(waiter);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private IResult HandleDeleteSession(HttpContext ctx)
    {
        var sid = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sid))
            return Results.BadRequest(new { error = "Missing Mcp-Session-Id header." });

        if (!_mcpSessions.TryGet(sid, out _))
            return Results.NotFound(new { error = "Session not found." });

        _mcpSessions.Invalidate(sid);
        _logger.Info("mcp", $"Terminated MCP session '{sid}'.");
        return Results.Ok(new { terminated = true });
    }

    /// <summary>
    /// Runs a local stdio MCP server loop (newline-delimited UTF-8 JSON-RPC on stdin/stdout).
    /// Useful for desktop/CLI MCP clients spawned locally without any network listener.
    /// </summary>
    public async Task RunStdioAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? response;
            try
            {
                var node = JsonNode.Parse(line);
                if (node is not JsonObject obj)
                {
                    response = MakeError(null, -32600, "Expected JSON-RPC object.");
                }
                else
                {
                    (response, _, _) = await DispatchMessageAsync(obj, isStateless2026: false, ct);
                }
            }
            catch (JsonException ex)
            {
                response = MakeError(null, -32700, $"Parse error: {ex.Message}");
            }

            if (response is not null)
            {
                await writer.WriteLineAsync(response.ToJsonString());
            }
        }
    }

    private async Task<(JsonObject? Response, string? MintedSessionId, string ProtocolVersion)> DispatchMessageAsync(
        JsonObject msg,
        bool isStateless2026,
        CancellationToken ct)
    {
        var jsonrpc = msg["jsonrpc"]?.GetValueKind() == JsonValueKind.String ? msg["jsonrpc"]!.GetValue<string>() : null;
        var hasId = msg.TryGetPropertyValue("id", out var idNode);
        var method = msg["method"]?.GetValueKind() == JsonValueKind.String ? msg["method"]!.GetValue<string>() : null;

        if (jsonrpc != "2.0")
            return (MakeError(idNode, -32600, "Invalid Request: jsonrpc must be '2.0'."), null, DefaultProtocolVersion);

        // Client response to a server message -> acknowledge with 202.
        if (method is null && hasId && (msg.ContainsKey("result") || msg.ContainsKey("error")))
            return (null, null, DefaultProtocolVersion);

        if (string.IsNullOrWhiteSpace(method))
            return (MakeError(idNode, -32600, "Invalid Request: missing 'method'."), null, DefaultProtocolVersion);

        var paramsObj = msg["params"] as JsonObject ?? new JsonObject();

        // JSON-RPC notifications do not include an 'id' property.
        if (!hasId)
        {
            return (null, null, DefaultProtocolVersion);
        }

        switch (method)
        {
            case "initialize":
            {
                var clientVer = paramsObj.OptionalString("protocolVersion", DefaultProtocolVersion)!;
                var negotiated = SupportedVersions.Contains(clientVer) ? clientVer : DefaultProtocolVersion;
                string? sid = null;
                if (!isStateless2026 && negotiated != "2026-07-28")
                {
                    sid = GenerateSessionId();
                    _mcpSessions.Set(sid, new McpSessionInfo(sid, negotiated, DateTimeOffset.UtcNow), 256);
                }

                var result = new JsonObject
                {
                    ["protocolVersion"] = negotiated,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                        ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                        ["prompts"] = new JsonObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "LocalIdeBridge",
                        ["version"] = "1.0.0"
                    },
                    ["instructions"] =
                        "LocalIdeBridge exposes trusted local software-engineering tools (workspace inspection, Roslyn C# semantic code intelligence, atomic file editing/patching, builds, tests, Git, async jobs, and DAG workflows). Start with workspace_status and repo_tree. Use job_start for long-running commands."
                };

                _logger.Info("mcp", $"Initialized MCP client (protocol={negotiated}{(sid is null ? ", stateless" : $", session={sid[..12]}...")}).");
                return (MakeResult(idNode, result), sid, negotiated);
            }

            case "ping":
                return (MakeResult(idNode, new JsonObject()), null, DefaultProtocolVersion);

            case "tools/list":
            {
                var toolsArr = new JsonArray();
                foreach (var t in _registry.All)
                {
                    toolsArr.Add(new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.BuildJsonSchema(),
                        ["annotations"] = new JsonObject
                        {
                            ["title"] = t.Name,
                            ["readOnlyHint"] = t.IsReadOnly,
                            ["idempotentHint"] = t.IsIdempotent,
                            ["destructiveHint"] = t.Name is "delete_file" or "git_push",
                            ["openWorldHint"] = t.Name is "git_push" or "run_command"
                        }
                    });
                }
                return (MakeResult(idNode, new JsonObject { ["tools"] = toolsArr }), null, DefaultProtocolVersion);
            }

            case "tools/call":
            {
                var toolName = paramsObj.OptionalString("name");
                if (string.IsNullOrWhiteSpace(toolName))
                    return (MakeError(idNode, -32602, "Invalid params: 'name' is required for tools/call."), null, DefaultProtocolVersion);

                JsonObject? toolArgs = null;
                if (paramsObj.TryGetPropertyValue("arguments", out var argsNode) && argsNode is not null)
                {
                    if (argsNode is not JsonObject obj)
                        return (MakeError(idNode, -32602, "Invalid params: 'arguments' must be a JSON object."), null, DefaultProtocolVersion);
                    toolArgs = obj;
                }

                if (_registry.Find(toolName) is null)
                    return (MakeError(idNode, -32602, $"Unknown tool '{toolName}'."), null, DefaultProtocolVersion);

                var res = await _registry.InvokeAsync(toolName, toolArgs, null, ct);
                var structuredNode = JsonSerializer.SerializeToNode(res, AppConfig.JsonOptions) as JsonObject ?? new JsonObject();
                var textPayload = structuredNode.ToJsonString(AppConfig.JsonOptions);

                var callResult = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = textPayload
                        }
                    },
                    ["structuredContent"] = structuredNode.DeepClone(),
                    ["isError"] = !res.Success
                };
                return (MakeResult(idNode, callResult), null, DefaultProtocolVersion);
            }

            case "resources/list":
            {
                var resources = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = "bridge://workspace/status",
                        ["name"] = "Active Workspace Status",
                        ["description"] = "Current trusted workspace, active session, steering note, and autonomy policy.",
                        ["mimeType"] = "application/json"
                    },
                    new JsonObject
                    {
                        ["uri"] = "bridge://session/active",
                        ["name"] = "Active Session Summary",
                        ["description"] = "Paginated summary of current session, running jobs, and local supervisor steering note.",
                        ["mimeType"] = "application/json"
                    },
                    new JsonObject
                    {
                        ["uri"] = "bridge://docs/agent-quickstart",
                        ["name"] = "Agent Quickstart Instructions",
                        ["description"] = "Concise instructions and limitations for connected AI agents.",
                        ["mimeType"] = "text/markdown"
                    }
                };
                return (MakeResult(idNode, new JsonObject { ["resources"] = resources }), null, DefaultProtocolVersion);
            }

            case "resources/read":
            {
                var uri = paramsObj.OptionalString("uri");
                if (string.IsNullOrWhiteSpace(uri))
                    return (MakeError(idNode, -32602, "Missing 'uri' parameter."), null, DefaultProtocolVersion);

                string mime = "application/json";
                string text;
                if (uri == "bridge://workspace/status")
                {
                    var st = await _registry.InvokeAsync("workspace_status", null, null, ct);
                    text = JsonSerializer.Serialize(st, AppConfig.JsonOptions);
                }
                else if (uri == "bridge://session/active")
                {
                    var si = await _registry.InvokeAsync("session_inspect", new JsonObject { ["toolCallLimit"] = 15 }, null, ct);
                    text = JsonSerializer.Serialize(si, AppConfig.JsonOptions);
                }
                else if (uri == "bridge://docs/agent-quickstart")
                {
                    mime = "text/markdown";
                    text = _instructions.BuildAgentInstructions();
                }
                else
                {
                    return (MakeError(idNode, -32602, $"Unknown resource URI '{uri}'."), null, DefaultProtocolVersion);
                }

                return (MakeResult(idNode, new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }
                    }
                }), null, DefaultProtocolVersion);
            }

            case "prompts/list":
            {
                var prompts = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "edit_build_test_commit",
                        ["description"] = "Structured workflow guide for inspecting, editing, compiling, testing, and committing safely in the active workspace.",
                        ["arguments"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "task", ["description"] = "Engineering task description.", ["required"] = true }
                        }
                    }
                };
                return (MakeResult(idNode, new JsonObject { ["prompts"] = prompts }), null, DefaultProtocolVersion);
            }

            case "prompts/get":
            {
                var name = paramsObj.OptionalString("name");
                if (name != "edit_build_test_commit")
                    return (MakeError(idNode, -32602, $"Unknown prompt '{name}'."), null, DefaultProtocolVersion);
                var task = (paramsObj["arguments"] as JsonObject)?.OptionalString("task", "Complete the requested code change.")!;
                var promptResult = new JsonObject
                {
                    ["description"] = "Autonomous edit-build-test-commit engineering sequence.",
                    ["messages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] =
                                    $"Task: {task}\n\n" +
                                    $"Active workspace: {_guard.ActiveWorkspace ?? "(none selected)"}\n" +
                                    "Recommended sequence: 1) workspace_status & project_discover, 2) symbol_search/text_search & read_file, " +
                                    "3) apply_patch or replace_range (pass expectedSha256), 4) dotnet_build, 5) dotnet_test, " +
                                    "6) git_diff review & git_commit (local only; do not push unless authorized)."
                            }
                        }
                    }
                };
                return (MakeResult(idNode, promptResult), null, DefaultProtocolVersion);
            }

            default:
                return (MakeError(idNode, -32601, $"Method not found: '{method}'."), null, DefaultProtocolVersion);
        }
    }

    private TaskCompletionSource? RegisterSseWaiter()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sseSubLock)
        {
            if (_sseWaiters.Count >= 32)
                return null; // Fail closed: no busy-loop for a 33rd streaming connection.
            _sseWaiters.Add(tcs);
        }
        return tcs;
    }

    private void SignalSseWaiters()
    {
        List<TaskCompletionSource> copy;
        lock (_sseSubLock)
        {
            if (_sseWaiters.Count == 0) return;
            copy = [.. _sseWaiters];
            _sseWaiters.Clear();
        }
        foreach (var w in copy) w.TrySetResult();
    }

    private static string GenerateSessionId()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "mcp_" + Convert.ToHexStringLower(bytes);
    }

    private static JsonObject MakeResult(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject MakeError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static async Task WriteJsonRpcErrorHttp(HttpContext ctx, int statusCode, JsonNode? id, int rpcCode, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(MakeError(id, rpcCode, message).ToJsonString());
    }
}
