using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;
using LocalIdeBridge.Workflows;

namespace LocalIdeBridge.Protocols;

/// <summary>
/// Documented HTTPS/HTTP JSON API and dynamic OpenAPI 3.1.0 specification generator.
/// Uses the shared tool registry, state database, and job manager for execution.
/// </summary>
public static class HttpApiEndpoints
{
    public static void MapEndpoints(
        WebApplication app,
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        ToolRegistry registry,
        JobManager jobs,
        TunnelManager tunnel,
        ConnectionInstructions instructions)
    {
        // Minimal public liveness probe (no sensitive data).
        app.MapGet("/health", () => Results.Json(new
        {
            status = "online",
            service = "LocalIdeBridge",
            version = "1.0.0"
        }));

        // Authenticated server and workspace overview.
        app.MapGet("/api/v1/info", () =>
        {
            var baseUrl = tunnel.ActiveTunnelEndpoint ?? config.GetEffectiveBaseUrl();
            var session = db.GetSession(db.ActiveSessionId);
            return Results.Json(new
            {
                ok = true,
                service = "LocalIdeBridge",
                version = "1.0.0",
                status = registry.IsPaused ? "paused" : "online",
                mcpEndpoint = $"{baseUrl}/mcp",
                httpApiBase = $"{baseUrl}/api/v1",
                openApiUrl = $"{baseUrl}/openapi.json",
                connectionMode = config.Connection.Mode,
                tunnelActive = tunnel.IsRunning,
                activeWorkspace = guard.ActiveWorkspace,
                activeSessionId = db.ActiveSessionId,
                supervisorSteeringNote = session?.SteeringNote,
                toolCount = registry.All.Count,
                activeJobs = jobs.ActiveJobCount,
                totalInvocations = registry.TotalInvocations,
                totalErrors = registry.TotalErrors
            }, AppConfig.JsonOptions);
        });

        // AI-readable connection instructions (never includes the secret credential).
        app.MapGet("/api/v1/instructions", (HttpContext ctx) =>
        {
            var client = ctx.Request.Query["client"].FirstOrDefault();
            return Results.Json(new
            {
                ok = true,
                instructions = instructions.BuildAgentInstructions(),
                clientTemplate = string.IsNullOrWhiteSpace(client) ? null : instructions.BuildClientTemplate(client)
            }, AppConfig.JsonOptions);
        });

        // Authoritative tool list + JSON schemas.
        app.MapGet("/api/v1/tools", (HttpContext ctx) =>
        {
            var cat = ctx.Request.Query["category"].FirstOrDefault();
            var items = registry.All
                .Where(t => string.IsNullOrWhiteSpace(cat) || string.Equals(t.Category, cat, StringComparison.OrdinalIgnoreCase))
                .Select(t => new JsonObject
                {
                    ["name"] = t.Name,
                    ["category"] = t.Category,
                    ["description"] = t.Description,
                    ["readOnly"] = t.IsReadOnly,
                    ["idempotent"] = t.IsIdempotent,
                    ["sideEffects"] = t.SideEffects,
                    ["inputSchema"] = t.BuildJsonSchema()
                });
            var arr = new JsonArray();
            foreach (var item in items) arr.Add(item);
            return Results.Text(
                new JsonObject { ["ok"] = true, ["count"] = arr.Count, ["tools"] = arr }.ToJsonString(AppConfig.JsonOptions),
                "application/json; charset=utf-8");
        });

        // Invoke any registered tool synchronously (or asynchronously with ?async=true).
        app.MapPost("/api/v1/tools/{toolName}/invoke", async (string toolName, HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            if (registry.Find(toolName) is null)
                return Results.NotFound(new { ok = false, error = new { code = "unknown_tool", message = $"Unknown tool '{toolName}'." } });

            var (argsObj, parseErr) = await ReadJsonObjectBodyAsync(ctx, ct);
            if (parseErr is not null)
                return Results.BadRequest(new { ok = false, error = new { code = "malformed_json", message = parseErr } });

            // Support wrapping {"arguments":{...}} or passing direct argument properties.
            if (argsObj.Count == 1 && argsObj.TryGetPropertyValue("arguments", out var nested) && nested is JsonObject nestedObj &&
                !toolName.Equals("job_start", StringComparison.OrdinalIgnoreCase))
            {
                argsObj = nestedObj;
            }

            if (string.Equals(ctx.Request.Query["async"], "true", StringComparison.OrdinalIgnoreCase))
            {
                var job = jobs.StartToolJob(toolName, argsObj);
                return Results.Accepted($"/api/v1/jobs/{job.Id}", new
                {
                    ok = true,
                    jobId = job.Id,
                    sessionId = job.SessionId,
                    tool = job.ToolName,
                    status = job.Status
                });
            }

            var result = await registry.InvokeAsync(toolName, argsObj, null, ct);
            var status = result.Success ? StatusCodes.Status200OK : MapErrorStatusCode(result.ErrorCode);
            return Results.Json(result, AppConfig.JsonOptions, statusCode: status);
        });

        // Convenience REST routes mapped directly to shared ToolRegistry tools.
        app.MapGet("/api/v1/workspaces", async (HttpContext ctx) =>
            Results.Json(await registry.InvokeAsync("workspace_list", null, null, ctx.RequestAborted), AppConfig.JsonOptions));

        app.MapPost("/api/v1/workspaces/select", async (HttpContext ctx) =>
        {
            var (body, err) = await ReadJsonObjectBodyAsync(ctx, ctx.RequestAborted);
            if (err is not null) return Results.BadRequest(new { ok = false, error = new { code = "malformed_json", message = err } });
            var res = await registry.InvokeAsync("workspace_select", body, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapPost("/api/v1/jobs", async (HttpContext ctx) =>
        {
            var (body, err) = await ReadJsonObjectBodyAsync(ctx, ctx.RequestAborted);
            if (err is not null) return Results.BadRequest(new { ok = false, error = new { code = "malformed_json", message = err } });
            var res = await registry.InvokeAsync("job_start", body, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 202 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapGet("/api/v1/jobs", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            var args = new JsonObject();
            if (q.ContainsKey("sessionId")) args["sessionId"] = q["sessionId"].ToString();
            if (q.ContainsKey("status")) args["status"] = q["status"].ToString();
            if (int.TryParse(q["limit"], out var lim)) args["limit"] = lim;
            if (int.TryParse(q["offset"], out var off)) args["offset"] = off;
            return Results.Json(await registry.InvokeAsync("job_list", args, null, ctx.RequestAborted), AppConfig.JsonOptions);
        });

        app.MapGet("/api/v1/jobs/{jobId}", async (string jobId, HttpContext ctx) =>
        {
            var res = await registry.InvokeAsync("job_status", new JsonObject { ["jobId"] = jobId }, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapGet("/api/v1/jobs/{jobId}/events", async (string jobId, HttpContext ctx) =>
        {
            var args = new JsonObject { ["jobId"] = jobId };
            if (long.TryParse(ctx.Request.Query["afterEventId"], out var after)) args["afterEventId"] = after;
            if (int.TryParse(ctx.Request.Query["limit"], out var lim)) args["limit"] = lim;
            var res = await registry.InvokeAsync("job_events", args, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapPost("/api/v1/jobs/{jobId}/cancel", async (string jobId, HttpContext ctx) =>
        {
            var res = await registry.InvokeAsync("job_cancel", new JsonObject { ["jobId"] = jobId }, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapPost("/api/v1/workflows", async (HttpContext ctx) =>
        {
            var (body, err) = await ReadJsonObjectBodyAsync(ctx, ctx.RequestAborted);
            if (err is not null) return Results.BadRequest(new { ok = false, error = new { code = "malformed_json", message = err } });
            var res = await registry.InvokeAsync("workflow_run", body, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapGet("/api/v1/workflows/{workflowId}", async (string workflowId, HttpContext ctx) =>
        {
            var res = await registry.InvokeAsync("workflow_status", new JsonObject { ["workflowId"] = workflowId }, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapGet("/api/v1/sessions", async (HttpContext ctx) =>
        {
            var args = new JsonObject();
            if (int.TryParse(ctx.Request.Query["limit"], out var lim)) args["limit"] = lim;
            if (int.TryParse(ctx.Request.Query["offset"], out var off)) args["offset"] = off;
            return Results.Json(await registry.InvokeAsync("session_list", args, null, ctx.RequestAborted), AppConfig.JsonOptions);
        });

        app.MapGet("/api/v1/sessions/{sessionId}", async (string sessionId, HttpContext ctx) =>
        {
            var res = await registry.InvokeAsync("session_inspect", new JsonObject { ["sessionId"] = sessionId }, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        app.MapGet("/api/v1/logs/{execId}", async (string execId, HttpContext ctx) =>
        {
            var args = new JsonObject { ["execId"] = execId };
            if (int.TryParse(ctx.Request.Query["startLine"], out var s)) args["startLine"] = s;
            if (int.TryParse(ctx.Request.Query["maxLines"], out var m)) args["maxLines"] = m;
            if (ctx.Request.Query.ContainsKey("contains")) args["contains"] = ctx.Request.Query["contains"].ToString();
            var res = await registry.InvokeAsync("read_log", args, null, ctx.RequestAborted);
            return Results.Json(res, AppConfig.JsonOptions, statusCode: res.Success ? 200 : MapErrorStatusCode(res.ErrorCode));
        });

        // Machine-readable OpenAPI 3.1.0 contract generated directly from the authoritative ToolRegistry.
        if (config.Integrations.EnableOpenApi)
        {
            app.MapGet("/openapi.json", () =>
            {
                var baseUrl = tunnel.ActiveTunnelEndpoint ?? config.GetEffectiveBaseUrl();
                var spec = BuildOpenApiDocument(baseUrl, registry);
                return Results.Text(spec.ToJsonString(AppConfig.JsonOptions), "application/json; charset=utf-8");
            });
        }
    }

    public static JsonObject BuildOpenApiDocument(string baseUrl, ToolRegistry registry)
    {
        var paths = new JsonObject
        {
            ["/health"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["operationId"] = "getHealth",
                    ["summary"] = "Unauthenticated liveness check.",
                    ["security"] = new JsonArray(),
                    ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "Server is online." } }
                }
            },
            ["/mcp"] = new JsonObject
            {
                ["post"] = new JsonObject
                {
                    ["operationId"] = "mcpStreamableHttp",
                    ["summary"] = "Model Context Protocol (MCP) Streamable HTTP JSON-RPC 2.0 endpoint.",
                    ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "JSON-RPC response or SSE stream." } }
                }
            },
            ["/api/v1/info"] = SimpleGetOp("getServerInfo", "Get server, active workspace, and session summary."),
            ["/api/v1/tools"] = SimpleGetOp("listTools", "List all discoverable tools and their JSON schemas."),
            ["/api/v1/workspaces"] = SimpleGetOp("listWorkspaces", "List trusted workspaces."),
            ["/api/v1/jobs"] = new JsonObject
            {
                ["get"] = SimpleGetOp("listJobs", "List asynchronous jobs.")["get"]!.DeepClone(),
                ["post"] = SimplePostOp("startJob", "Launch a background tool job.")
            },
            ["/api/v1/workflows"] = new JsonObject
            {
                ["post"] = SimplePostOp("runWorkflow", "Execute a dependency-aware DAG workflow.")
            },
            ["/api/v1/sessions"] = SimpleGetOp("listSessions", "List paginated disk-backed sessions."),
            ["/api/v1/logs/{execId}"] = SimpleGetOp("readExecutionLog", "Read a bounded line range of a command execution log.")
        };

        // Emit explicit typed operation paths for every registered tool from the single authoritative registry.
        foreach (var tool in registry.All)
        {
            paths[$"/api/v1/tools/{tool.Name}/invoke"] = new JsonObject
            {
                ["post"] = new JsonObject
                {
                    ["operationId"] = $"invoke_{tool.Name}",
                    ["tags"] = new JsonArray(tool.Category),
                    ["summary"] = tool.Description,
                    ["description"] = $"{tool.Description} Side effects: {tool.SideEffects}",
                    ["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = tool.BuildJsonSchema()
                            }
                        }
                    },
                    ["responses"] = new JsonObject
                    {
                        ["200"] = new JsonObject
                        {
                            ["description"] = "Structured ToolResult payload.",
                            ["content"] = new JsonObject
                            {
                                ["application/json"] = new JsonObject
                                {
                                    ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ToolResult" }
                                }
                            }
                        },
                        ["401"] = new JsonObject { ["description"] = "Missing or invalid Bearer key." }
                    }
                }
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "LocalIdeBridge API",
                ["version"] = "1.0.0",
                ["description"] = "Universal coding-agent tool API sharing one execution engine with the /mcp Streamable HTTP endpoint."
            },
            ["servers"] = new JsonArray { new JsonObject { ["url"] = baseUrl } },
            ["security"] = new JsonArray { new JsonObject { ["bearerAuth"] = new JsonArray() } },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["description"] = "LocalIdeBridge access key generated at first launch."
                    }
                },
                ["schemas"] = new JsonObject
                {
                    ["ToolResult"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["success"] = new JsonObject { ["type"] = "boolean" },
                            ["summary"] = new JsonObject { ["type"] = "string" },
                            ["data"] = new JsonObject { ["type"] = "object" },
                            ["errorCode"] = new JsonObject { ["type"] = "string" },
                            ["errorMessage"] = new JsonObject { ["type"] = "string" },
                            ["executionId"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("success", "summary")
                    }
                }
            }
        };
    }

    private static JsonObject SimpleGetOp(string opId, string summary) => new()
    {
        ["get"] = new JsonObject
        {
            ["operationId"] = opId,
            ["summary"] = summary,
            ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "OK" } }
        }
    };

    private static JsonObject SimplePostOp(string opId, string summary) => new()
    {
        ["operationId"] = opId,
        ["summary"] = summary,
        ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "OK" } }
    };

    private static async Task<(JsonObject Body, string? Error)> ReadJsonObjectBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var raw = await sr.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return (new JsonObject(), null);
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is not JsonObject obj)
                return (new JsonObject(), "Request body must be a JSON object.");
            return (obj, null);
        }
        catch (JsonException ex)
        {
            return (new JsonObject(), $"Invalid JSON: {ex.Message}");
        }
    }

    private static int MapErrorStatusCode(string? code) => code switch
    {
        "not_found" or "unknown_tool" => StatusCodes.Status404NotFound,
        "invalid_arguments" or "invalid_patch" or "invalid_regex" or "range_out_of_bounds" => StatusCodes.Status400BadRequest,
        "workspace_violation" or "authorization_required" or "host_paused" => StatusCodes.Status403Forbidden,
        "stale_file" or "conflict" or "precondition_failed" or "already_exists" or "already_reverted" => StatusCodes.Status409Conflict,
        "timeout" => StatusCodes.Status408RequestTimeout,
        _ => StatusCodes.Status422UnprocessableEntity
    };
}
