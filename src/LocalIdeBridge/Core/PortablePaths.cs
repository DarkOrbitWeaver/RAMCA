using System.Diagnostics;

namespace LocalIdeBridge.Core;

/// <summary>
/// Manages the self-contained portable directory structure.
/// All LocalIdeBridge-owned persistent state lives strictly inside RootDir.
/// </summary>
public sealed class PortablePaths
{
    public string RootDir { get; }
    public string ConfigFile { get; }
    public string DataDir { get; }
    public string SessionsDir { get; }
    public string StateDbFile { get; }
    public string SecretKeyFile { get; }
    public string CliProfileFile { get; }
    public string HostLockFile { get; }
    public string LogsDir { get; }
    public string CacheDir { get; }
    public string TempDir { get; }
    public string SandboxDir { get; }
    public string ToolsDir { get; }
    public string DocsDir { get; }

    public PortablePaths(string? explicitRoot = null)
    {
        var candidate = explicitRoot;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Environment.GetEnvironmentVariable("LOCALIDEBRIDGE_HOME");
        }
        if (string.IsNullOrWhiteSpace(candidate))
        {
            // For single-file published executables, AppContext.BaseDirectory resolves
            // to the directory containing LocalIdeBridge.exe, never the extraction cache.
            candidate = AppContext.BaseDirectory;
        }

        RootDir = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ConfigFile = Path.Combine(RootDir, "config.json");
        DataDir = Path.Combine(RootDir, "data");
        SessionsDir = Path.Combine(DataDir, "sessions");
        StateDbFile = Path.Combine(DataDir, "state.db");
        SecretKeyFile = Path.Combine(DataDir, "secret.key");
        CliProfileFile = Path.Combine(DataDir, "cli-profile.json");
        HostLockFile = Path.Combine(DataDir, "host.pid");
        LogsDir = Path.Combine(RootDir, "logs");
        CacheDir = Path.Combine(RootDir, "cache");
        TempDir = Path.Combine(RootDir, "temp");
        SandboxDir = Path.Combine(RootDir, "sandbox");
        ToolsDir = Path.Combine(RootDir, "tools");
        DocsDir = Path.Combine(RootDir, "docs");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(SessionsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(SandboxDir);
        Directory.CreateDirectory(ToolsDir);
        Directory.CreateDirectory(DocsDir);
    }

    public string GetSessionDir(string sessionId)
    {
        var safeId = SanitizeId(sessionId);
        var dir = Path.Combine(SessionsDir, safeId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetSessionLogsDir(string sessionId)
    {
        var dir = Path.Combine(GetSessionDir(sessionId), "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetSessionEditsDir(string sessionId)
    {
        var dir = Path.Combine(GetSessionDir(sessionId), "edits");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void CleanTempDirectory()
    {
        if (!Directory.Exists(TempDir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(TempDir))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch
            {
                // Ignore files temporarily held by active processes.
            }
        }
    }

    public void ApplyPortableChildEnvironment(IDictionary<string, string?> env, bool redirectTemp)
    {
        env["LOCALIDEBRIDGE_SANDBOX"] = SandboxDir;
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        if (redirectTemp)
        {
            Directory.CreateDirectory(TempDir);
            env["TEMP"] = TempDir;
            env["TMP"] = TempDir;
            env["TMPDIR"] = TempDir;
        }
    }

    public static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Identifier must not be empty.", nameof(id));

        Span<char> buf = stackalloc char[Math.Min(id.Length, 80)];
        var len = 0;
        foreach (var ch in id)
        {
            if (len >= buf.Length) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                buf[len++] = ch;
        }
        if (len == 0)
            throw new ArgumentException("Identifier contained no valid ASCII characters.", nameof(id));
        return new string(buf[..len]);
    }

    public static void AtomicWriteAllText(string targetPath, string content, System.Text.Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Cannot determine parent directory for {targetPath}");
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}.tmp");
        var enc = encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, enc))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
