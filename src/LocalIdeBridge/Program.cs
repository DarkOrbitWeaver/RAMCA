using System.Text.Json;
using LocalIdeBridge.Cli;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Protocols;

namespace LocalIdeBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch { }
        };

        var explicitHome = SetupWizard.GetOpt(args, "--home");
        var paths = new PortablePaths(explicitHome);

        // Strip --home <dir> when evaluating positional command token.
        var filtered = StripHomeFlag(args);
        var cmd = filtered.FirstOrDefault()?.ToLowerInvariant();

        if (cmd is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (cmd is "--version" or "-v" or "version")
        {
            Console.WriteLine("LocalIdeBridge 1.0.0 (.NET 10)");
            return 0;
        }

        try
        {
            switch (cmd)
            {
                case "cli":
                    return await CliClient.RunAsync(paths, filtered.Skip(1).ToList(), cts.Token);

                case "setup":
                    return filtered.Count == 1 && !Console.IsInputRedirected
                        ? SetupWizard.RunInteractive(paths)
                        : SetupWizard.RunScripted(paths, filtered.Skip(1).ToList());

                case "mcp-stdio":
                    return await RunMcpStdioModeAsync(paths, cts.Token);

                case "host" or "serve":
                    return await RunServerModeAsync(paths, filtered.Skip(1).ToList(), headless: true, cts.Token);

                case "key":
                    return HandleKeyCommand(paths, filtered.Skip(1).ToList());

                case "endpoint":
                    return HandleEndpointCommand(paths, filtered.Skip(1).ToList());

                case "instructions":
                    return await HandleInstructionsCommandAsync(paths, filtered.Skip(1).ToList());

                case "workspace":
                    return HandleWorkspaceCommand(paths, filtered.Skip(1).ToList());

                case "config":
                    return HandleConfigCommand(paths, filtered.Skip(1).ToList());

                case "doctor":
                    return HandleDoctorCommand(paths, filtered.Skip(1).ToList());

                case "benchmark":
                    return await BenchmarkRunner.RunAsync(filtered.Skip(1).ToList(), cts.Token);

                case "gendocs":
                {
                    var outPath = SetupWizard.GetOpt(filtered, "--output") ?? Path.Combine(paths.DocsDir, "TOOLS.md");
                    return DocGenerator.GenerateToolsMarkdown(outPath);
                }

                case null or "tui" or "start":
                    return await RunServerModeAsync(paths, filtered, headless: SetupWizard.HasFlag(filtered, "--headless"), cts.Token);

                default:
                    Console.Error.WriteLine($"Unknown command '{cmd}'. Run 'LocalIdeBridge --help' for usage.");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunServerModeAsync(
        PortablePaths paths,
        IReadOnlyList<string> args,
        bool headless,
        CancellationToken ct)
    {
        paths.EnsureDirectories();
        var config = AppConfig.Load(paths);

        // Run compact first-launch setup wizard automatically on interactive launch if unconfigured.
        if (!config.SetupCompleted && !File.Exists(paths.ConfigFile))
        {
            if (!headless && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                SetupWizard.RunInteractive(paths);
                config = AppConfig.Load(paths);
            }
            else
            {
                var wsOpt = SetupWizard.GetOpt(args, "--workspace") ?? Path.Combine(paths.SandboxDir, "default-workspace");
                Directory.CreateDirectory(wsOpt);
                config.AddTrustedWorkspace(wsOpt, setAsDefault: true);
                config.SetupCompleted = true;
                config.Save(paths);
            }
        }

        // Allow one-off CLI overrides without permanently rewriting config.json.
        if (int.TryParse(SetupWizard.GetOpt(args, "--port"), out var portOverride))
            config.Connection.Port = portOverride;
        if (SetupWizard.GetOpt(args, "--workspace") is { } wsOverride)
        {
            var full = Path.GetFullPath(wsOverride);
            if (Directory.Exists(full))
            {
                using var guard = new WorkspaceGuard(paths, config);
                guard.ValidateTrustableDirectory(full);
                config.AddTrustedWorkspace(full, setAsDefault: true);
            }
        }

        await using var host = new WorkbenchHost(paths, config);

        // Belt-and-suspenders shutdown hook in case the terminal window is closed abruptly.
        void OnProcessExit(object? _, EventArgs __)
        {
            try { host.Tunnel.Stop(); } catch { }
            try { host.ProcessSupervisor.TerminateAll(); } catch { }
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        try
        {
            await host.StartNetworkListenerAsync(ct);
            var tui = new SupervisorTui(host);
            if (headless)
                await tui.RunHeadlessAsync(ct);
            else
                await tui.RunAsync(ct);
            return 0;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    private static async Task<int> RunMcpStdioModeAsync(PortablePaths paths, CancellationToken ct)
    {
        paths.EnsureDirectories();
        var config = AppConfig.Load(paths);
        if (config.TrustedWorkspaces.Count == 0)
        {
            var def = Path.Combine(paths.SandboxDir, "default-workspace");
            Directory.CreateDirectory(def);
            config.AddTrustedWorkspace(def, setAsDefault: true);
            config.SetupCompleted = true;
            config.Save(paths);
        }

        await using var host = new WorkbenchHost(paths, config);
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        await host.Mcp.RunStdioAsync(stdin, stdout, ct);
        return 0;
    }

    private static int HandleKeyCommand(PortablePaths paths, IReadOnlyList<string> args)
    {
        paths.EnsureDirectories();
        var store = new CredentialStore(paths);
        store.EnsureInitialized();
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";

        switch (sub)
        {
            case "status" or "masked":
                Console.WriteLine($"Key: {store.GetMasked()} (rotated {store.LastRotatedUtc:u})");
                return 0;
            case "copy":
            {
                var (ok, msg) = ConnectionInstructions.TryCopyToClipboard(store.GetSecretForExplicitCopy());
                Console.WriteLine(ok ? $"Copied access key ({store.GetMasked()}) to clipboard." : $"{msg} Key file: {paths.SecretKeyFile}");
                return ok ? 0 : 1;
            }
            case "print":
                Console.WriteLine(store.GetSecretForExplicitCopy());
                return 0;
            case "rotate" or "regenerate":
            {
                store.Rotate();
                Console.WriteLine($"Regenerated access key: {store.GetMasked()}");
                if (SetupWizard.HasFlag(args, "--copy"))
                {
                    var (ok, msg) = ConnectionInstructions.TryCopyToClipboard(store.GetSecretForExplicitCopy());
                    Console.WriteLine(msg);
                    if (!ok) return 1;
                }
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: LocalIdeBridge key [status | copy | print | rotate [--copy]]");
                return 2;
        }
    }

    private static int HandleEndpointCommand(PortablePaths paths, IReadOnlyList<string> args)
    {
        var cfg = AppConfig.Load(paths);
        var mcp = $"{cfg.GetEffectiveBaseUrl()}/mcp";
        if (SetupWizard.HasFlag(args, "--copy"))
        {
            var (_, msg) = ConnectionInstructions.TryCopyToClipboard(mcp);
            Console.WriteLine($"{mcp} ({msg})");
        }
        else
        {
            Console.WriteLine(mcp);
        }
        return 0;
    }

    private static Task<int> HandleInstructionsCommandAsync(PortablePaths paths, IReadOnlyList<string> args)
    {
        // Instructions must never create a WorkbenchHost: a second host would recover/close
        // the first host's active jobs, clear its temporary files, and delete its PID file.
        var config = AppConfig.Load(paths);
        var baseUrl = config.GetEffectiveBaseUrl();
        var client = SetupWizard.GetOpt(args, "--client");
        var text = string.IsNullOrWhiteSpace(client)
            ? $"""
              # RAMCA — AI Client Connection Instructions

              MCP Streamable HTTP: {baseUrl}/mcp
              HTTP tools: {baseUrl}/api/v1
              Tools are discoverable through the live MCP tools/list method.
              See docs/CONNECTING_AI_CLIENTS.md and docs/TOOLS.md for details.
              Authentication: Authorization: Bearer <LOCALIDEBRIDGE_KEY>.
              Copy the key using `LocalIdeBridge key copy` (never paste it into untrusted prompts).
              Connecting an AI website still requires actual MCP integration/tool permission from that client.
              The URL is a configured default and may differ from the running host/tunnel endpoint.
              """
            : ConnectionInstructions.BuildClientTemplateOffline(
                client, baseUrl, Environment.ProcessPath ?? Path.Combine(paths.RootDir, "LocalIdeBridge.exe"));

        if (SetupWizard.HasFlag(args, "--copy"))
        {
            var (_, msg) = ConnectionInstructions.TryCopyToClipboard(text);
            Console.Error.WriteLine(msg);
        }
        Console.WriteLine(text);
        return Task.FromResult(0);
    }

    private static int HandleWorkspaceCommand(PortablePaths paths, IReadOnlyList<string> args)
    {
        paths.EnsureDirectories();
        var cfg = AppConfig.Load(paths);
        var guard = new WorkspaceGuard(paths, cfg);
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        switch (sub)
        {
            case "list":
                if (cfg.TrustedWorkspaces.Count == 0) { Console.WriteLine("No trusted workspaces configured."); return 0; }
                foreach (var w in cfg.TrustedWorkspaces)
                {
                    var mark = string.Equals(w.Path, cfg.DefaultWorkspace, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                    Console.WriteLine($"{mark} {w.Name,-20} {w.Path}");
                }
                return 0;

            case "add":
            {
                if (args.Count < 2) { Console.Error.WriteLine("Usage: LocalIdeBridge workspace add <path> [--alias name] [--default]"); return 2; }
                var full = Path.GetFullPath(args[1]);
                guard.ValidateTrustableDirectory(full);
                if (!Directory.Exists(full)) Directory.CreateDirectory(full);
                cfg.AddTrustedWorkspace(full, SetupWizard.GetOpt(args, "--alias"), SetupWizard.HasFlag(args, "--default") || cfg.TrustedWorkspaces.Count == 0);
                cfg.SetupCompleted = true;
                cfg.Save(paths);
                Console.WriteLine($"Added trusted workspace: {full}");
                return 0;
            }

            case "select" or "default":
            {
                if (args.Count < 2) { Console.Error.WriteLine("Usage: LocalIdeBridge workspace select <pathOrAlias>"); return 2; }
                var selected = guard.SelectWorkspace(args[1]);
                cfg.DefaultWorkspace = selected;
                cfg.Save(paths);
                Console.WriteLine($"Default workspace set to: {selected}");
                return 0;
            }

            case "remove":
            {
                if (args.Count < 2) { Console.Error.WriteLine("Usage: LocalIdeBridge workspace remove <pathOrAlias>"); return 2; }
                var target = args[1];
                var removed = cfg.TrustedWorkspaces.RemoveAll(w =>
                    string.Equals(w.Name, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(w.Path, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
                if (removed > 0 && cfg.DefaultWorkspace is not null && !cfg.TrustedWorkspaces.Any(w => w.Path == cfg.DefaultWorkspace))
                    cfg.DefaultWorkspace = cfg.TrustedWorkspaces.FirstOrDefault()?.Path;
                cfg.Save(paths);
                Console.WriteLine($"Removed {removed} workspace entry(ies).");
                return 0;
            }

            default:
                Console.Error.WriteLine("Usage: LocalIdeBridge workspace [list | add <path> | select <name> | remove <name>]");
                return 2;
        }
    }

    private static int HandleConfigCommand(PortablePaths paths, IReadOnlyList<string> args)
    {
        paths.EnsureDirectories();
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "show";
        switch (sub)
        {
            case "show":
                Console.WriteLine(JsonSerializer.Serialize(AppConfig.Load(paths), AppConfig.JsonOptions));
                return 0;
            case "export":
                // Strictly excludes secrets and machine-specific certificate paths.
                Console.Write(AppConfig.Load(paths).ExportShareableJson());
                return 0;
            case "reset":
            {
                var fresh = new AppConfig();
                fresh.Save(paths);
                if (SetupWizard.HasFlag(args, "--rotate-key"))
                {
                    var cs = new CredentialStore(paths);
                    cs.EnsureInitialized();
                    cs.Rotate();
                }
                Console.WriteLine($"Reset configuration at {paths.ConfigFile}.");
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: LocalIdeBridge config [show | export | reset [--rotate-key]]");
                return 2;
        }
    }

    private static int HandleDoctorCommand(PortablePaths paths, IReadOnlyList<string> args)
    {
        paths.EnsureDirectories();
        var cfg = AppConfig.Load(paths);
        var tools = ProcessRunner.ProbeInstalledTools(paths);
        var net = TunnelManager.DiagnoseNetwork(cfg, checkPortBind: true);

        if (SetupWizard.HasFlag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { portableRoot = paths.RootDir, tools, network = net }, AppConfig.JsonOptions));
            return 0;
        }

        Console.WriteLine($"LocalIdeBridge Doctor — {paths.RootDir}");
        Console.WriteLine("Tools:");
        SetupWizard.PrintToolDoctorSummary(paths);
        Console.WriteLine($"\nNetwork (mode={net.ConfiguredMode}, listen={net.ListenHost}:{net.Port}, portFree={net.PortAvailable}, tlsReady={net.TlsReady}):");
        Console.WriteLine($"  Private NAT IPv4: {net.HasPrivateNatIpv4} | CGNAT (100.64/10): {net.HasCgnatIpv4} | Public IPv4: {net.HasPublicIpv4Candidate} | Global IPv6: {net.HasGlobalIpv6Candidate}");
        foreach (var adv in net.Advisories)
            Console.WriteLine($"  * {adv}");
        return 0;
    }

    private static List<string> StripHomeFlag(string[] args)
    {
        var list = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--home", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (args[i].StartsWith("--home=", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(args[i]);
        }
        return list;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            LocalIdeBridge 1.0.0 — Portable Coding-Agent Workbench (.NET 10)

            Supervisor & Host:
              LocalIdeBridge                          Launch interactive supervisor TUI (runs setup wizard on first run)
              LocalIdeBridge host [--port N]          Run headless local/remote host until Ctrl+C or termination
              LocalIdeBridge mcp-stdio                Run MCP server over stdin/stdout (no network listener)
              LocalIdeBridge setup [options]          Run interactive or scripted setup (--workspace, --mode, --port, --tunnel)

            Connection & Credentials:
              LocalIdeBridge instructions [--copy] [--client codex|claude-code|claude-desktop|gemini-cli|http|cli]
              LocalIdeBridge endpoint [--copy]        Display or copy the active MCP endpoint URL
              LocalIdeBridge key [status|copy|rotate] View masked key, copy secret key, or regenerate key
              LocalIdeBridge workspace [list|add|select|remove]
              LocalIdeBridge config [show|export|reset]
              LocalIdeBridge doctor [--json]          Verify installed tools and diagnose NAT/CGNAT/TLS/port readiness

            Agent CLI Client:
              LocalIdeBridge cli <command> [options]  Discover tools, invoke operations, inspect jobs/logs (see 'cli --help')

            Verification:
              LocalIdeBridge benchmark [--output f]   Run 100 / 1,000 / 10,000 op resource & latency benchmarks
              LocalIdeBridge gendocs [--output f]     Generate authoritative TOOLS.md from ToolRegistry contracts

            Global option: --home <dir>               Override portable directory (defaults to executable folder)
            """);
    }
}
