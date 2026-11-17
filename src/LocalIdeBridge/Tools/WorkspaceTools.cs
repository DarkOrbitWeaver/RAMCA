using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

public static class WorkspaceTools
{
    public static readonly HashSet<string> DefaultIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "TestResults", ".next", "dist"
    };

    public static IEnumerable<ToolDefinition> Create(
        PortablePaths paths,
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        ProcessRunner runner,
        ToolRegistry registry)
    {
        yield return new ToolDefinition(
            Name: "workspace_list",
            Category: "workspace",
            Description: "Lists all trusted workspaces configured in LocalIdeBridge and shows which workspace is currently active.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters: [],
            Handler: (_, _, _) =>
            {
                var active = guard.ActiveWorkspace;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var items = config.TrustedWorkspaces.Select(w =>
                {
                    var norm = WorkspaceGuard.NormalizeRoot(w.Path);
                    return new
                    {
                        name = w.Name,
                        path = norm,
                        exists = Directory.Exists(norm),
                        isActive = active is not null && string.Equals(norm, active, comparison),
                        isDefault = config.DefaultWorkspace is not null && string.Equals(norm, WorkspaceGuard.NormalizeRoot(config.DefaultWorkspace), comparison)
                    };
                }).ToList();

                return Task.FromResult(ToolResult.Ok(
                    $"Found {items.Count} trusted workspace(s).",
                    new { activeWorkspace = active, workspaces = items }));
            });

        yield return new ToolDefinition(
            Name: "workspace_select",
            Category: "workspace",
            Description: "Switches the active workspace to one of the trusted workspaces configured by the local user.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Changes the active workspace for subsequent tool calls in this host.",
            Parameters:
            [
                new("workspace", "string", "Path or configured alias of a trusted workspace.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var target = args.RequireString("workspace");
                var selected = guard.SelectWorkspace(target);
                return Task.FromResult(ToolResult.Ok(
                    $"Selected workspace '{selected}'.",
                    new { activeWorkspace = selected, sessionId = db.ActiveSessionId }));
            });

        yield return new ToolDefinition(
            Name: "workspace_status",
            Category: "workspace",
            Description: "Returns active workspace details, current Git branch and dirty summary, active session info, supervisor steering note, and autonomy policy summary.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters: [],
            Handler: async (_, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var session = db.GetSession(db.ActiveSessionId);
                var repoPolicy = guard.LoadRepoPolicy(ws);

                string? gitBranch = null;
                int dirtyCount = 0;
                bool isGitRepo = Directory.Exists(Path.Combine(ws, ".git")) || File.Exists(Path.Combine(ws, ".git"));
                if (isGitRepo)
                {
                    try
                    {
                        var br = await runner.RunAsync("git", "git", ["symbolic-ref", "--quiet", "--short", "HEAD"], ws, timeoutSeconds: 10, ct: ct, stdoutCaptureLimit: 5000);
                        if (br.ExitCode != 0 || br.StdoutCaptureTruncated || string.IsNullOrWhiteSpace(br.StdoutCapture))
                        {
                            // Detached HEAD has no symbolic ref; unborn HEAD *does*.
                            var detached = await runner.RunAsync("git", "git", ["rev-parse", "--short", "HEAD"], ws, timeoutSeconds: 10, ct: ct, stdoutCaptureLimit: 5000);
                            if (detached.ExitCode != 0 || detached.StdoutCaptureTruncated || string.IsNullOrWhiteSpace(detached.StdoutCapture))
                                return ToolResult.Fail("git_status_unavailable", "Could not determine Git branch or detached HEAD reliably.");
                            gitBranch = "HEAD@" + detached.StdoutCapture.Trim();
                        }
                        else gitBranch = br.StdoutCapture.Trim();

                        var st = await runner.RunAsync("git", "git", ["status", "--porcelain=v1"], ws, timeoutSeconds: 10, ct: ct, stdoutCaptureLimit: 2_000_000);
                        if (st.ExitCode != 0 || st.StdoutCaptureTruncated || st.StdoutCapture is null)
                            return ToolResult.Fail("git_status_incomplete", "Git status failed or exceeded bounded output; repository cleanliness is unknown.");
                        dirtyCount = st.StdoutCapture.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return ToolResult.Fail("git_status_unavailable", $"Git status could not be determined: {ex.Message}");
                    }
                }

                return ToolResult.Ok(
                    $"Workspace '{Path.GetFileName(ws)}' active (git={(gitBranch ?? "none")}, dirty={dirtyCount}).",
                    new
                    {
                        workspace = ws,
                        isGitRepository = isGitRepo,
                        gitBranch,
                        modifiedOrUntrackedCount = dirtyCount,
                        sessionId = session?.Id ?? db.ActiveSessionId,
                        sessionToolCalls = session?.ToolCallCount ?? 0,
                        sessionErrors = session?.ErrorCount ?? 0,
                        supervisorSteeringNote = session?.SteeringNote,
                        executionPaused = registry.IsPaused,
                        hasRepoPolicy = repoPolicy is not null,
                        autonomy = new
                        {
                            config.Autonomy.AllowFileWrites,
                            config.Autonomy.AllowFileDeletes,
                            config.Autonomy.AllowCommands,
                            config.Autonomy.AllowGitCommits,
                            gitPushPolicy = repoPolicy?.AllowGitPush == false ? "deny" : config.Autonomy.GitPushPolicy
                        }
                    });
            });

        yield return new ToolDefinition(
            Name: "repo_tree",
            Category: "code_intel",
            Description: "Inspects the directory and file tree within the active workspace with bounded entry count and optional depth limit.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("path", "string", "Relative subpath inside the workspace (defaults to workspace root).", Default: "."),
                new("maxDepth", "integer", "Maximum recursion depth (1-12).", Default: 4),
                new("maxEntries", "integer", "Maximum entries returned (1-1000).", Default: 250),
                new("includeIgnored", "boolean", "Include build/cache folders such as bin, obj, node_modules, .git.", Default: false)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var sub = args.OptionalString("path", ".")!;
                var startDir = sub == "." ? ws : guard.ResolvePath(sub);
                if (!Directory.Exists(startDir))
                    throw new DirectoryNotFoundException($"Directory not found: {sub}");

                var maxDepth = Math.Clamp(args.OptionalInt("maxDepth", 4), 1, 12);
                var maxEntries = Math.Clamp(args.OptionalInt("maxEntries", 250), 1, 1000);
                var includeIgnored = args.OptionalBool("includeIgnored", false);

                var entries = new List<object>();
                var truncated = false;

                void Walk(string dir, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if (depth > maxDepth || truncated) return;

                    IEnumerable<string> children;
                    try
                    {
                        children = Directory.EnumerateFileSystemEntries(dir)
                            .OrderBy(p => !Directory.Exists(p))
                            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                    }
                    catch { return; }

                    foreach (var child in children)
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(child);
                        var isDir = Directory.Exists(child);
                        if (!includeIgnored && isDir && DefaultIgnoredDirs.Contains(name))
                            continue;
                        if (!guard.IsSafeEntryInsideWorkspace(ws, child))
                            continue;

                        if (entries.Count >= maxEntries)
                        {
                            truncated = true;
                            return;
                        }

                        var rel = guard.GetRelativePathUnix(ws, child);
                        if (isDir)
                        {
                            entries.Add(new { path = rel, type = "directory" });
                            if (depth < maxDepth)
                                Walk(child, depth + 1);
                        }
                        else
                        {
                            long size = 0;
                            try { size = new FileInfo(child).Length; } catch { }
                            entries.Add(new { path = rel, type = "file", sizeBytes = size });
                        }
                    }
                }

                Walk(startDir, 1);
                return Task.FromResult(ToolResult.Ok(
                    $"Listed {entries.Count} tree entries{(truncated ? " (truncated)" : "")}.",
                    new { root = guard.GetRelativePathUnix(ws, startDir), count = entries.Count, truncated, entries }));
            });

        yield return new ToolDefinition(
            Name: "filename_search",
            Category: "code_intel",
            Description: "Searches filenames and relative paths in the workspace by wildcard glob (e.g. '*.cs', '**/*Test*.cs') or case-insensitive substring.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("pattern", "string", "Filename substring or wildcard pattern (* and ? supported).", Required: true),
                new("maxResults", "integer", "Maximum matches to return (1-500).", Default: 100),
                new("includeIgnored", "boolean", "Include ignored directories like bin/obj/node_modules.", Default: false)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var pattern = args.RequireString("pattern");
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 100), 1, 500);
                var includeIgnored = args.OptionalBool("includeIgnored", false);
                var regex = PatternToMatcher(pattern);

                var matches = new List<object>();
                var truncated = false;

                foreach (var file in EnumerateWorkspaceFiles(ws, guard, includeIgnored, ct))
                {
                    var rel = guard.GetRelativePathUnix(ws, file);
                    var name = Path.GetFileName(file);
                    if (regex is not null ? (regex.IsMatch(name) || regex.IsMatch(rel))
                        : (name.Contains(pattern, StringComparison.OrdinalIgnoreCase) || rel.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (matches.Count >= maxResults)
                        {
                            truncated = true;
                            break;
                        }
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }
                        matches.Add(new { path = rel, sizeBytes = size });
                    }
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Found {matches.Count} matching file(s){(truncated ? " (truncated)" : "")}.",
                    new { pattern, count = matches.Count, truncated, matches }));
            });

        yield return new ToolDefinition(
            Name: "create_directory",
            Category: "editing",
            Description: "Creates a directory (and any missing parent directories) inside the trusted workspace.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Creates directory on disk inside the active workspace.",
            Parameters:
            [
                new("path", "string", "Relative directory path inside the active workspace.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var full = guard.ResolvePath(args.RequireString("path"), forWrite: true);
                var existed = Directory.Exists(full);
                Directory.CreateDirectory(full);
                var rel = guard.GetRelativePathUnix(ws, full);
                return Task.FromResult(ToolResult.Ok(
                    existed ? $"Directory '{rel}' already exists." : $"Created directory '{rel}'.",
                    new { path = rel, created = !existed }));
            });

        yield return new ToolDefinition(
            Name: "tool_doctor",
            Category: "workspace",
            Description: "Verifies installed developer toolchains (dotnet, git, PowerShell, MSBuild, optional tunnels) and reports NAT/CGNAT/TLS network diagnostics.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "Runs quick --version checks on installed tools.",
            Parameters: [],
            Handler: (_, _, _) =>
            {
                var tools = ProcessRunner.ProbeInstalledTools(paths);
                var net = TunnelManager.DiagnoseNetwork(config, checkPortBind: false);
                return Task.FromResult(ToolResult.Ok(
                    "Completed developer toolchain and connectivity diagnostics.",
                    new
                    {
                        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                        dotnetRuntime = Environment.Version.ToString(),
                        portableRoot = paths.RootDir,
                        installedTools = tools,
                        network = net
                    }));
            });
    }

    public static IEnumerable<string> EnumerateWorkspaceFiles(
        string workspaceRoot,
        WorkspaceGuard guard,
        bool includeIgnored,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(workspaceRoot);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (guard.IsSafeEntryInsideWorkspace(workspaceRoot, f))
                    yield return f;
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
            for (var i = subDirs.Length - 1; i >= 0; i--)
            {
                var sd = subDirs[i];
                var name = Path.GetFileName(sd);
                if (!includeIgnored && DefaultIgnoredDirs.Contains(name))
                    continue;
                if (guard.IsSafeEntryInsideWorkspace(workspaceRoot, sd))
                    stack.Push(sd);
            }
        }
    }

    public static Regex? PatternToMatcher(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return null;
        var norm = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(norm)
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string? FirstCleanOutputLine(string excerpt)
    {
        foreach (var line in excerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("[out] ", StringComparison.Ordinal))
                return line[6..].Trim();
        }
        return null;
    }
}
