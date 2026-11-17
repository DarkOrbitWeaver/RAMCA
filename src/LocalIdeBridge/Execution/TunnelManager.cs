using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Execution;

public sealed record NetworkDiagnosticReport(
    string ConfiguredMode,
    string ListenHost,
    int Port,
    bool PortAvailable,
    bool HasPrivateNatIpv4,
    bool HasCgnatIpv4,
    bool HasPublicIpv4Candidate,
    bool HasGlobalIpv6Candidate,
    bool TlsReady,
    IReadOnlyList<string> InterfaceAddresses,
    IReadOnlyList<string> Advisories);

/// <summary>
/// Manages optional lightweight application-owned tunnel subprocesses (cloudflared, ssh -R, or custom)
/// and provides honest NAT / CGNAT / TLS / port diagnostics without silently altering firewall or router settings.
/// </summary>
public sealed partial class TunnelManager : IDisposable
{
    private readonly PortablePaths _paths;
    private readonly AppConfig _config;
    private readonly ChildProcessSupervisor _supervisor;
    private readonly RedactingLogger _logger;
    private Process? _tunnelProcess;
    private string? _announcedEndpoint;
    private readonly object _lock = new();

    [GeneratedRegex(@"https://[a-zA-Z0-9\-\.]+\.[a-zA-Z]{2,}(?::\d+)?", RegexOptions.Compiled)]
    private static partial Regex HttpsUrlRegex();

    public string? ActiveTunnelEndpoint { get { lock (_lock) return _announcedEndpoint; } }
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                try { return _tunnelProcess is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public TunnelManager(PortablePaths paths, AppConfig config, ChildProcessSupervisor supervisor, RedactingLogger logger)
    {
        _paths = paths;
        _config = config;
        _supervisor = supervisor;
        _logger = logger;
    }

    public async Task<string?> StartIfConfiguredAsync(string localOriginUrl, CancellationToken ct)
    {
        var conn = _config.Connection;
        if (!conn.Mode.Equals("tunnel", StringComparison.OrdinalIgnoreCase) ||
            conn.TunnelProvider.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string exe;
        List<string> args;

        switch (conn.TunnelProvider.ToLowerInvariant())
        {
            case "cloudflared":
                exe = conn.TunnelExecutable
                      ?? FindPortableOrPath("cloudflared")
                      ?? throw new FileNotFoundException(
                          "Tunnel provider 'cloudflared' selected, but cloudflared was not found in tools/ or PATH. " +
                          "Place cloudflared.exe in LocalIdeBridge\\tools\\ or switch connection.mode to 'localhost' or 'direct_https'.");
                args = conn.TunnelArguments.Count > 0
                    ? [.. conn.TunnelArguments]
                    : ["tunnel", "--no-autoupdate", "--url", localOriginUrl];
                break;

            case "ssh":
                exe = conn.TunnelExecutable
                      ?? FindPortableOrPath("ssh")
                      ?? throw new FileNotFoundException("OpenSSH client (ssh) was not found in PATH.");
                if (conn.TunnelArguments.Count == 0)
                    throw new InvalidOperationException(
                        "For 'ssh' tunnel mode, configure connection.tunnelArguments (e.g. [\"-N\", \"-R\", \"443:127.0.0.1:43170\", \"user@your-host\"]).");
                args = [.. conn.TunnelArguments];
                break;

            case "custom":
                if (string.IsNullOrWhiteSpace(conn.TunnelExecutable))
                    throw new InvalidOperationException("Custom tunnel provider requires connection.tunnelExecutable.");
                exe = FindPortableOrPath(conn.TunnelExecutable) ?? conn.TunnelExecutable;
                args = [.. conn.TunnelArguments];
                break;

            default:
                throw new InvalidOperationException($"Unsupported tunnel provider '{conn.TunnelProvider}'.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _paths.RootDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to launch tunnel executable '{exe}'.");

        try { _supervisor.Register(proc); }
        catch
        {
            ChildProcessSupervisor.TryKillTree(proc);
            proc.Dispose();
            throw;
        }
        try { proc.StandardInput.Close(); } catch { }

        lock (_lock)
        {
            _tunnelProcess = proc;
        }

        var urlTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void WatchStream(StreamReader reader)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Tunnel implementations can emit malformed/unbounded lines.
                    // Drain all output, but never retain more than one 8 KiB line.
                    var buffer = new char[4096];
                    var line = new StringBuilder(8192);
                    var oversized = false;
                    void HandleLine()
                    {
                        if (oversized)
                        {
                            _logger.Warn("tunnel", "Tunnel output line exceeded 8 KiB; discarded.");
                            return;
                        }
                        var message = line.ToString().TrimEnd('\r');
                        if (message.Length == 0) return;
                        _logger.Info("tunnel", message);
                        var m = HttpsUrlRegex().Match(message);
                        if (m.Success &&
                            !m.Value.Contains("cloudflare.com/website-terms", StringComparison.OrdinalIgnoreCase) &&
                            !m.Value.Contains("developers.cloudflare.com", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (_lock) _announcedEndpoint ??= m.Value.TrimEnd('/');
                            urlTcs.TrySetResult(m.Value.TrimEnd('/'));
                        }
                    }
                    int count;
                    while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            if (buffer[i] == '\n')
                            {
                                HandleLine();
                                line.Clear();
                                oversized = false;
                            }
                            else if (!oversized)
                            {
                                if (line.Length >= 8192) oversized = true;
                                else line.Append(buffer[i]);
                            }
                        }
                    }
                    if (line.Length > 0 || oversized) HandleLine();
                }
                catch { }
                finally
                {
                    urlTcs.TrySetResult(null);
                }
            }, CancellationToken.None);
        }

        WatchStream(proc.StandardOutput);
        WatchStream(proc.StandardError);

        if (!string.IsNullOrWhiteSpace(conn.PublicEndpoint))
        {
            lock (_lock) _announcedEndpoint = conn.PublicEndpoint.TrimEnd('/');
            return _announcedEndpoint;
        }

        var completed = await Task.WhenAny(urlTcs.Task, Task.Delay(TimeSpan.FromSeconds(12), ct)).ConfigureAwait(false);
        return completed == urlTcs.Task ? await urlTcs.Task.ConfigureAwait(false) : null;
    }

    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _tunnelProcess;
            _tunnelProcess = null;
            _announcedEndpoint = null;
        }

        if (proc is not null)
        {
            try
            {
                var pid = proc.HasExited ? -1 : proc.Id;
                ChildProcessSupervisor.TryKillTree(proc);
                if (pid > 0) _supervisor.Unregister(pid);
                proc.Dispose();
                _logger.Info("tunnel", "Tunnel process terminated cleanly.");
            }
            catch { }
        }
    }

    public void Dispose() => Stop();

    private string? FindPortableOrPath(string name)
    {
        var inTools = Path.Combine(_paths.ToolsDir, name);
        if (File.Exists(inTools)) return inTools;
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name) && File.Exists(inTools + ".exe"))
            return inTools + ".exe";
        return ProcessRunner.FindOnPath(name);
    }

    public static X509Certificate2 GetOrCreateSelfSignedTlsCert(PortablePaths paths, string listenHost)
    {
        var certPath = Path.Combine(paths.DataDir, "selfsigned-tls.pfx");
        if (File.Exists(certPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(certPath, password: null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(3))
                    return existing;
                existing.Dispose();
            }
            catch { }
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=LocalIdeBridge", ecdsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        if (IPAddress.TryParse(listenHost, out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
            san.AddIpAddress(ip);
        else if (!string.IsNullOrWhiteSpace(listenHost) && listenHost is not "0.0.0.0" and not "::" and not "*" and not "localhost")
            san.AddDnsName(listenHost);

        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", "TLS Web Server Authentication") }, false));

        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var pfxBytes = ephemeral.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(paths.DataDir);
        PortablePaths.AtomicWriteAllBytes(certPath, pfxBytes);
        if (OperatingSystem.IsWindows())
        {
            // The passwordless PFX contains the private key; make the ACL user-only.
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls", ArgumentList = { certPath, "/inheritance:r", "/grant:r", $"{Environment.UserName}:(R,W)" },
                    CreateNoWindow = true, UseShellExecute = false
                });
                proc?.WaitForExit(3000);
            }
            catch { }
        }
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(certPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        // Reload from PFX so Windows SChannel can bind the persisted private key cleanly.
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }

    public static NetworkDiagnosticReport DiagnoseNetwork(AppConfig config, bool checkPortBind = true)
    {
        var conn = config.Connection;
        var addrs = new List<string>();
        var advisories = new List<string>();
        bool hasNat = false, hasCgnat = false, hasPublicV4 = false, hasGlobalV6 = false;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var b = ip.GetAddressBytes();
                        addrs.Add($"{nic.Name}: {ip}");
                        if (b[0] == 10 ||
                            (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                            (b[0] == 192 && b[1] == 168))
                        {
                            hasNat = true;
                        }
                        else if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                        {
                            // RFC 6598 Carrier-Grade NAT (100.64.0.0/10) (also used by Tailscale overlay IPs).
                            hasCgnat = true;
                        }
                        else if (b[0] == 169 && b[1] == 254)
                        {
                            // APIPA link-local
                        }
                        else if (b[0] != 127)
                        {
                            hasPublicV4 = true;
                        }
                    }
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                             !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6UniqueLocal && !IPAddress.IsLoopback(ip))
                    {
                        addrs.Add($"{nic.Name}: {ip}");
                        hasGlobalV6 = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            advisories.Add($"Could not enumerate all network interfaces: {ex.Message}");
        }

        var portAvailable = true;
        if (checkPortBind)
        {
            try
            {
                var bindIp = IPAddress.TryParse(conn.ListenHost, out var parsed) ? parsed : IPAddress.Loopback;
                using var listener = new TcpListener(bindIp, conn.Port);
                listener.Start();
                listener.Stop();
            }
            catch
            {
                portAvailable = false;
                advisories.Add($"TCP port {conn.Port} on {conn.ListenHost} is already in use or cannot be bound.");
            }
        }

        var tlsReady = !conn.UseHttps ||
                       conn.AutoGenerateSelfSignedTls ||
                       (!string.IsNullOrWhiteSpace(conn.TlsCertPath) && File.Exists(conn.TlsCertPath));

        if (conn.Mode.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            advisories.Add("Localhost-only mode: reachable only from agents running on this machine (no internet exposure).");
        }
        else if (conn.Mode.Equals("direct_https", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasPublicV4 && hasCgnat)
                advisories.Add("Detected RFC 6598 100.64.0.0/10 address (Carrier-Grade NAT or private mesh like Tailscale). Direct public IPv4 inbound connections from cloud AI services usually fail behind CGNAT; use a tunnel or mesh-connected agent.");
            else if (!hasPublicV4 && hasNat)
                advisories.Add("Only RFC 1918 private LAN IPv4 addresses detected (behind router NAT). Direct remote access requires manual router port-forwarding, a local firewall allow rule, and a valid TLS certificate. LocalIdeBridge never modifies router or firewall settings automatically.");
            if (!tlsReady)
                advisories.Add("Direct HTTPS requires either connection.tlsCertPath (PFX) or connection.autoGenerateSelfSignedTls=true. Note: cloud-hosted AI services generally require a publicly trusted CA certificate rather than a self-signed certificate.");
        }
        else if (conn.Mode.Equals("tunnel", StringComparison.OrdinalIgnoreCase))
        {
            advisories.Add($"Tunnel mode ({conn.TunnelProvider}): keep listenHost=127.0.0.1 so only the application-owned tunnel exposes HTTPS externally, and exits automatically when LocalIdeBridge closes.");
        }

        return new NetworkDiagnosticReport(
            conn.Mode,
            conn.ListenHost,
            conn.Port,
            portAvailable,
            hasNat,
            hasCgnat,
            hasPublicV4,
            hasGlobalV6,
            tlsReady,
            addrs,
            advisories);
    }
}
