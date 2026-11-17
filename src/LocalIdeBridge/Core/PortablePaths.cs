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
        // Short staging filename avoids MAX_PATH component overflow for long source names.
        var tmpPath = Path.Combine(dir, $".ramca-{Guid.NewGuid():N}.tmp");
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(targetPath))
            unixMode = File.GetUnixFileMode(targetPath);
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
            if (unixMode is not null)
                File.SetUnixFileMode(tmpPath, unixMode.Value);
            if (OperatingSystem.IsWindows() && File.Exists(targetPath))
                File.Replace(tmpPath, targetPath, null);
            else
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
    /// <summary>
    /// Restore a verified snapshot without materializing a 256 MiB backup in RAM.
    /// Caller checks the snapshot SHA-256; the target is replaced only after the
    /// temporary copy has been fully flushed to storage on the same volume.
    /// </summary>
    public static void AtomicRestoreBackupFile(string backupPath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? throw new IOException("Missing target directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ramca-{Guid.NewGuid():N}.tmp");
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(targetPath))
            unixMode = File.GetUnixFileMode(targetPath);
        try
        {
            using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                source.CopyTo(destination, 81920);
                destination.Flush(flushToDisk: true);
            }
            if (unixMode is not null) File.SetUnixFileMode(temporary, unixMode.Value);
            if (OperatingSystem.IsWindows() && File.Exists(targetPath))
                File.Replace(temporary, targetPath, null);
            else
                File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public static void AtomicWriteAllBytes(string targetPath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("No target directory.");
        Directory.CreateDirectory(dir);
        var temporary = Path.Combine(dir, $".ramca-{Guid.NewGuid():N}.tmp");
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(targetPath))
            unixMode = File.GetUnixFileMode(targetPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (unixMode is not null) File.SetUnixFileMode(temporary, unixMode.Value);
            if (OperatingSystem.IsWindows() && File.Exists(targetPath))
                File.Replace(temporary, targetPath, null);
            else
                File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
