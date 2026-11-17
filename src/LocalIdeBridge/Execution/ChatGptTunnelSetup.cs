using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Execution;

/// <summary>
/// One-time, per-installation setup for OpenAI Secure MCP Tunnel.
/// The user keeps the tunnel-client executable in RAMCA/tools and RAMCA starts/stops it.
/// The runtime key never appears in command-line arguments, connection instructions, or logs.
/// </summary>
public static class ChatGptTunnelSetup
{
    public sealed record Settings(string TunnelId);

    public static string SettingsFile(PortablePaths paths) => Path.Combine(paths.DataDir, "chatgpt-tunnel.json");
    public static string KeyFile(PortablePaths paths) => Path.Combine(paths.DataDir, "chatgpt-runtime.key");
    public static string ProfileFile(PortablePaths paths) => Path.Combine(paths.DataDir, "chatgpt-tunnel.yaml");

    public static bool IsConfigured(PortablePaths paths) =>
        File.Exists(SettingsFile(paths)) && File.Exists(KeyFile(paths));

    public static Settings? Load(PortablePaths paths)
    {
        if (!IsConfigured(paths)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile(paths)));
            return value is not null && IsValidTunnelId(value.TunnelId) ? value : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public static bool IsValidTunnelId(string? value) =>
        value is not null && Regex.IsMatch(value,
            @"^tunnel_(?:[a-z0-9]{4}_)?[a-z0-9]{32}$", RegexOptions.CultureInvariant);

    /// <summary>Called from the CLI once; never requires an OpenAI admin key.</summary>
    public static int Configure(PortablePaths paths, IReadOnlyList<string> args)
    {
        if (args.Contains("--disable", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in new[] { SettingsFile(paths), KeyFile(paths), ProfileFile(paths) })
                if (File.Exists(file)) File.Delete(file);
            Console.WriteLine("ChatGPT auto-tunnel disabled. Restart RAMCA or press Q to stop the existing tunnel.");
            return 0;
        }

        var tunnelId = LocalIdeBridge.Cli.SetupWizard.GetOpt(args, "--tunnel-id");
        if (string.IsNullOrWhiteSpace(tunnelId) && !Console.IsInputRedirected)
        {
            Console.Write("OpenAI tunnel ID (from Platform > Tunnels): ");
            tunnelId = Console.ReadLine()?.Trim();
        }
        if (!IsValidTunnelId(tunnelId))
        {
            Console.Error.WriteLine("Usage: LocalIdeBridge connect-chatgpt --tunnel-id tunnel_<id>");
            Console.Error.WriteLine("Use the existing tunnel ID from OpenAI Platform > Tunnels.");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The integrated setup currently targets Windows. Other OS clients can use normal tunnel-client profiles.");
            return 2;
        }

        var key = Environment.GetEnvironmentVariable("RAMCA_TUNNEL_SETUP_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Run interactively to enter the runtime key, or supply RAMCA_TUNNEL_SETUP_KEY in the process environment.");
                return 2;
            }
            Console.Write("OpenAI tunnel runtime API key (hidden): ");
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
                else if (!char.IsControl(k.KeyChar) && sb.Length < 1024) sb.Append(k.KeyChar);
            }
            Console.WriteLine();
            key = sb.ToString();
            sb.Clear();
        }
        if (!key.StartsWith("sk-", StringComparison.Ordinal) || key.Length < 25 ||
            key.Any(char.IsWhiteSpace))
        {
            Console.Error.WriteLine("Invalid runtime key format; nothing saved.");
            return 2;
        }

        paths.EnsureDirectories();
        var binary = FindBinary(paths);
        if (binary is null)
        {
            Console.WriteLine("Downloading verified official tunnel-client for Windows x64...");
            try { binary = InstallOfficialClientAsync(paths).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not install tunnel-client: {ex.Message}");
                Console.Error.WriteLine("You can still extract the official Windows x64 ZIP into RAMCA/tools/tunnel-client/ and retry.");
                return 2;
            }
        }

        PortablePaths.AtomicWriteAllText(KeyFile(paths), key + Environment.NewLine);
        try { RestrictToCurrentWindowsUser(KeyFile(paths)); }
        catch
        {
            try { File.Delete(KeyFile(paths)); } catch { }
            throw;
        }
        PortablePaths.AtomicWriteAllText(SettingsFile(paths),
            JsonSerializer.Serialize(new Settings(tunnelId!)) + Environment.NewLine);
        Console.WriteLine("Saved ChatGPT tunnel connection locally (API key not displayed).");
        Console.WriteLine("RAMCA will start its tunnel automatically on launch, or press C in the running RAMCA window.");
        Console.WriteLine("In ChatGPT: Add custom MCP > Tunnel > select your existing tunnel > No authentication.");
        return 0;
    }

    /// <summary>
    /// First-run convenience download only. Never execute an unverified binary.
    /// The pinned SHA-256 belongs to the official OpenAI v0.0.21 windows-amd64 release asset.
    /// </summary>
    public static async Task<string> InstallOfficialClientAsync(PortablePaths paths, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Automatic tunnel-client installation supports Windows x64 only.");

        const string url = "https://github.com/openai/tunnel-client/releases/download/v0.0.21/tunnel-client-v0.0.21-windows-amd64.zip";
        const string sha = "2faf80ae62b58485fcef534cd3f78cd8b82e71cb04d14c2a5c73dbb4eb6c575a";
        var destinationDir = Path.Combine(paths.ToolsDir, "tunnel-client");
        var destination = Path.Combine(destinationDir, "tunnel-client.exe");
        if (File.Exists(destination)) return destination;

        paths.EnsureDirectories();
        Directory.CreateDirectory(destinationDir);
        var archivePath = Path.Combine(paths.TempDir, $"tunnel-client-{Guid.NewGuid():N}.zip");
        var stagingExe = Path.Combine(destinationDir, $".tunnel-client-{Guid.NewGuid():N}.tmp");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                // Do not allow an unexpectedly large release archive to consume the drive.
                var buffer = new byte[81920];
                long count = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    count += read;
                    if (count > 85L * 1024 * 1024)
                        throw new InvalidDataException("Tunnel-client archive exceeds the expected size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            await using (var stream = File.OpenRead(archivePath))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                if (!string.Equals(digest, sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded tunnel-client SHA-256 does not match the pinned official asset.");
            }

            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var entry = zip.Entries.SingleOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), "tunnel-client.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length is <= 0 or > 120L * 1024 * 1024)
                    throw new InvalidDataException("Verified archive does not contain the expected tunnel-client.exe.");
                await using var input = entry.Open();
                await using var output = new FileStream(stagingExe, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(stagingExe, destination, overwrite: false);
            return destination;
        }
        finally
        {
            try { File.Delete(archivePath); } catch { }
            try { File.Delete(stagingExe); } catch { }
        }
    }

    public static string? FindBinary(PortablePaths paths)
    {
        var nested = Path.Combine(paths.ToolsDir, "tunnel-client", "tunnel-client.exe");
        if (File.Exists(nested)) return nested;
        var direct = Path.Combine(paths.ToolsDir, "tunnel-client.exe");
        if (File.Exists(direct)) return direct;
        // Also support a source checkout with a published app in its /portable child.
        // Both locations remain explicitly inside the user's chosen RAMCA installation.
        if (Path.GetFileName(paths.RootDir).Equals("portable", StringComparison.OrdinalIgnoreCase))
        {
            var checkoutTools = Path.Combine(Directory.GetParent(paths.RootDir)!.FullName,
                "tools", "tunnel-client", "tunnel-client.exe");
            if (File.Exists(checkoutTools)) return checkoutTools;
        }
        return null; // Never launch an arbitrary binary from PATH for remote access.
    }

    /// <summary>Writes a profile containing no plaintext API key. Local MCP header uses a file reference.</summary>
    public static string CreateProfile(PortablePaths paths, string localMcpUrl)
    {
        var settings = Load(paths) ?? throw new InvalidOperationException("ChatGPT tunnel has not been configured.");
        if (!Uri.TryCreate(localMcpUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttp || url.AbsolutePath != "/mcp" ||
            url.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("The OpenAI tunnel may only forward to RAMCA's localhost MCP endpoint.");

        var secretPath = paths.SecretKeyFile.Replace('\\', '/');
        var profile = $"""
            config_version: 1
            control_plane:
              tunnel_id: {settings.TunnelId}
              api_key: env:CONTROL_PLANE_API_KEY
            mcp:
              server_urls:
                - channel: main
                  url: {JsonSerializer.Serialize(localMcpUrl)}
              extra_headers:
                X-LocalIdeBridge-Key: {JsonSerializer.Serialize("file:" + secretPath)}
              discovery_extra_headers:
                X-LocalIdeBridge-Key: {JsonSerializer.Serialize("file:" + secretPath)}
            health:
              listen_addr: 127.0.0.1:0
            """;
        PortablePaths.AtomicWriteAllText(ProfileFile(paths), profile + Environment.NewLine);
        return ProfileFile(paths);
    }

    private static void RestrictToCurrentWindowsUser(string path)
    {
        // Match RAMCA's existing portable secret.key ACL policy. Do not announce success if ACL tightening fails.
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            ArgumentList = { path, "/inheritance:r", "/grant:r", $"{Environment.UserDomainName}\\{Environment.UserName}:(R,W)" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (proc is null || !proc.WaitForExit(5000) || proc.ExitCode != 0)
            throw new InvalidOperationException("Could not restrict the API key file permissions. Disable connection and check folder ACLs.");
    }
}
