using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Cli;

/// <summary>
/// Lightweight CLI client for shell-capable AI agents and local scripts.
/// Exit codes: 0=success, 2=usage/argument error, 3=auth/forbidden, 4=tool/job failed, 5=network error, 6=timeout.
/// </summary>
public static class CliClient
{
    private sealed class CliProfile
    {
        public string? Endpoint { get; set; }
        public string? KeyEnvVar { get; set; } = "LOCALIDEBRIDGE_KEY";
        public bool AllowSelfSignedTls { get; set; }
        public int TimeoutSeconds { get; set; } = 180;
    }

    public static async Task<int> RunAsync(PortablePaths paths, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || SetupWizard.HasFlag(args, "--help") || SetupWizard.HasFlag(args, "-h") || args[0] == "help")
        {
            PrintUsage();
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        var jsonOut = SetupWizard.HasFlag(args, "--json");

        if (sub == "configure")
        {
            paths.EnsureDirectories();
            var profile = LoadProfile(paths);
            if (SetupWizard.GetOpt(rest, "--endpoint") is { } ep) profile.Endpoint = ep.TrimEnd('/');
            if (SetupWizard.GetOpt(rest, "--key-env") is { } ke) profile.KeyEnvVar = ke;
            if (int.TryParse(SetupWizard.GetOpt(rest, "--timeout"), out var to)) profile.TimeoutSeconds = Math.Clamp(to, 5, 3600);
            if (SetupWizard.HasFlag(rest, "--insecure-tls")) profile.AllowSelfSignedTls = true;
            PortablePaths.AtomicWriteAllText(paths.CliProfileFile, JsonSerializer.Serialize(profile, AppConfig.JsonOptions));
            Output(jsonOut, new { ok = true, profileFile = paths.CliProfileFile, profile }, $"Saved CLI profile to {paths.CliProfileFile}");
            return 0;
        }

        var (endpoint, key, allowInsecure, timeoutSec) = ResolveConnection(paths, rest);
        if (string.IsNullOrWhiteSpace(key))
        {
            WriteError(jsonOut, "missing_key",
                "No access key provided. Pass --key <KEY>, set LOCALIDEBRIDGE_KEY, or run from the LocalIdeBridge directory.");
            return 3;
        }

        using var handler = new HttpClientHandler();
        if (allowInsecure)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSec)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            return sub switch
            {
                "status" or "info" => await HandleGetAsync(http, "api/v1/info", jsonOut, ct),
                "tools" => await HandleToolsAsync(http, rest, jsonOut, ct),
                "schema" => await HandleSchemaAsync(http, rest, ct),
                "call" or "invoke" => await HandleCallAsync(http, rest, jsonOut, ct),
                "jobs" => await HandleJobsAsync(http, rest, jsonOut, ct),
                "job" => await HandleJobDetailAsync(http, rest, jsonOut, ct),
                "cancel" => await HandleCancelAsync(http, rest, jsonOut, ct),
                "logs" or "log" => await HandleLogsAsync(http, rest, jsonOut, ct),
                "workflow" => await HandleWorkflowAsync(http, rest, jsonOut, ct),
                "sessions" => await HandleSessionsAsync(http, rest, jsonOut, ct),
                "instructions" => await HandleInstructionsAsync(http, rest, jsonOut, ct),
                _ => UnknownCommand(sub)
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            WriteError(jsonOut, "timeout", $"Request to {endpoint} timed out after {timeoutSec}s.");
            return 6;
        }
        catch (HttpRequestException ex)
        {
            WriteError(jsonOut, "network_error", $"Cannot reach LocalIdeBridge at {endpoint}: {ex.Message}");
            return 5;
        }
    }

    private static async Task<int> HandleGetAsync(HttpClient http, string path, bool jsonOut, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = MapStatusToExitCode(resp.StatusCode, body);
        Console.WriteLine(body);
        return code;
    }

    private static async Task<int> HandleToolsAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var cat = SetupWizard.GetOpt(args, "--category");
        var url = string.IsNullOrWhiteSpace(cat) ? "api/v1/tools" : $"api/v1/tools?category={Uri.EscapeDataString(cat)}";
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(body);
            return MapStatusToExitCode(resp.StatusCode, body);
        }

        if (jsonOut)
        {
            Console.WriteLine(body);
            return 0;
        }

        var doc = JsonNode.Parse(body)?.AsObject();
        var tools = doc?["tools"]?.AsArray();
        if (tools is not null)
        {
            Console.WriteLine($"Available tools ({tools.Count}):");
            foreach (var t in tools)
            {
                var name = t?["name"]?.GetValue<string>() ?? "";
                var category = t?["category"]?.GetValue<string>() ?? "";
                var desc = t?["description"]?.GetValue<string>() ?? "";
                Console.WriteLine($"  {name,-24} [{category,-10}] {desc}");
            }
        }
        return 0;
    }

    private static async Task<int> HandleSchemaAsync(HttpClient http, IReadOnlyList<string> args, CancellationToken ct)
    {
        var toolName = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge cli schema <toolName>");
            return 2;
        }

        using var resp = await http.GetAsync("api/v1/tools", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(body);
            return MapStatusToExitCode(resp.StatusCode, body);
        }

        var tools = JsonNode.Parse(body)?["tools"]?.AsArray();
        var match = tools?.FirstOrDefault(t => string.Equals(t?["name"]?.GetValue<string>(), toolName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine($"Tool '{toolName}' not found.");
            return 4;
        }

        Console.WriteLine(match.ToJsonString(AppConfig.JsonOptions));
        return 0;
    }

    private static async Task<int> HandleCallAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var toolName = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge cli call <toolName> [--json-args '{...}'] [--arg key=value ...] [--async] [--json]");
            return 2;
        }

        JsonObject payload;
        var rawJsonArgs = SetupWizard.GetOpt(args, "--json-args");
        if (!string.IsNullOrWhiteSpace(rawJsonArgs))
        {
            try
            {
                payload = JsonNode.Parse(rawJsonArgs)?.AsObject()
                    ?? throw new ArgumentException("--json-args must be a JSON object.");
            }
            catch (Exception ex)
            {
                WriteError(jsonOut, "invalid_arguments", $"Invalid --json-args: {ex.Message}");
                return 2;
            }
        }
        else
        {
            payload = ParseKeyValueArgs(args);
        }

        var isAsync = SetupWizard.HasFlag(args, "--async");
        var url = $"api/v1/tools/{Uri.EscapeDataString(toolName)}/invoke{(isAsync ? "?async=true" : "")}";
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        Console.WriteLine(body);
        return MapStatusToExitCode(resp.StatusCode, body);
    }

    private static async Task<int> HandleJobsAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var qs = new List<string>();
        if (SetupWizard.GetOpt(args, "--status") is { } st) qs.Add($"status={Uri.EscapeDataString(st)}");
        if (SetupWizard.GetOpt(args, "--limit") is { } lim) qs.Add($"limit={Uri.EscapeDataString(lim)}");
        var url = "api/v1/jobs" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await HandleGetAsync(http, url, jsonOut, ct);
    }

    private static async Task<int> HandleJobDetailAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var jobId = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge cli job <jobId> [--events] [--after <eventId>]");
            return 2;
        }
        if (SetupWizard.HasFlag(args, "--events"))
        {
            var after = SetupWizard.GetOpt(args, "--after") ?? "0";
            return await HandleGetAsync(http, $"api/v1/jobs/{Uri.EscapeDataString(jobId)}/events?afterEventId={Uri.EscapeDataString(after)}", jsonOut, ct);
        }
        return await HandleGetAsync(http, $"api/v1/jobs/{Uri.EscapeDataString(jobId)}", jsonOut, ct);
    }

    private static async Task<int> HandleCancelAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var jobId = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge cli cancel <jobId>");
            return 2;
        }
        using var resp = await http.PostAsync($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/cancel", new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Console.WriteLine(body);
        return MapStatusToExitCode(resp.StatusCode, body);
    }

    private static async Task<int> HandleLogsAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var execId = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(execId))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge cli logs <execId> [--start 1] [--lines 120] [--contains text]");
            return 2;
        }
        var start = SetupWizard.GetOpt(args, "--start") ?? "1";
        var lines = SetupWizard.GetOpt(args, "--lines") ?? "120";
        var contains = SetupWizard.GetOpt(args, "--contains");
        var url = $"api/v1/logs/{Uri.EscapeDataString(execId)}?startLine={Uri.EscapeDataString(start)}&maxLines={Uri.EscapeDataString(lines)}";
        if (!string.IsNullOrWhiteSpace(contains)) url += $"&contains={Uri.EscapeDataString(contains)}";
        return await HandleGetAsync(http, url, jsonOut, ct);
    }

    private static async Task<int> HandleWorkflowAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var file = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            WriteError(jsonOut, "invalid_arguments", "Usage: LocalIdeBridge cli workflow <workflow.json> [--async]");
            return 2;
        }
        var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))?.AsObject()
            ?? throw new ArgumentException("Workflow file must contain a JSON object.");
        if (SetupWizard.HasFlag(args, "--async"))
            node["async"] = true;

        using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("api/v1/workflows", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Console.WriteLine(body);
        return MapStatusToExitCode(resp.StatusCode, body);
    }

    private static async Task<int> HandleSessionsAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var lim = SetupWizard.GetOpt(args, "--limit") ?? "20";
        return await HandleGetAsync(http, $"api/v1/sessions?limit={Uri.EscapeDataString(lim)}", jsonOut, ct);
    }

    private static async Task<int> HandleInstructionsAsync(HttpClient http, IReadOnlyList<string> args, bool jsonOut, CancellationToken ct)
    {
        var client = SetupWizard.GetOpt(args, "--client");
        var url = string.IsNullOrWhiteSpace(client) ? "api/v1/instructions" : $"api/v1/instructions?client={Uri.EscapeDataString(client)}";
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode || jsonOut)
        {
            Console.WriteLine(body);
            return MapStatusToExitCode(resp.StatusCode, body);
        }
        var parsed = JsonNode.Parse(body)?.AsObject();
        Console.WriteLine(parsed?["clientTemplate"]?.GetValue<string>() ?? parsed?["instructions"]?.GetValue<string>() ?? body);
        return 0;
    }

    private static (string Endpoint, string? Key, bool AllowInsecure, int TimeoutSec) ResolveConnection(PortablePaths paths, IReadOnlyList<string> args)
    {
        var profile = LoadProfile(paths);
        var cfg = AppConfig.Load(paths);

        // Endpoint resolution precedence: --endpoint -> env -> cli-profile.json -> host.pid -> config.json.
        var endpoint = SetupWizard.GetOpt(args, "--endpoint")
            ?? Environment.GetEnvironmentVariable("LOCALIDEBRIDGE_ENDPOINT")
            ?? profile.Endpoint;

        if (string.IsNullOrWhiteSpace(endpoint) && File.Exists(paths.HostLockFile))
        {
            var parts = File.ReadAllText(paths.HostLockFile).Trim().Split('|');
            if (parts.Length >= 2 && parts[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                endpoint = parts[1];
        }
        endpoint ??= cfg.GetEffectiveBaseUrl();

        // Key precedence: --key -> --key-env / configured env -> LOCALIDEBRIDGE_KEY -> local data/secret.key (same-host convenience).
        var keyEnv = SetupWizard.GetOpt(args, "--key-env") ?? profile.KeyEnvVar ?? "LOCALIDEBRIDGE_KEY";
        var key = SetupWizard.GetOpt(args, "--key")
            ?? Environment.GetEnvironmentVariable(keyEnv)
            ?? Environment.GetEnvironmentVariable("LOCALIDEBRIDGE_KEY");

        if (string.IsNullOrWhiteSpace(key) && File.Exists(paths.SecretKeyFile))
        {
            try { key = File.ReadAllText(paths.SecretKeyFile).Trim(); } catch { }
        }

        var insecure = SetupWizard.HasFlag(args, "--insecure-tls") || profile.AllowSelfSignedTls;
        var timeout = int.TryParse(SetupWizard.GetOpt(args, "--timeout"), out var t) ? Math.Clamp(t, 2, 3600) : profile.TimeoutSeconds;
        return (endpoint, key, insecure, timeout);
    }

    private static JsonObject ParseKeyValueArgs(IReadOnlyList<string> args)
    {
        var obj = new JsonObject();
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--arg", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Count)
                continue;
            var pair = args[++i];
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var k = pair[..eq];
            var v = pair[(eq + 1)..];
            if (bool.TryParse(v, out var b)) obj[k] = b;
            else if (int.TryParse(v, out var n)) obj[k] = n;
            else if ((v.StartsWith('[') && v.EndsWith(']')) || (v.StartsWith('{') && v.EndsWith('}')))
            {
                try { obj[k] = JsonNode.Parse(v); } catch { obj[k] = v; }
            }
            else obj[k] = v;
        }
        return obj;
    }

    private static string? FirstPositional(IReadOnlyList<string> args)
    {
        var flagsWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--endpoint", "--key", "--key-env", "--timeout", "--json-args", "--arg",
            "--category", "--status", "--limit", "--after", "--start", "--lines", "--contains", "--client", "--home"
        };
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (flagsWithValue.Contains(a)) { i++; continue; }
            if (a.StartsWith('-')) continue;
            return a;
        }
        return null;
    }

    private static int MapStatusToExitCode(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Distinguish host policy denial (tool ran and returned success=false) vs bad auth key.
            if (status == HttpStatusCode.Forbidden && body.Contains("\"workspace_violation\"", StringComparison.Ordinal))
                return 4;
            return 3;
        }
        if (status == HttpStatusCode.RequestTimeout) return 6;
        if (status == HttpStatusCode.BadRequest) return 2;
        if (!((int)status is >= 200 and < 300)) return 4;

        try
        {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is not null && node.TryGetPropertyValue("success", out var s) && s?.GetValue<bool>() == false)
                return 4;
        }
        catch { }
        return 0;
    }

    private static CliProfile LoadProfile(PortablePaths paths)
    {
        if (!File.Exists(paths.CliProfileFile)) return new CliProfile();
        try { return JsonSerializer.Deserialize<CliProfile>(File.ReadAllText(paths.CliProfileFile), AppConfig.JsonOptions) ?? new CliProfile(); }
        catch { return new CliProfile(); }
    }

    private static void Output(bool json, object payload, string text)
        => Console.WriteLine(json ? JsonSerializer.Serialize(payload, AppConfig.JsonOptions) : text);

    private static void WriteError(bool json, string code, string message)
    {
        if (json)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code, message } }));
        else
            Console.Error.WriteLine($"Error [{code}]: {message}");
    }

    private static int UnknownCommand(string sub)
    {
        Console.Error.WriteLine($"Unknown cli command '{sub}'. Run 'LocalIdeBridge cli --help' for usage.");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            LocalIdeBridge CLI Client — Usage:
              LocalIdeBridge cli configure --endpoint <url> [--key-env LOCALIDEBRIDGE_KEY] [--insecure-tls]
              LocalIdeBridge cli status [--json]
              LocalIdeBridge cli tools [--category <name>] [--json]
              LocalIdeBridge cli schema <toolName>
              LocalIdeBridge cli call <toolName> [--arg k=v ...] [--json-args '{...}'] [--async] [--json]
              LocalIdeBridge cli jobs [--status running|succeeded|failed] [--limit 25] [--json]
              LocalIdeBridge cli job <jobId> [--events] [--after 0] [--json]
              LocalIdeBridge cli cancel <jobId> [--json]
              LocalIdeBridge cli logs <execId> [--start 1] [--lines 120] [--contains text] [--json]
              LocalIdeBridge cli workflow <workflow.json> [--async] [--json]
              LocalIdeBridge cli sessions [--limit 20] [--json]
              LocalIdeBridge cli instructions [--client codex|claude-code|claude-desktop|gemini-cli|http|cli]

            Global flags: --endpoint <url>  --key <key>  --key-env <VAR>  --timeout <sec>  --insecure-tls  --json
            Exit codes  : 0=ok  2=usage/args  3=auth  4=tool/job error  5=network  6=timeout
            """);
    }
}
