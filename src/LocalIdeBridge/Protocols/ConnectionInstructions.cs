using System.Diagnostics;
using System.Text;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Tools;

namespace LocalIdeBridge.Protocols;

/// <summary>
/// Generates concise AI-readable connection instructions (excluding secrets by default),
/// verified client configuration templates, and Windows/cross-platform clipboard operations.
/// </summary>
public sealed class ConnectionInstructions
{
    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly ToolRegistry _registry;
    private readonly TunnelManager _tunnel;

    public ConnectionInstructions(
        AppConfig config,
        WorkspaceGuard guard,
        ToolRegistry registry,
        TunnelManager tunnel)
    {
        _config = config;
        _guard = guard;
        _registry = registry;
        _tunnel = tunnel;
    }

    public string GetEffectiveBaseUrl()
        => _tunnel.ActiveTunnelEndpoint ?? _config.GetEffectiveBaseUrl();

    public string GetMcpEndpoint()
        => $"{GetEffectiveBaseUrl()}/mcp";

    /// <summary>
    /// Builds concise AI-readable connection instructions. Never embeds the secret credential.
    /// </summary>
    public string BuildAgentInstructions()
    {
        var baseUrl = GetEffectiveBaseUrl();
        var mcpUrl = $"{baseUrl}/mcp";
        var ws = _guard.ActiveWorkspace ?? "(select via workspace_select)";
        var categories = _registry.All
            .GroupBy(t => t.Category)
            .Select(g => $"- **{g.Key}** ({g.Count()}): {string.Join(", ", g.Select(t => $"`{t.Name}`"))}");

        var sb = new StringBuilder();
        sb.AppendLine("# LocalIdeBridge — AI Agent Connection Instructions");
        sb.AppendLine();
        sb.AppendLine("## Purpose");
        sb.AppendLine("LocalIdeBridge exposes authenticated software-engineering tools inside a trusted workspace on the user's Windows PC (file search, Roslyn C# semantic intelligence, atomic editing/unified-diff patching, dotnet/MSBuild builds, TRX test execution, local Git operations, async jobs, and DAG workflows).");
        sb.AppendLine();
        sb.AppendLine("## Endpoints & Protocols");
        sb.AppendLine($"- **Primary (MCP Streamable HTTP):** `{mcpUrl}`");
        sb.AppendLine($"- **Secondary (JSON HTTP/HTTPS API):** `{baseUrl}/api/v1`");
        sb.AppendLine($"- **OpenAPI 3.1 Schema:** `{baseUrl}/openapi.json`");
        sb.AppendLine($"- **CLI Client (shell-capable agents):** `LocalIdeBridge cli --endpoint {baseUrl} <command>`");
        sb.AppendLine($"- **Active Workspace:** `{ws}`");
        sb.AppendLine();
        sb.AppendLine("## Authentication");
        sb.AppendLine("- Send header `Authorization: Bearer <LOCALIDEBRIDGE_KEY>` (or `X-LocalIdeBridge-Key: <LOCALIDEBRIDGE_KEY>`) on every request.");
        sb.AppendLine("- The secret key is intentionally omitted from these instructions; obtain it via **Copy Key** and store it in your client's secret/env configuration.");
        sb.AppendLine();
        sb.AppendLine("## Tool Discovery & Quick Verification");
        sb.AppendLine("1. **Discover tools:** MCP `tools/list`; use `tool_discover` for specialist schemas, `tool_read` for specialist reads, and `execute_once` for mutations. HTTP and CLI can list the full catalog.");
        sb.AppendLine("2. **Orient once:** Prefer `project_context`; use focused search and `read_context` to batch related source. Reuse known hashes and read only changed or missing ranges.");
        sb.AppendLine("3. **Work in batches:** Use `workflow_run` with selected `returnPointers` for independent reads and dependent actions. Guard edits with versions. Verify proportionately to risk; do not run the full suite after every small edit.");
        sb.AppendLine();
        sb.AppendLine("## Asynchronous Jobs & Logs");
        sb.AppendLine("- For long builds, test suites, or workflows, call `job_start` with `{ \"tool\": \"dotnet_test\", \"arguments\": {} }`.");
        sb.AppendLine("- Wait with `job_events` (`afterEventId`, `waitMs:25000`, `waitForTerminal:true`, `includeResult:true`); avoid repeated status polls. Cancel with `job_cancel`.");
        sb.AppendLine("- Command outputs return concise excerpts plus `executionId`; call `read_log` with `{ \"execId\": \"...\", \"startLine\": 1, \"maxLines\": 150 }` for full paginated output.");
        sb.AppendLine();
        sb.AppendLine($"## Available Tools ({_registry.All.Count})");
        foreach (var line in categories)
            sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("## Important Limitations");
        sb.AppendLine("- Pasting these instructions alone does **not** grant tool access to a chat website; your AI client must natively support custom MCP servers, authenticated HTTP calls, or shell access with a network route to the endpoint.");
        sb.AppendLine("- File operations are strictly confined to trusted workspaces (traversal, junctions, and external symlinks are rejected).");
        sb.AppendLine("- Local Git commits work unattended; external `git_push` is denied unless explicitly enabled by the local user.");
        sb.AppendLine("- The listener runs only while LocalIdeBridge is open; closing it stops the server and terminates all owned child processes.");
        return sb.ToString().TrimEnd();
    }

    public string BuildClientTemplate(string clientName)
        => BuildClientTemplateOffline(clientName, GetEffectiveBaseUrl(), Environment.ProcessPath ?? @"C:\LocalIdeBridge\LocalIdeBridge.exe");

    // Safe to call from the CLI while a separate host owns the portable state.
    public static string BuildClientTemplateOffline(string clientName, string baseUrl, string exePath)
    {
        var mcpUrl = $"{baseUrl}/mcp";
        return clientName.Trim().ToLowerInvariant() switch
        {
            "codex" => $"""
                # OpenAI Codex CLI (~/.codex/config.toml)
                # First set environment variable: $env:LOCALIDEBRIDGE_KEY = "<paste-key-from-Copy-Key>"
                [mcp_servers.localidebridge]
                url = "{mcpUrl}"
                bearer_token_env_var = "LOCALIDEBRIDGE_KEY"
                """,

            "claude-code" => $"""
                # Claude Code (PowerShell)
                # Replace $env:LOCALIDEBRIDGE_KEY in your session first via Copy Key:
                claude mcp add --transport http localidebridge "{mcpUrl}" --header "Authorization: Bearer $env:LOCALIDEBRIDGE_KEY"
                """,

            "claude-desktop" => $$"""
                // Claude Desktop local stdio config (%APPDATA%\Claude\claude_desktop_config.json)
                // Runs LocalIdeBridge directly over stdio with no public network exposure:
                {
                  "mcpServers": {
                    "localidebridge": {
                      "command": "{{exePath.Replace("\\", "\\\\")}}",
                      "args": ["mcp-stdio", "--home", "{{AppContext.BaseDirectory.TrimEnd('\\', '/').Replace("\\", "\\\\")}}"]
                    }
                  }
                }
                """,

            "gemini-cli" => $$"""
                // Gemini CLI (~/.gemini/settings.json)
                // Set environment variable LOCALIDEBRIDGE_KEY before launching gemini:
                {
                  "mcpServers": {
                    "localidebridge": {
                      "type": "http",
                      "httpUrl": "{{mcpUrl}}",
                      "headers": {
                        "Authorization": "Bearer $LOCALIDEBRIDGE_KEY"
                      }
                    }
                  }
                }
                """,

            "http" or "curl" => $$"""
                # Authenticated HTTP API example (PowerShell)
                $headers = @{ Authorization = "Bearer $env:LOCALIDEBRIDGE_KEY" }
                Invoke-RestMethod -Uri "{{baseUrl}}/api/v1/tools/workspace_status/invoke" -Method Post -Headers $headers -ContentType "application/json" -Body "{}"
                """,

            "cli" => $"""
                # LocalIdeBridge CLI client (for shell-capable agents)
                LocalIdeBridge cli configure --endpoint "{baseUrl}" --key-env LOCALIDEBRIDGE_KEY
                LocalIdeBridge cli tools --json
                LocalIdeBridge cli call workspace_status --json
                """,

            _ => $"Supported template names: codex, claude-code, claude-desktop, gemini-cli, http, cli."
        };
    }

    public static (bool Copied, string Message) TryCopyToClipboard(string text)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "clip.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    StandardInputEncoding = Encoding.Unicode
                });
                if (proc is not null)
                {
                    proc.StandardInput.Write(text);
                    proc.StandardInput.Close();
                    if (proc.WaitForExit(3000) && proc.ExitCode == 0)
                        return (true, "Copied to Windows clipboard.");
                }
            }
            else
            {
                foreach (var (cmd, args) in new[] { ("wl-copy", Array.Empty<string>()), ("xclip", new[] { "-selection", "clipboard" }) })
                {
                    var found = ProcessRunner.FindOnPath(cmd);
                    if (found is null) continue;
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = found,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true
                    });
                    if (proc is null) continue;
                    foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
                    proc.StandardInput.Write(text);
                    proc.StandardInput.Close();
                    if (proc.WaitForExit(2000) && proc.ExitCode == 0)
                        return (true, $"Copied to clipboard via {cmd}.");
                }
            }
        }
        catch { }

        return (false, "Clipboard utility unavailable in this session.");
    }
}
