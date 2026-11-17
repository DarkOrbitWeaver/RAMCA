using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Protocols;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;
using LocalIdeBridge.Workflows;

namespace LocalIdeBridge.Core;

/// <summary>
/// Owns the host, tool registry, network listener, and process supervisor.
/// Shutdown stops remote access first, then cancels jobs and releases local resources.
/// </summary>
public sealed class WorkbenchHost : IAsyncDisposable
{
    public PortablePaths Paths { get; }
    public AppConfig Config { get; }
    public CredentialStore Credentials { get; }
    public RedactingLogger Logger { get; }
    public StateDatabase Database { get; }
    public LogStore Logs { get; }
    public WorkspaceGuard Guard { get; }
    public ChildProcessSupervisor ProcessSupervisor { get; }
    public ProcessRunner Runner { get; }
    public TunnelManager Tunnel { get; }
    public SearchAndIntelTools CodeIntel { get; }
    public ToolRegistry Registry { get; }
    public JobManager Jobs { get; }
    public WorkflowEngine Workflows { get; }
    public ConnectionInstructions Instructions { get; }
    public McpServer Mcp { get; }
    public int RecoveredInterruptedItems { get; }
    public bool CreatedNewCredential { get; }
    public string BoundLocalUrl { get; private set; } = "";

    private WebApplication? _webApp;
    private X509Certificate2? _tlsCert;
    private int _disposed;
    // An OS-managed exclusive file lease prevents concurrent hosts from modifying one portable state DB.
    private readonly FileStream _instanceLock;

    public WorkbenchHost(PortablePaths paths, AppConfig? explicitConfig = null)
    {
        Paths = paths;
        Paths.EnsureDirectories();
        // Acquire BEFORE cleanup, recovery, credential initialization, and logging.
        // FileShare.None is released by the OS even if the process crashes.
        try
        {
            _instanceLock = new FileStream(Path.Combine(Paths.DataDir, "host.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "RAMCA is already running against this portable folder. Connect to its MCP/HTTP listener instead of starting another host.", ex);
        }

        try
        {
            Paths.CleanTempDirectory();

            Config = explicitConfig ?? AppConfig.Load(Paths);
            Config.Normalize();

            Credentials = new CredentialStore(Paths);
            CreatedNewCredential = Credentials.EnsureInitialized();

            Logger = new RedactingLogger(Paths, Credentials, Config.Retention, Config.Resources.TuiEventBufferSize);
            Logger.PruneExpiredLogs();

            Guard = new WorkspaceGuard(Paths, Config);
            Database = new StateDatabase(Paths, Credentials, Config.Retention);
            RecoveredInterruptedItems = Database.Initialize(Guard.ActiveWorkspace ?? "");
            if (RecoveredInterruptedItems > 0)
            {
                Logger.Warn("recovery", $"Recovered {RecoveredInterruptedItems} interrupted item(s) from prior run without replaying non-idempotent commands.");
            }

            Logs = new LogStore(Paths, Database, Credentials, Config.Retention);
            ProcessSupervisor = new ChildProcessSupervisor(Config.Resources.ChildProcessMemoryLimitMb);
            Runner = new ProcessRunner(Paths, Config, Guard, Database, Logs, ProcessSupervisor, Logger);
            Tunnel = new TunnelManager(Paths, Config, ProcessSupervisor, Logger);
            CodeIntel = new SearchAndIntelTools(Config, Guard, Runner);

            Registry = new ToolRegistry(Guard, Database, Logger, Credentials);
            Jobs = new JobManager(Config, Guard, Database, Registry, Logger);
            Workflows = new WorkflowEngine(Config, Guard, Database, Registry, Logger);

            // Register all authoritative tools once for MCP, HTTPS API, CLI, and Workflows.
            Registry.RegisterRange(WorkspaceTools.Create(Paths, Config, Guard, Database, Runner, Registry));
            Registry.RegisterRange(CodeIntel.CreateTools());
            Registry.RegisterRange(EditingTools.Create(Paths, Config, Guard, Database, CodeIntel.InvalidateFile));
            Registry.RegisterRange(BuildGitCommandTools.Create(Paths, Config, Guard, Database, Logs, Runner, ProcessSupervisor));
            Registry.RegisterRange(JobAndWorkflowTools.Create(Guard, Database, Jobs, Workflows));

            Instructions = new ConnectionInstructions(Config, Guard, Registry, Tunnel);
            Mcp = new McpServer(Config, Guard, Database, Registry, Instructions, Logger);
        }
        catch
        {
            _instanceLock.Dispose();
            throw;
        }
    }

    public async Task StartNetworkListenerAsync(CancellationToken ct = default)
    {
        if (_webApp is not null)
            throw new InvalidOperationException("Network listener is already running.");

        var conn = Config.Connection;
        var isRemoteBind = conn.ListenHost is not ("127.0.0.1" or "::1" or "localhost");
        if (isRemoteBind && Config.Security.RequireHttpsForRemote && !conn.UseHttps)
        {
            throw new InvalidOperationException(
                $"Refusing to bind non-loopback address '{conn.ListenHost}' without HTTPS. " +
                "Enable connection.useHttps (with tlsCertPath or autoGenerateSelfSignedTls=true) or use 127.0.0.1 with an HTTPS tunnel.");
        }

        if (conn.UseHttps)
        {
            if (!string.IsNullOrWhiteSpace(conn.TlsCertPath))
            {
                if (!File.Exists(conn.TlsCertPath))
                    throw new FileNotFoundException($"Configured TLS certificate not found: {conn.TlsCertPath}");
                var pwd = string.IsNullOrWhiteSpace(conn.TlsCertPasswordEnvVar)
                    ? null
                    : Environment.GetEnvironmentVariable(conn.TlsCertPasswordEnvVar);
                _tlsCert = X509CertificateLoader.LoadPkcs12FromFile(conn.TlsCertPath, pwd);
            }
            else if (conn.AutoGenerateSelfSignedTls || conn.Mode.Equals("direct_https", StringComparison.OrdinalIgnoreCase))
            {
                _tlsCert = TunnelManager.GetOrCreateSelfSignedTlsCert(Paths, conn.ListenHost);
            }
        }

        if (conn.UseHttps && _tlsCert is null)
            throw new InvalidOperationException("connection.useHttps=true requires a configured TLS certificate or autoGenerateSelfSignedTls=true. Refusing silent HTTP downgrade.");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "LocalIdeBridge",
            ContentRootPath = Paths.RootDir
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Config.Security.MaxRequestBodyBytes;
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);

            void ConfigureListen(ListenOptions lo)
            {
                lo.Protocols = HttpProtocols.Http1AndHttp2;
                if (conn.UseHttps && _tlsCert is not null)
                    lo.UseHttps(_tlsCert);
            }

            if (conn.ListenHost is "0.0.0.0" or "*")
                kestrel.ListenAnyIP(conn.Port, ConfigureListen);
            else if (conn.ListenHost == "::")
                kestrel.Listen(IPAddress.IPv6Any, conn.Port, ConfigureListen);
            else if (conn.ListenHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                kestrel.ListenLocalhost(conn.Port, ConfigureListen);
            else if (IPAddress.TryParse(conn.ListenHost, out var ip))
                kestrel.Listen(ip, conn.Port, ConfigureListen);
            else
                kestrel.ListenLocalhost(conn.Port, ConfigureListen);
        });

        var app = builder.Build();
        app.UseMiddleware<SecurityMiddleware>(Config, Credentials, Logger);
        Mcp.MapEndpoints(app);
        HttpApiEndpoints.MapEndpoints(app, Config, Guard, Database, Registry, Jobs, Tunnel, Instructions);

        await app.StartAsync(ct).ConfigureAwait(false);
        _webApp = app;

        // Discover actual bound port (critical when Port=0 is used for ephemeral test servers).
        var boundAddress = app.Urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(boundAddress) && Uri.TryCreate(boundAddress, UriKind.Absolute, out var boundUri))
        {
            Config.Connection.Port = boundUri.Port;
            var host = boundUri.Host is "0.0.0.0" or "[::]" or "::" ? "127.0.0.1" : boundUri.Host;
            BoundLocalUrl = $"{boundUri.Scheme}://{host}:{boundUri.Port}";
        }
        else
        {
            BoundLocalUrl = Config.GetEffectiveBaseUrl();
        }

        PortablePaths.AtomicWriteAllText(Paths.HostLockFile,
            $"{Environment.ProcessId}|{BoundLocalUrl}|{DateTimeOffset.UtcNow:O}{Environment.NewLine}");

        Logger.Info("host", $"Listener online at {BoundLocalUrl} (mode={conn.Mode}, tools={Registry.All.Count}).");

        if (conn.Mode.Equals("tunnel", StringComparison.OrdinalIgnoreCase))
        {
            var tunnelUrl = await Tunnel.StartIfConfiguredAsync(BoundLocalUrl, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tunnelUrl))
                Logger.Info("host", $"Tunnel endpoint ready: {tunnelUrl}/mcp");
            else
                Logger.Warn("host", "Tunnel started; waiting for public URL announcement in logs.");
        }
    }

    public async Task StopAsync()
    {
        var sw = Stopwatch.StartNew();

        // 1. Stop external tunnel immediately.
        Tunnel.Stop();

        // 2. Stop accepting new HTTP/MCP connections and release socket port.
        if (_webApp is not null)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _webApp.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch { }
            try { await _webApp.DisposeAsync().ConfigureAwait(false); } catch { }
            _webApp = null;
        }

        // 3. Cancel active jobs and terminate all owned child process trees.
        await Jobs.ShutdownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        ProcessSupervisor.TerminateAll();

        _tlsCert?.Dispose();
        _tlsCert = null;

        if (File.Exists(Paths.HostLockFile))
        {
            try { File.Delete(Paths.HostLockFile); } catch { }
        }

        Paths.CleanTempDirectory();
        sw.Stop();
        Logger.Info("host", "Listener and all owned child processes stopped.", null, sw.ElapsedMilliseconds);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync().ConfigureAwait(false);
        Tunnel.Dispose();
        Jobs.Dispose();
        ProcessSupervisor.Dispose();
        Guard.Dispose();
        Database.Dispose();
        Logger.Dispose();
        // Hold the exclusive state lease until *all* state handles have closed.
        _instanceLock.Dispose();
    }
}
