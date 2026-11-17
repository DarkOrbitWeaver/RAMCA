using System.Collections.Concurrent;
using System.Text.Json;

namespace LocalIdeBridge.Core;

public sealed class WorkspaceViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Enforces trusted workspace boundaries, blocks path traversal, NTFS junctions, and symlink escapes,
/// loads optional per-repository .localidebridge.json policies, and coordinates write locking.
/// </summary>
public sealed class WorkspaceGuard : IDisposable
{
    private readonly PortablePaths _paths;
    private readonly AppConfig _config;
    private readonly object _activeLock = new();
    private string? _activeWorkspace;
    private readonly AsyncLocal<string?> _invocationWorkspace = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WorkspaceGuard(PortablePaths paths, AppConfig config)
    {
        _paths = paths;
        _config = config;
        if (!string.IsNullOrWhiteSpace(config.DefaultWorkspace) && Directory.Exists(config.DefaultWorkspace))
        {
            _activeWorkspace = NormalizeRoot(config.DefaultWorkspace);
        }
        else
        {
            var first = config.TrustedWorkspaces.FirstOrDefault(w => Directory.Exists(w.Path));
            if (first is not null)
                _activeWorkspace = NormalizeRoot(first.Path);
        }
    }

    public string? ActiveWorkspace
    {
        get
        {
            if (_invocationWorkspace.Value is { } scoped) return scoped;
            lock (_activeLock) return _activeWorkspace;
        }
    }


    /// <summary>Pin the workspace for this async call and its child tasks, even when
    /// another user/client selects a new default workspace concurrently.</summary>
    public IDisposable UseInvocationWorkspace(string workspace)
    {
        var normalized = NormalizeRoot(workspace);
        ValidateTrustableDirectory(normalized);
        if (!_config.TrustedWorkspaces.Any(w =>
            string.Equals(NormalizeRoot(w.Path), normalized, PathComparison)))
            throw new WorkspaceViolationException("Operation workspace is not on the trusted-workspace list.");
        var prior = _invocationWorkspace.Value;
        _invocationWorkspace.Value = normalized;
        return new InvocationScope(_invocationWorkspace, prior);
    }

    private sealed class InvocationScope(AsyncLocal<string?> current, string? prior) : IDisposable
    {
        public void Dispose() => current.Value = prior;
    }

    public string RequireActiveWorkspace()
    {
        var ws = ActiveWorkspace;
        if (string.IsNullOrWhiteSpace(ws))
            throw new WorkspaceViolationException("No active trusted workspace selected. Configure or select a trusted workspace first.");
        if (!Directory.Exists(ws))
            throw new WorkspaceViolationException($"Active workspace directory does not exist: {ws}");
        return ws;
    }

    public string SelectWorkspace(string workspacePathOrName)
    {
        if (string.IsNullOrWhiteSpace(workspacePathOrName))
            throw new WorkspaceViolationException("Workspace path or name is required.");

        var match = _config.TrustedWorkspaces.FirstOrDefault(w =>
            string.Equals(w.Name, workspacePathOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeRoot(w.Path), SafeNormalize(workspacePathOrName), PathComparison));

        if (match is null)
            throw new WorkspaceViolationException($"Workspace '{workspacePathOrName}' is not in the trusted workspaces list.");

        var full = NormalizeRoot(match.Path);
        ValidateTrustableDirectory(full);
        if (!Directory.Exists(full))
            throw new WorkspaceViolationException($"Trusted workspace directory not found on disk: {full}");

        lock (_activeLock)
        {
            _activeWorkspace = full;
        }
        return full;
    }

    public void ValidateTrustableDirectory(string directoryPath)
    {
        var full = NormalizeRoot(directoryPath);
        if (Directory.Exists(full) &&
            (new DirectoryInfo(full).LinkTarget is not null ||
             (new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0))
            throw new WorkspaceViolationException("Workspace root cannot be a symbolic link/junction.");
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, PathComparison))
            throw new WorkspaceViolationException("Refusing to trust an entire filesystem/drive root.");

        // A workspace must never contain or be contained by the portable application root.
        // The only exception is the explicit, isolated sandbox directory.
        if (!IsEqualOrInside(full, _paths.SandboxDir) &&
            (IsEqualOrInside(full, _paths.RootDir) || IsEqualOrInside(_paths.RootDir, full)))
            throw new WorkspaceViolationException("Refusing a workspace overlapping RAMCA's portable installation or secrets.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && string.Equals(full, NormalizeRoot(home), PathComparison))
            throw new WorkspaceViolationException("Refusing to trust an entire user-profile directory.");

        string[] forbiddenPrefixes = OperatingSystem.IsWindows()
            ? [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
              ]
            : ["/etc", "/proc", "/sys", "/dev", "/boot", "/root", "/usr", "/bin", "/sbin"];

        foreach (var forbidden in forbiddenPrefixes)
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            var normForbidden = NormalizeRoot(forbidden);
            if (IsEqualOrInside(full, normForbidden))
                throw new WorkspaceViolationException($"Refusing to trust protected operating system directory: {full}");
        }
    }

    /// <summary>
    /// Resolves a relative or absolute path strictly within the active workspace, rejecting
    /// '..', drive changes, null bytes, NTFS directory junctions, and symbolic links pointing outside.
    /// </summary>
    public string ResolvePath(string userPath, bool forWrite = false, bool forDelete = false, string? explicitWorkspace = null)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new WorkspaceViolationException("Path must not be empty.");
        if (userPath.Contains('\0'))
            throw new WorkspaceViolationException("Path contains invalid null byte.");
        // NTFS alternate streams, DOS device aliases and trailing dot/space normalization must
        // not bypass protected paths. Absolute drive paths may contain ONE colon after the drive.
        if (OperatingSystem.IsWindows())
        {
            var firstColon = userPath.IndexOf(':');
            if (firstColon >= 0 && !(firstColon == 1 && char.IsAsciiLetter(userPath[0])) ||
                (firstColon >= 0 && userPath.IndexOf(':', firstColon + 1) >= 0))
                throw new WorkspaceViolationException("NTFS alternate data stream syntax is not allowed.");
            foreach (var segment in userPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue; // Ordinary . and ./ paths are legal inside the workspace.
                var trimmed = segment.TrimEnd(' ', '.');
                if (trimmed.Length == 0 || trimmed != segment)
                    throw new WorkspaceViolationException("Ambiguous Windows path with trailing dots/spaces is forbidden.");
                var device = trimmed.Split('.')[0].ToUpperInvariant();
                if (device is "CON" or "PRN" or "AUX" or "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] >= '1' && device[3] <= '9'))
                    throw new WorkspaceViolationException("Reserved Windows device paths are forbidden.");
                // NTFS 8.3 names can alias protected paths without spelling their
                // real names (for example LOCALI~1.JSO -> .localidebridge.json).
                // Reject short-name syntax until existing targets are resolved by
                // a canonical handle, including their full long names.
                if (System.Text.RegularExpressions.Regex.IsMatch(device, @"~[0-9]+$"))
                    throw new WorkspaceViolationException("Windows 8.3 short-name aliases are forbidden in tool paths.");
            }
        }

        var workspaceRoot = explicitWorkspace is not null ? NormalizeRoot(explicitWorkspace) : RequireActiveWorkspace();
        var canonicalWorkspaceRoot = ResolveRealDirectory(workspaceRoot);

        string combined;
        if (Path.IsPathRooted(userPath))
        {
            // On Windows, a drive-relative path like "\etc" or "D:foo" must still resolve inside workspaceRoot.
            combined = Path.GetFullPath(userPath);
        }
        else
        {
            combined = Path.GetFullPath(Path.Combine(canonicalWorkspaceRoot, userPath));
        }

        combined = combined.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsEqualOrInside(combined, canonicalWorkspaceRoot))
            throw new WorkspaceViolationException($"Path '{userPath}' escapes the trusted workspace boundary.");

        // Inspect every path segment for reparse points (Windows junctions or symlinks) and POSIX symlinks.
        VerifyNoReparseEscape(canonicalWorkspaceRoot, combined);

        var relPath = GetRelativePathUnix(canonicalWorkspaceRoot, combined);

        // File tools cannot access Git internal metadata even through nested worktrees/submodules.
        // Git is managed through its dedicated tools instead of arbitrary writes to hooks/config.
        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals(".git", PathComparison)))
            throw new WorkspaceViolationException("Direct access to Git metadata is forbidden; use the Git tools.");
        if (forWrite || forDelete)
        {
            if (forWrite && !_config.Autonomy.AllowFileWrites)
                throw new WorkspaceViolationException("File writes are disabled by autonomy configuration.");
            if (forDelete && !_config.Autonomy.AllowFileDeletes)
                throw new WorkspaceViolationException("File deletion is disabled by autonomy configuration.");

            if (relPath.StartsWith(".git/hooks", PathComparison) ||
                string.Equals(relPath, ".git/config", PathComparison) ||
                string.Equals(relPath, ".localidebridge.json", PathComparison))
            {
                throw new WorkspaceViolationException($"Modifying protected repository control file '{relPath}' requires manual local authorization.");
            }
            if (forDelete && string.IsNullOrEmpty(relPath))
            {
                throw new WorkspaceViolationException("Refusing to delete the workspace root directory.");
            }
        }

        // Enforce optional per-repository .localidebridge.json policy.
        var repoPolicy = LoadRepoPolicy(canonicalWorkspaceRoot);
        if (repoPolicy is not null)
        {
            if (forDelete && repoPolicy.AllowFileDeletes == false)
                throw new WorkspaceViolationException("File deletion is disabled by repository .localidebridge.json policy.");

            foreach (var denied in repoPolicy.DeniedPaths)
            {
                if (MatchesPrefix(relPath, denied))
                    throw new WorkspaceViolationException($"Path '{relPath}' is denied by repository .localidebridge.json policy.");
            }

            if (forWrite || forDelete)
            {
                foreach (var ro in repoPolicy.ReadOnlyPaths)
                {
                    if (MatchesPrefix(relPath, ro))
                        throw new WorkspaceViolationException($"Path '{relPath}' is marked read-only by repository .localidebridge.json policy.");
                }
            }
        }

        return combined;
    }

    public bool IsSafeEntryInsideWorkspace(string workspaceRoot, string entryPath)
    {
        try
        {
            var canonicalRoot = ResolveRealDirectory(workspaceRoot);
            var safe = ResolvePath(entryPath, explicitWorkspace: canonicalRoot);
            return IsEqualOrInside(safe, canonicalRoot);
        }
        catch
        {
            return false;
        }
    }

    public RepoPolicy? LoadRepoPolicy(string? workspaceRoot = null)
    {
        var root = workspaceRoot ?? ActiveWorkspace;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var policyFile = Path.Combine(root, ".localidebridge.json");
        if (!File.Exists(policyFile)) return null;

        try
        {
            var fi = new FileInfo(policyFile);
            if (fi.Length > 256 * 1024 || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceViolationException("Repository policy is oversized or a reparse point; execution refused.");
            var json = File.ReadAllText(policyFile);
            return JsonSerializer.Deserialize<RepoPolicy>(json, AppConfig.JsonOptions)
                ?? throw new WorkspaceViolationException("Repository policy is empty; execution refused.");
        }
        catch (WorkspaceViolationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new WorkspaceViolationException("Repository policy cannot be safely read or parsed; execution refused.");
        }
    }

    public async Task<IDisposable> AcquireWriteLockAsync(string workspaceRoot, CancellationToken ct)
    {
        var key = NormalizeRoot(workspaceRoot);
        var sem = _writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    public string GetRelativePathUnix(string workspaceRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(workspaceRoot, fullPath);
        if (rel == ".") return "";
        return rel.Replace('\\', '/');
    }

    private static void VerifyNoReparseEscape(string canonicalWorkspaceRoot, string fullCandidatePath)
    {
        var relative = Path.GetRelativePath(canonicalWorkspaceRoot, fullCandidatePath);
        if (relative == ".") return;

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = canonicalWorkspaceRoot;

        foreach (var seg in segments)
        {
            if (seg == "..")
                throw new WorkspaceViolationException("Path traversal detected.");

            current = Path.Combine(current, seg);
            FileSystemInfo? fsi = null;
            if (Directory.Exists(current))
                fsi = new DirectoryInfo(current);
            else if (File.Exists(current))
                fsi = new FileInfo(current);
            else
            {
                // Even dangling symlinks exist as reparse points; check via FileInfo attributes.
                var probe = new FileInfo(current);
                if (probe.LinkTarget is not null)
                    fsi = probe;
            }

            if (fsi is null)
                break; // Remaining segments do not exist yet (e.g., creating a new file/folder).

            var isReparse = fsi.LinkTarget is not null ||
                            (fsi.Exists && (fsi.Attributes & FileAttributes.ReparsePoint) != 0);
            if (!isReparse)
                continue;

            // Even an in-workspace link can alias `.git`, a denied path or create a cycle.
            // Reject every reparse point until a canonical-handle-based alias policy exists.
            throw new WorkspaceViolationException($"Symbolic links and junctions cannot be traversed by file tools: '{current}'.");
        }
    }

    private static string ResolveRealDirectory(string dirPath)
    {
        var di = new DirectoryInfo(dirPath);
        if (di.Exists && (di.LinkTarget is not null || (di.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            var resolved = di.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                return NormalizeRoot(resolved.FullName);
        }
        return NormalizeRoot(di.FullName);
    }

    public static bool IsEqualOrInside(string candidate, string root)
    {
        var normCandidate = NormalizeRoot(candidate);
        var normRoot = NormalizeRoot(root);
        if (string.Equals(normCandidate, normRoot, PathComparison))
            return true;

        var prefix = normRoot + Path.DirectorySeparatorChar;
        return normCandidate.StartsWith(prefix, PathComparison);
    }

    private static bool MatchesPrefix(string relPath, string pattern)
    {
        var clean = pattern.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(clean)) return false;
        return string.Equals(relPath, clean, PathComparison) ||
               relPath.StartsWith(clean + "/", PathComparison);
    }

    public static string NormalizeRoot(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? SafeNormalize(string path)
    {
        try { return NormalizeRoot(path); }
        catch { return null; }
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                sem.Release();
        }
    }

    public void Dispose()
    {
        foreach (var kv in _writeLocks)
            kv.Value.Dispose();
        _writeLocks.Clear();
    }
}
