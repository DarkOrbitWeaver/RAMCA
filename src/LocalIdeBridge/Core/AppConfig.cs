using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalIdeBridge.Core;

/// <summary>
/// Versioned, human-readable application configuration persisted in config.json.
/// Secrets are intentionally excluded and stored exclusively in data/secret.key.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool SetupCompleted { get; set; }
    public List<WorkspaceEntry> TrustedWorkspaces { get; set; } = [];
    public string? DefaultWorkspace { get; set; }
    public ConnectionSettings Connection { get; set; } = new();
    public AutonomySettings Autonomy { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();
    public ResourceSettings Resources { get; set; } = new();
    public TuiSettings Tui { get; set; } = new();
    public IntegrationSettings Integrations { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load(PortablePaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
            return new AppConfig();

        var json = File.ReadAllText(paths.ConfigFile);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        cfg.Normalize();
        return cfg;
    }

    public void Save(PortablePaths paths)
    {
        Normalize();
        var json = JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine;
        PortablePaths.AtomicWriteAllText(paths.ConfigFile, json);
    }

    /// <summary>
    /// Exports a shareable configuration JSON with machine-local certificate paths sanitized and zero secrets.
    /// </summary>
    public string ExportShareableJson()
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.Connection.TlsCertPath = null;
        copy.Connection.TlsCertPasswordEnvVar = null;
        copy.Connection.PublicEndpoint = null;
        return JsonSerializer.Serialize(copy, JsonOptions) + Environment.NewLine;
    }

    public void AddTrustedWorkspace(string path, string? alias = null, bool setAsDefault = false)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = TrustedWorkspaces.FirstOrDefault(w => string.Equals(
            w.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), full, comparison));

        if (existing is null)
        {
            TrustedWorkspaces.Add(new WorkspaceEntry
            {
                Name = string.IsNullOrWhiteSpace(alias) ? Path.GetFileName(full) : alias.Trim(),
                Path = full,
                AddedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else if (!string.IsNullOrWhiteSpace(alias))
        {
            existing.Name = alias.Trim();
        }

        if (setAsDefault || string.IsNullOrWhiteSpace(DefaultWorkspace))
            DefaultWorkspace = full;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Connection.Port = Math.Clamp(Connection.Port, 0, 65535);
        Security.RequestsPerMinute = Math.Clamp(Security.RequestsPerMinute, 10, 100_000);
        Security.FailedAuthPerMinute = Math.Clamp(Security.FailedAuthPerMinute, 3, 200);
        Security.MaxRequestBodyBytes = Math.Clamp(Security.MaxRequestBodyBytes, 64 * 1024, 64 * 1024 * 1024);
        Retention.MaxLogAgeDays = Math.Clamp(Retention.MaxLogAgeDays, 1, 365);
        Retention.MaxSessionCount = Math.Clamp(Retention.MaxSessionCount, 5, 5_000);
        Retention.MaxSingleLogBytes = Math.Clamp(Retention.MaxSingleLogBytes, 256 * 1024, 256L * 1024 * 1024);
        Retention.MaxToolOutputChars = Math.Clamp(Retention.MaxToolOutputChars, 1_024, 128_000);
        Retention.EditJournalMaxEntries = Math.Clamp(Retention.EditJournalMaxEntries, 20, 5_000);
        Resources.MaxConcurrentJobs = Math.Clamp(Resources.MaxConcurrentJobs, 1, 32);
        Resources.MaxParallelReadSteps = Math.Clamp(Resources.MaxParallelReadSteps, 1, 16);
        Resources.DefaultCommandTimeoutSeconds = Math.Clamp(Resources.DefaultCommandTimeoutSeconds, 5, 7_200);
        Resources.MaxCommandTimeoutSeconds = Math.Clamp(Resources.MaxCommandTimeoutSeconds, 10, 28_800);
        Resources.MemoryCacheMaxEntries = Math.Clamp(Resources.MemoryCacheMaxEntries, 8, 1_024);
        Resources.MemoryCacheMaxBytes = Math.Clamp(Resources.MemoryCacheMaxBytes, 256 * 1024, 64L * 1024 * 1024);
        Resources.TuiEventBufferSize = Math.Clamp(Resources.TuiEventBufferSize, 32, 1_000);
        Tui.RefreshIntervalMs = Math.Clamp(Tui.RefreshIntervalMs, 100, 5_000);
    }

    public string GetEffectiveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(Connection.PublicEndpoint))
            return Connection.PublicEndpoint.TrimEnd('/');

        var scheme = Connection.UseHttps ? "https" : "http";
        var host = Connection.ListenHost is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : Connection.ListenHost;
        return $"{scheme}://{host}:{Connection.Port}";
    }
}

public sealed class WorkspaceEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConnectionSettings
{
    /// <summary>localhost | direct_https | tunnel</summary>
    public string Mode { get; set; } = "localhost";
    public string ListenHost { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 43170;
    public bool UseHttps { get; set; }
    public string? TlsCertPath { get; set; }
    public string? TlsCertPasswordEnvVar { get; set; }
    public bool AutoGenerateSelfSignedTls { get; set; }
    public string? PublicEndpoint { get; set; }
    /// <summary>none | cloudflared | ssh | custom</summary>
    public string TunnelProvider { get; set; } = "none";
    public string? TunnelExecutable { get; set; }
    public List<string> TunnelArguments { get; set; } = [];
}

public sealed class AutonomySettings
{
    public bool AllowFileWrites { get; set; } = true;
    public bool AllowFileDeletes { get; set; } = true;
    public bool AllowCommands { get; set; } = true;
    public bool AllowShellScripts { get; set; } = true;
    public bool AllowDependencyInstall { get; set; } = true;
    public bool AllowGitCommits { get; set; } = true;
    public bool AllowGitWorktrees { get; set; } = false;
    // Owner preference: main-only by default; explicit local opt-in for exceptions.
    public bool AllowGitBranchChanges { get; set; } = false;
    /// <summary>deny | allow</summary>
    public string GitPushPolicy { get; set; } = "deny";
    public bool RedirectChildTempToPortable { get; set; }

    public List<string> AllowedExecutables { get; set; } =
    [
        "dotnet", "git", "msbuild", "vstest.console",
        "powershell", "pwsh", "cmd",
        "npm", "npx", "node", "nuget", "csc"
    ];

    public List<string> BlockedCommandPatterns { get; set; } =
    [
        "format ", "diskpart", "bcdedit", "cipher /w",
        "reg delete", "reg add hklm", "sc create", "sc delete",
        "schtasks /create", "netsh advfirewall", "New-NetFirewallRule",
        "Set-MpPreference", "Add-MpPreference", "DisableRealtimeMonitoring",
        "Stop-Computer", "Restart-Computer", "shutdown ",
        "Remove-Item -Recurse -Force C:\\", "rm -rf /"
    ];
}

public sealed class SecuritySettings
{
    public int RequestsPerMinute { get; set; } = 600;
    public int FailedAuthPerMinute { get; set; } = 20;
    public long MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;
    public bool RequireHttpsForRemote { get; set; } = true;
    public List<string> AllowedOrigins { get; set; } = [];
}

public sealed class RetentionSettings
{
    public int MaxLogAgeDays { get; set; } = 14;
    public int MaxSessionCount { get; set; } = 100;
    public long MaxSingleLogBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxToolOutputChars { get; set; } = 16_000;
    public int EditJournalMaxEntries { get; set; } = 250;
}

public sealed class ResourceSettings
{
    public int MaxConcurrentJobs { get; set; } = 4;
    public int MaxParallelReadSteps { get; set; } = 4;
    public int DefaultCommandTimeoutSeconds { get; set; } = 180;
    public int MaxCommandTimeoutSeconds { get; set; } = 1_800;
    public int ChildProcessMemoryLimitMb { get; set; } = 0; // 0 = no explicit per-job cap
    public int MemoryCacheMaxEntries { get; set; } = 64;
    public long MemoryCacheMaxBytes { get; set; } = 4 * 1024 * 1024;
    public int TuiEventBufferSize { get; set; } = 200;
}

public sealed class TuiSettings
{
    /// <summary>minimal | high_contrast | mono</summary>
    public string Theme { get; set; } = "minimal";
    public int RefreshIntervalMs { get; set; } = 500;
}

public sealed class IntegrationSettings
{
    public bool EnableRoslynSemantic { get; set; } = true;
    public bool EnableOpenApi { get; set; } = true;
    public bool EnableMcpSseStream { get; set; } = true;
    /// <summary>auto preserves legacy clients; text or structured sends one full result.</summary>
    public string McpResultMode { get; set; } = "auto";
}

/// <summary>
/// Optional persistent per-repository policy loaded from &lt;workspace&gt;/.localidebridge.json.
/// Can only restrict or add approved project executables; never escapes workspace confinement.
/// </summary>
public sealed class RepoPolicy
{
    public List<string> ReadOnlyPaths { get; set; } = [];
    public List<string> DeniedPaths { get; set; } = [];
    public List<string> ApprovedProjectExecutables { get; set; } = [];
    public bool? AllowGitPush { get; set; }
    public bool? AllowFileDeletes { get; set; }
    public bool? AllowShellScripts { get; set; }
}
