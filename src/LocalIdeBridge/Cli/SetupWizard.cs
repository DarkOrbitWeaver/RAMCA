using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;

namespace LocalIdeBridge.Cli;

/// <summary>
/// Compact first-run terminal setup wizard and scriptable command-line setup handler.
/// </summary>
public static class SetupWizard
{
    public static int RunInteractive(PortablePaths paths)
    {
        paths.EnsureDirectories();
        var config = AppConfig.Load(paths);
        var credentials = new CredentialStore(paths);
        var createdKey = credentials.EnsureInitialized();

        Console.WriteLine();
        Console.WriteLine("LocalIdeBridge — First-Run Setup");
        Console.WriteLine(new string('─', 52));
        Console.WriteLine($"Portable directory: {paths.RootDir}");
        Console.WriteLine("All state, logs, and credentials stay inside this folder.");
        Console.WriteLine();

        // Step 1: Select or create trusted workspace.
        var defaultWs = config.DefaultWorkspace ?? Path.Combine(paths.SandboxDir, "starter-project");
        string chosenWs;
        while (true)
        {
            Console.Write($"1. Trusted workspace folder [{defaultWs}]: ");
            var input = Console.ReadLine()?.Trim().Trim('"');
            var candidate = string.IsNullOrWhiteSpace(input) ? defaultWs : Path.GetFullPath(input);

            try
            {
                var probeGuard = new WorkspaceGuard(paths, config);
                probeGuard.ValidateTrustableDirectory(candidate);

                if (!Directory.Exists(candidate))
                {
                    Console.Write($"   Folder does not exist. Create '{candidate}'? [Y/n]: ");
                    var yn = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(yn) && yn.StartsWith("n", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(candidate);
                }

                chosenWs = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Invalid workspace: {ex.Message}");
            }
        }

        config.AddTrustedWorkspace(chosenWs, setAsDefault: true);

        // Step 2: Verify installed development tools.
        Console.WriteLine();
        Console.WriteLine("2. Installed development tools:");
        PrintToolDoctorSummary(paths);

        // Step 3: Choose connection mode.
        Console.WriteLine();
        Console.WriteLine("3. Connection method:");
        Console.WriteLine("   [1] Localhost only (127.0.0.1 — recommended for local CLI/desktop agents)");
        Console.WriteLine("   [2] Direct remote HTTPS (for PCs with a reachable IP and TLS certificate)");
        Console.WriteLine("   [3] Lightweight tunnel (owned cloudflared/ssh child process for NAT/CGNAT)");
        Console.Write("   Select [1]: ");
        var modeChoice = Console.ReadLine()?.Trim();

        switch (modeChoice)
        {
            case "2":
                config.Connection.Mode = "direct_https";
                config.Connection.ListenHost = "0.0.0.0";
                config.Connection.UseHttps = true;
                Console.Write("   PFX certificate path (leave blank to generate self-signed cert in data/): ");
                var pfx = Console.ReadLine()?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(pfx))
                {
                    config.Connection.TlsCertPath = pfx;
                    config.Connection.AutoGenerateSelfSignedTls = false;
                }
                else
                {
                    config.Connection.AutoGenerateSelfSignedTls = true;
                }
                Console.Write("   Optional public HTTPS URL (e.g. https://dev.example.com:43170): ");
                var pub = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(pub)) config.Connection.PublicEndpoint = pub;
                break;

            case "3":
                config.Connection.Mode = "tunnel";
                config.Connection.ListenHost = "127.0.0.1";
                config.Connection.UseHttps = false;
                Console.Write("   Tunnel provider [cloudflared / ssh / custom] (default cloudflared): ");
                var tp = Console.ReadLine()?.Trim();
                config.Connection.TunnelProvider = string.IsNullOrWhiteSpace(tp) ? "cloudflared" : tp.ToLowerInvariant();
                if (config.Connection.TunnelProvider == "ssh")
                {
                    Console.Write("   SSH target (e.g. user@your-host): ");
                    var sshHost = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(sshHost))
                        config.Connection.TunnelArguments = ["-N", "-R", $"43170:127.0.0.1:{config.Connection.Port}", sshHost];
                }
                break;

            default:
                config.Connection.Mode = "localhost";
                config.Connection.ListenHost = "127.0.0.1";
                config.Connection.UseHttps = false;
                config.Connection.TunnelProvider = "none";
                break;
        }

        Console.Write($"4. Listening port [{config.Connection.Port}]: ");
        var portRaw = Console.ReadLine()?.Trim();
        if (int.TryParse(portRaw, out var parsedPort) && parsedPort is >= 1024 and <= 65535)
            config.Connection.Port = parsedPort;

        config.SetupCompleted = true;
        config.Save(paths);

        // Network reachability summary.
        var net = TunnelManager.DiagnoseNetwork(config, checkPortBind: true);
        Console.WriteLine();
        Console.WriteLine("Setup Complete");
        Console.WriteLine(new string('─', 52));
        Console.WriteLine($"Workspace : {config.DefaultWorkspace}");
        Console.WriteLine($"Endpoint  : {config.GetEffectiveBaseUrl()}/mcp");
        Console.WriteLine($"Key       : {credentials.GetMasked()} ({(createdKey ? "generated in data/secret.key" : "loaded from data/secret.key")})");
        Console.WriteLine($"Config    : {paths.ConfigFile}");
        foreach (var adv in net.Advisories)
            Console.WriteLine($"Note      : {adv}");
        Console.WriteLine();
        return 0;
    }

    public static int RunScripted(PortablePaths paths, IReadOnlyList<string> args)
    {
        paths.EnsureDirectories();
        var reset = HasFlag(args, "--reset");
        if (reset && File.Exists(paths.ConfigFile))
            File.Delete(paths.ConfigFile);

        var config = AppConfig.Load(paths);
        var credentials = new CredentialStore(paths);
        credentials.EnsureInitialized();
        if (HasFlag(args, "--rotate-key"))
            credentials.Rotate();

        var ws = GetOpt(args, "--workspace");
        var createWs = HasFlag(args, "--create-workspace") || HasFlag(args, "--create");
        if (!string.IsNullOrWhiteSpace(ws))
        {
            var fullWs = Path.GetFullPath(ws);
            var guard = new WorkspaceGuard(paths, config);
            guard.ValidateTrustableDirectory(fullWs);
            if (!Directory.Exists(fullWs))
            {
                if (!createWs)
                {
                    Console.Error.WriteLine($"Error: Workspace '{fullWs}' does not exist. Pass --create-workspace to create it.");
                    return 2;
                }
                Directory.CreateDirectory(fullWs);
            }
            config.AddTrustedWorkspace(fullWs, GetOpt(args, "--alias"), setAsDefault: true);
        }
        else if (string.IsNullOrWhiteSpace(config.DefaultWorkspace))
        {
            var fallback = Path.Combine(paths.SandboxDir, "default-workspace");
            Directory.CreateDirectory(fallback);
            config.AddTrustedWorkspace(fallback, "sandbox", setAsDefault: true);
        }

        if (GetOpt(args, "--mode") is { } mode)
        {
            config.Connection.Mode = mode.ToLowerInvariant();
            if (config.Connection.Mode == "localhost")
            {
                config.Connection.ListenHost = "127.0.0.1";
                config.Connection.UseHttps = false;
                config.Connection.TunnelProvider = "none";
            }
            else if (config.Connection.Mode == "direct_https")
            {
                config.Connection.ListenHost = GetOpt(args, "--listen") ?? "0.0.0.0";
                config.Connection.UseHttps = true;
                config.Connection.AutoGenerateSelfSignedTls = string.IsNullOrWhiteSpace(GetOpt(args, "--tls-cert"));
            }
            else if (config.Connection.Mode == "tunnel")
            {
                config.Connection.ListenHost = "127.0.0.1";
                config.Connection.UseHttps = false;
                config.Connection.TunnelProvider = GetOpt(args, "--tunnel") ?? "cloudflared";
            }
        }

        if (GetOpt(args, "--listen") is { } listen) config.Connection.ListenHost = listen;
        if (int.TryParse(GetOpt(args, "--port"), out var port)) config.Connection.Port = port;
        if (GetOpt(args, "--public-endpoint") is { } pub) config.Connection.PublicEndpoint = pub;
        if (GetOpt(args, "--tls-cert") is { } cert) config.Connection.TlsCertPath = cert;
        if (GetOpt(args, "--tunnel") is { } tun) config.Connection.TunnelProvider = tun;
        if (GetOpt(args, "--git-push") is { } gp) config.Autonomy.GitPushPolicy = gp;
        if (GetOpt(args, "--theme") is { } theme) config.Tui.Theme = theme;
        if (HasFlag(args, "--https")) { config.Connection.UseHttps = true; config.Connection.AutoGenerateSelfSignedTls = string.IsNullOrWhiteSpace(config.Connection.TlsCertPath); }

        config.SetupCompleted = true;
        config.Save(paths);

        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                portableRoot = paths.RootDir,
                configFile = paths.ConfigFile,
                defaultWorkspace = config.DefaultWorkspace,
                mode = config.Connection.Mode,
                mcpEndpoint = $"{config.GetEffectiveBaseUrl()}/mcp",
                httpApi = $"{config.GetEffectiveBaseUrl()}/api/v1",
                maskedKey = credentials.GetMasked()
            }, AppConfig.JsonOptions));
        }
        else
        {
            Console.WriteLine($"LocalIdeBridge configured.");
            Console.WriteLine($"  Workspace : {config.DefaultWorkspace}");
            Console.WriteLine($"  Endpoint  : {config.GetEffectiveBaseUrl()}/mcp");
            Console.WriteLine($"  Key       : {credentials.GetMasked()}");
            Console.WriteLine($"  Config    : {paths.ConfigFile}");
        }
        return 0;
    }

    public static void PrintToolDoctorSummary(PortablePaths paths)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(ProcessRunner.ProbeInstalledTools(paths));
        foreach (var item in json.EnumerateArray())
        {
            var name = item.GetProperty("tool").GetString();
            var installed = item.GetProperty("installed").GetBoolean();
            var ver = item.GetProperty("version").GetString();
            Console.WriteLine(installed
                ? $"   [OK] {name,-28} {ver}"
                : $"   [--] {name,-28} not detected");
        }
    }

    public static string? GetOpt(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];
        }
        return null;
    }

    public static bool HasFlag(IReadOnlyList<string> args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
