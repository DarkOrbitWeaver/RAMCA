using System.Diagnostics;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Reliable, cancellable wrappers for PowerShell/shell commands, dotnet, MSBuild, test runners,
/// Git operations (status, diff, branch, worktree, stage, commit, checkpoint, policy-gated push),
/// process diagnostics, and paginated execution log retrieval.
/// </summary>
public static class BuildGitCommandTools
{
    public static IEnumerable<ToolDefinition> Create(
        PortablePaths paths,
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        LogStore logs,
        ProcessRunner runner,
        ChildProcessSupervisor supervisor)
    {
        yield return new ToolDefinition(
            Name: "run_command",
            Category: "execution",
            Description: "Executes an approved developer executable or PowerShell command inside the trusted workspace with timeout, cancellation, structured metadata, and on-demand log reference.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Executes a supervised child process inside the workspace (terminated automatically on timeout, cancellation, or host exit).",
            Parameters:
            [
                new("command", "string", "PowerShell script/command text (when executable is omitted) or executable name/workspace-relative binary path.", Required: true),
                new("arguments", "array", "Optional argument array when invoking an executable directly instead of a shell script.", ItemsSchema: new JsonObject { ["type"] = "string" }),
                new("useShell", "boolean", "Execute 'command' via PowerShell (pwsh/powershell on Windows, pwsh/bash fallback elsewhere).", Default: false),
                new("allowWorkspaceExecutable", "boolean", "Permit executing a compiled binary located strictly inside the active workspace.", Default: false),
                new("workingDirectory", "string", "Optional relative subdirectory inside the active workspace."),
                new("timeoutSeconds", "integer", "Execution timeout in seconds (clamped to config resources.maxCommandTimeoutSeconds).")
            ],
            Handler: async (args, _, ct) =>
            {
                var command = args.RequireString("command");
                var argList = args.OptionalStringList("arguments");
                var useShell = args.OptionalBool("useShell", false);
                var allowWsExe = args.OptionalBool("allowWorkspaceExecutable", false);
                var workDir = args.OptionalString("workingDirectory");
                int? timeout = args.ContainsKey("timeoutSeconds") ? args.OptionalInt("timeoutSeconds", config.Resources.DefaultCommandTimeoutSeconds) : null;

                string exe;
                IReadOnlyList<string> finalArgs;
                string category;

                if (useShell)
                {
                    exe = runner.ResolvePreferredShell();
                    finalArgs = runner.BuildShellArguments(exe, command);
                    category = "shell";
                }
                else
                {
                    exe = command;
                    finalArgs = argList;
                    category = "command";
                }

                var res = await runner.RunAsync(category, exe, finalArgs, workDir, timeout, parseCompilerDiagnostics: true, allowWorkspaceExecutable: allowWsExe, ct: ct);
                return FormatCommandResult(res);
            });

        yield return new ToolDefinition(
            Name: "dotnet_build",
            Category: "build",
            Description: "Builds a .NET project or solution inside the trusted workspace and returns structured compiler diagnostics plus log retrieval reference.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Compiles build outputs inside the workspace.",
            Parameters:
            [
                new("target", "string", "Optional relative .sln/.csproj path (defaults to workspace directory)."),
                new("configuration", "string", "Build configuration (Debug or Release).", Default: "Debug"),
                new("noRestore", "boolean", "Skip automatic NuGet restore.", Default: false),
                new("extraArgs", "array", "Optional additional dotnet build flags.", ItemsSchema: new JsonObject { ["type"] = "string" }),
                new("timeoutSeconds", "integer", "Optional build timeout in seconds.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var cmdArgs = new List<string> { "build", "--nologo" };
                var target = args.OptionalString("target");
                if (!string.IsNullOrWhiteSpace(target))
                    cmdArgs.Add(guard.ResolvePath(target));

                cmdArgs.Add("-c");
                cmdArgs.Add(args.OptionalString("configuration", "Debug")!);
                if (args.OptionalBool("noRestore", false))
                    cmdArgs.Add("--no-restore");
                foreach (var extra in args.OptionalStringList("extraArgs"))
                    cmdArgs.Add(extra);

                int? timeout = args.ContainsKey("timeoutSeconds") ? args.OptionalInt("timeoutSeconds", config.Resources.DefaultCommandTimeoutSeconds) : null;
                var res = await runner.RunAsync("build", "dotnet", cmdArgs, ws, timeout, parseCompilerDiagnostics: true, ct: ct);

                var data = BuildCommandPayload(res, new
                {
                    errorCount = res.Diagnostics.Count(d => d.Severity == "error"),
                    warningCount = res.Diagnostics.Count(d => d.Severity == "warning")
                });

                return res.ExitCode == 0
                    ? ToolResult.Ok($"dotnet build succeeded in {res.DurationMs}ms ({res.Diagnostics.Count} warning(s)).", data, res.ExecutionId)
                    : ToolResult.Fail(res.TimedOut ? "timeout" : (res.Cancelled ? "cancelled" : "build_failed"),
                        $"dotnet build failed with exit code {res.ExitCode} ({res.Diagnostics.Count(d => d.Severity == "error")} error(s)).", data, res.ExecutionId);
            });

        yield return new ToolDefinition(
            Name: "msbuild_run",
            Category: "build",
            Description: "Invokes MSBuild (standalone Visual Studio MSBuild when installed, or 'dotnet msbuild' fallback) with structured compiler diagnostics.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Executes MSBuild targets inside the workspace.",
            Parameters:
            [
                new("project", "string", "Optional relative project or solution file."),
                new("target", "string", "MSBuild target name (e.g. Build, Rebuild, Clean).", Default: "Build"),
                new("configuration", "string", "Configuration property value.", Default: "Debug"),
                new("timeoutSeconds", "integer", "Optional timeout in seconds.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var proj = args.OptionalString("project");
                var target = args.OptionalString("target", "Build")!;
                var cfg = args.OptionalString("configuration", "Debug")!;
                int? timeout = args.ContainsKey("timeoutSeconds") ? args.OptionalInt("timeoutSeconds", config.Resources.DefaultCommandTimeoutSeconds) : null;

                var hasStandalone = ProcessRunner.FindOnPath("msbuild") is not null;
                var cmdArgs = new List<string>();
                if (!hasStandalone) cmdArgs.Add("msbuild");
                cmdArgs.Add("-nologo");
                if (!string.IsNullOrWhiteSpace(proj))
                    cmdArgs.Add(guard.ResolvePath(proj));
                cmdArgs.Add($"-t:{target}");
                cmdArgs.Add($"-p:Configuration={cfg}");

                var res = await runner.RunAsync("msbuild", hasStandalone ? "msbuild" : "dotnet", cmdArgs, ws, timeout, parseCompilerDiagnostics: true, ct: ct);
                return FormatCommandResult(res);
            });

        yield return new ToolDefinition(
            Name: "dotnet_test",
            Category: "test",
            Description: "Runs 'dotnet test' with TRX structured reporting, returning exact total, passed, failed, skipped counts, failure stack traces, and log retrieval ID.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Builds (unless noBuild=true) and runs tests in workspace; writes temporary TRX inside LocalIdeBridge/temp.",
            Parameters:
            [
                new("target", "string", "Optional relative test project or solution path."),
                new("filter", "string", "Optional VSTest --filter expression for focused test runs."),
                new("configuration", "string", "Configuration (Debug or Release).", Default: "Debug"),
                new("noBuild", "boolean", "Skip build step before running tests.", Default: false),
                new("timeoutSeconds", "integer", "Optional test run timeout in seconds.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                Directory.CreateDirectory(paths.TempDir);
                var trxDir = Path.Combine(paths.TempDir, $"test_{Guid.NewGuid():N}");
                Directory.CreateDirectory(trxDir);

                var cmdArgs = new List<string> { "test", "--nologo" };
                var target = args.OptionalString("target");
                if (!string.IsNullOrWhiteSpace(target))
                    cmdArgs.Add(guard.ResolvePath(target));

                cmdArgs.Add("-c");
                cmdArgs.Add(args.OptionalString("configuration", "Debug")!);
                if (args.OptionalBool("noBuild", false))
                    cmdArgs.Add("--no-build");

                var filter = args.OptionalString("filter");
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    cmdArgs.Add("--filter");
                    cmdArgs.Add(filter);
                }

                cmdArgs.Add("--logger");
                cmdArgs.Add("trx");
                cmdArgs.Add("--results-directory");
                cmdArgs.Add(trxDir);

                int? timeout = args.ContainsKey("timeoutSeconds") ? args.OptionalInt("timeoutSeconds", config.Resources.DefaultCommandTimeoutSeconds) : null;
                CommandExecutionResult res;
                TestRunSummary summary;
                int reportFiles;
                try
                {
                    res = await runner.RunAsync("test", "dotnet", cmdArgs, ws, timeout, parseCompilerDiagnostics: true, ct: ct);
                    var reports = ProcessRunner.ParseTrxDirectory(trxDir);
                    summary = reports.Summary;
                    reportFiles = reports.ReportFiles;
                }
                finally
                {
                    try { Directory.Delete(trxDir, recursive: true); } catch { }
                }

                var payload = new
                {
                    executionId = res.ExecutionId,
                    category = res.Category,
                    startedAtUtc = res.StartedAtUtc,
                    completedAtUtc = res.CompletedAtUtc,
                    durationMs = res.DurationMs,
                    exitCode = res.ExitCode,
                    timedOut = res.TimedOut,
                    cancelled = res.Cancelled,
                    total = summary.Total,
                    reportFiles,
                    countsVerified = reportFiles > 0,
                    passed = summary.Passed,
                    failed = summary.Failed,
                    skipped = summary.Skipped,
                    failures = summary.Failures,
                    compilerDiagnostics = res.Diagnostics,
                    outputExcerpt = res.OutputExcerpt,
                    logReference = new { execId = res.ExecutionId, totalLines = res.TotalLogLines, totalBytes = res.TotalLogBytes }
                };

                if (res.ExitCode == 0 && (reportFiles == 0 || summary.Total == 0))
                    return ToolResult.Fail("test_results_missing", "dotnet test exited 0 but produced no test counts; cannot verify tests actually executed.", payload, res.ExecutionId);

                return res.ExitCode == 0
                    ? ToolResult.Ok($"Tests passed: {summary.Passed}/{summary.Total} passed, {summary.Failed} failed, {summary.Skipped} skipped ({res.DurationMs}ms).", payload, res.ExecutionId)
                    : ToolResult.Fail(res.TimedOut ? "timeout" : (res.Cancelled ? "cancelled" : "tests_failed"),
                        $"Test run failed (exit={res.ExitCode}): {summary.Passed} passed, {summary.Failed} failed, {summary.Skipped} skipped.", payload, res.ExecutionId);
            });

        yield return new ToolDefinition(
            Name: "read_log",
            Category: "execution",
            Description: "Retrieves a paginated line range from a stored command, build, or test execution log on disk without loading full logs into memory.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("execId", "string", "Execution identifier returned by run_command, dotnet_build, msbuild_run, or dotnet_test.", Required: true),
                new("startLine", "integer", "1-based line number to begin reading from.", Default: 1),
                new("maxLines", "integer", "Maximum lines to return (1-500).", Default: 120),
                new("contains", "string", "Optional case-insensitive substring filter (e.g. 'error').")
            ],
            Handler: (args, _, _) =>
            {
                var slice = logs.ReadRange(
                    args.RequireString("execId"),
                    args.OptionalInt("startLine", 1),
                    args.OptionalInt("maxLines", 120),
                    args.OptionalString("contains"));

                return Task.FromResult(ToolResult.Ok(
                    $"Read {slice.ReturnedLines} log line(s) from '{slice.ExecutionId}' (lines {slice.StartLine}..{slice.EndLine} of {slice.TotalLines}).",
                    slice));
            });

        yield return new ToolDefinition(
            Name: "process_diagnostics",
            Category: "execution",
            Description: "Inspects LocalIdeBridge host resource usage (working set, GC managed heap, threads, uptime, Windows Job Object state) and active owned child processes.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters: [],
            Handler: (_, _, _) =>
            {
                using var self = Process.GetCurrentProcess();
                var gcInfo = GC.GetGCMemoryInfo();
                var activeChildren = supervisor.SnapshotActiveProcesses();
                return Task.FromResult(ToolResult.Ok(
                    $"Host RSS={Math.Round(self.WorkingSet64 / (1024.0 * 1024.0), 1)} MB, managed={Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1)} MB, activeChildren={activeChildren.Count}.",
                    new
                    {
                        hostPid = self.Id,
                        workingSetMb = Math.Round(self.WorkingSet64 / (1024.0 * 1024.0), 2),
                        privateMemoryMb = Math.Round(self.PrivateMemorySize64 / (1024.0 * 1024.0), 2),
                        managedHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2),
                        gcHeapSizeMb = Math.Round(gcInfo.HeapSizeBytes / (1024.0 * 1024.0), 2),
                        gen0Collections = GC.CollectionCount(0),
                        gen1Collections = GC.CollectionCount(1),
                        gen2Collections = GC.CollectionCount(2),
                        threadCount = self.Threads.Count,
                        totalCpuSeconds = Math.Round(self.TotalProcessorTime.TotalSeconds, 3),
                        uptimeSeconds = Math.Round((DateTime.UtcNow - self.StartTime.ToUniversalTime()).TotalSeconds, 1),
                        windowsJobObjectActive = supervisor.IsWindowsJobObjectActive,
                        activeChildProcesses = activeChildren
                    }));
            });

        // ── Git Tools ──────────────────────────────────────────────────────────

        yield return new ToolDefinition(
            Name: "git_status",
            Category: "git",
            Description: "Returns structured Git branch, modified/staged/untracked file entries, and recent local commits for the active workspace.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("recentCommits", "integer", "Number of recent commits to include (0-25).", Default: 5)
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var statusRes = await runner.RunAsync("git", "git", ["status", "--porcelain=v1", "-b", "-z"], ws, timeoutSeconds: 20, ct: ct, stdoutCaptureLimit: 2_000_000);
                if (statusRes.ExitCode != 0)
                    return FormatCommandResult(statusRes);

                if (statusRes.StdoutCaptureTruncated || statusRes.StdoutCapture is null)
                    return ToolResult.Fail("status_incomplete", "Git status output exceeded the bounded capture or was interrupted. Nothing about repository cleanliness is assumed; inspect the execution log.", new { executionId = statusRes.ExecutionId });
                var fields = statusRes.StdoutCapture.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var branchLine = "unknown";
                var files = new List<object>();
                for (var i = 0; i < fields.Length; i++)
                {
                    var entry = fields[i].TrimEnd('\r', '\n');
                    if (entry.StartsWith("## ", StringComparison.Ordinal))
                    {
                        branchLine = entry[3..];
                        continue;
                    }
                    if (entry.Length < 4 || entry[2] != ' ') continue;
                    var xy = entry[..2];
                    var path = entry[3..]; // -z mode: unquoted, exact spaces/newlines.
                    string? originalPath = null;
                    if ((xy[0] is 'R' or 'C' || xy[1] is 'R' or 'C') && i + 1 < fields.Length)
                        originalPath = fields[++i]; // -z rename outputs new path, then old.
                    files.Add(new
                    {
                        status = xy.Trim(),
                        indexStatus = xy[0].ToString(),
                        workTreeStatus = xy[1].ToString(),
                        staged = xy[0] is not ' ' and not '?',
                        unstaged = xy[1] is not ' ' and not '?',
                        path,
                        originalPath
                    });
                }

                var commitLimit = Math.Clamp(args.OptionalInt("recentCommits", 5), 0, 25);
                var commits = new List<object>();
                if (commitLimit > 0)
                {
                    var logRes = await runner.RunAsync("git", "git", ["log", $"-n{commitLimit}", "--pretty=format:%H|%h|%an|%aI|%s"], ws, timeoutSeconds: 15, ct: ct, stdoutCaptureLimit: 50000);
                    if (logRes.ExitCode == 0 && !logRes.StdoutCaptureTruncated && logRes.StdoutCapture is not null)
                    {
                        foreach (var cl in logRes.StdoutCapture.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = cl.Split('|', 5);
                            if (parts.Length == 5)
                                commits.Add(new { sha = parts[0], shortSha = parts[1], author = parts[2], date = parts[3], subject = parts[4] });
                        }
                    }
                }

                return ToolResult.Ok(
                    $"Git branch '{branchLine}', {files.Count} changed/untracked file(s).",
                    new { branch = branchLine, changedCount = files.Count, files, recentCommits = commits },
                    statusRes.ExecutionId);
            });

        yield return new ToolDefinition(
            Name: "git_diff",
            Category: "git",
            Description: "Compares working-tree or staged changes against Git HEAD (or a specified revision/path).",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("staged", "boolean", "Inspect staged (--cached) diff instead of unstaged changes.", Default: false),
                new("revision", "string", "Optional commit/branch ref to compare against (e.g. 'HEAD~1')."),
                new("path", "string", "Optional relative file path inside workspace.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var gitArgs = new List<string> { "diff", "--no-color" };
                if (args.OptionalBool("staged", false)) gitArgs.Add("--cached");
                var rev = args.OptionalString("revision");
                if (!string.IsNullOrWhiteSpace(rev) && !rev.StartsWith('-')) gitArgs.Add(rev);
                var path = args.OptionalString("path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var full = guard.ResolvePath(path);
                    gitArgs.Add("--");
                    gitArgs.Add(guard.GetRelativePathUnix(ws, full));
                }

                var res = await runner.RunAsync("git", "git", gitArgs, ws, timeoutSeconds: 30, ct: ct, stdoutCaptureLimit: 512_000);
                if (res.ExitCode != 0) return FormatCommandResult(res);

                if (res.StdoutCaptureTruncated || res.StdoutCapture is null)
                    return ToolResult.Fail("diff_incomplete",
                        "Git diff exceeded the bounded capture or was interrupted. Read the complete paginated execution log rather than trusting a truncated diff.",
                        new { partialDiff = res.StdoutCapture, truncated = true, executionId = res.ExecutionId, totalLogLines = res.TotalLogLines }, res.ExecutionId);

                return ToolResult.Ok(
                    $"Retrieved complete Git diff ({res.TotalLogLines} log line(s)).",
                    new { diff = res.StdoutCapture, truncated = false, executionId = res.ExecutionId, totalLogLines = res.TotalLogLines },
                    res.ExecutionId);
            });

        yield return new ToolDefinition(
            Name: "git_branch",
            Category: "git",
            Description: "Lists branches or initializes main; creating/switching branches requires local owner authorization (autonomy.allowGitBranchChanges).",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Initializes repo or creates/switches local Git branch inside workspace.",
            Parameters:
            [
                new("action", "string", "list | create | switch | create_and_switch | init", EnumValues: ["list", "create", "switch", "create_and_switch", "init"], Default: "list"),
                new("name", "string", "Branch name (required for create/switch/create_and_switch; optional initial branch for init)."),
                new("startPoint", "string", "Optional starting commit or branch when creating.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var action = args.OptionalString("action", "list")!.ToLowerInvariant();
                var name = args.OptionalString("name");
                var startPoint = args.OptionalString("startPoint");

                if (name is not null && name.StartsWith('-'))
                    throw new ArgumentException("Branch name must not start with '-'.");

                if (action is "create" or "switch" or "create_and_switch" && !config.Autonomy.AllowGitBranchChanges)
                    throw new WorkspaceViolationException("Branch creation/switching is disabled by the owner's main-only policy; change autonomy.allowGitBranchChanges locally for an explicit exception.");
                if (action == "init" && !string.IsNullOrWhiteSpace(name) && !name.Equals("main", StringComparison.OrdinalIgnoreCase) &&
                    !config.Autonomy.AllowGitBranchChanges)
                    throw new WorkspaceViolationException("New Git repositories must initialize on main under the owner's default policy.");

                List<string> gitArgs = action switch
                {
                    "list" => ["branch", "--list", "-vv"],
                    "init" => ["init", "-b", string.IsNullOrWhiteSpace(name) ? "main" : name],
                    "create" => BuildBranchArgs(["branch", RequireBranch(name)], startPoint),
                    "switch" => ["switch", RequireBranch(name)],
                    "create_and_switch" => BuildBranchArgs(["switch", "-c", RequireBranch(name)], startPoint),
                    _ => throw new ArgumentException($"Unsupported git_branch action '{action}'.")
                };

                var res = await runner.RunAsync("git", "git", gitArgs, ws, timeoutSeconds: 30, ct: ct);
                return FormatCommandResult(res);
            });

        yield return new ToolDefinition(
            Name: "git_worktree",
            Category: "git",
            Description: "Lists, creates, or removes local Git worktrees confined to the active workspace or LocalIdeBridge/sandbox.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Creates or removes a Git worktree directory.",
            Parameters:
            [
                new("action", "string", "list | add | remove", EnumValues: ["list", "add", "remove"], Default: "list"),
                new("name", "string", "Worktree folder name (created under LocalIdeBridge/sandbox/worktrees/<name>) or workspace-relative path."),
                new("branch", "string", "Branch name to create (-b) or checkout when adding a worktree.")
            ],
            Handler: async (args, _, ct) =>
            {
                if (!config.Autonomy.AllowGitWorktrees)
                    throw new WorkspaceViolationException("Git worktrees are disabled in autonomy settings.");

                var ws = guard.RequireActiveWorkspace();
                var action = args.OptionalString("action", "list")!.ToLowerInvariant();

                if (action == "list")
                {
                    var listRes = await runner.RunAsync("git", "git", ["worktree", "list"], ws, timeoutSeconds: 20, ct: ct);
                    return FormatCommandResult(listRes);
                }

                var rawName = args.RequireString("name");
                var safeName = PortablePaths.SanitizeId(rawName);
                var wtBase = Path.Combine(paths.SandboxDir, "worktrees");
                Directory.CreateDirectory(wtBase);
                var wtPath = Path.GetFullPath(Path.Combine(wtBase, safeName));
                if (!WorkspaceGuard.IsEqualOrInside(wtPath, wtBase))
                    throw new WorkspaceViolationException("Worktree path escaped sandbox boundary.");

                List<string> gitArgs;
                if (action == "add")
                {
                    var branch = args.OptionalString("branch");
                    if (branch is not null && branch.StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException("A Git worktree branch name may not begin with '-'.");
                    gitArgs = string.IsNullOrWhiteSpace(branch)
                        ? ["worktree", "add", "--detach", wtPath]
                        : ["worktree", "add", "-b", branch, wtPath];
                }
                else if (action == "remove")
                {
                    gitArgs = ["worktree", "remove", wtPath]; // Git refuses to remove a dirty worktree. Never force-delete user changes.
                }
                else
                {
                    throw new ArgumentException($"Unsupported git_worktree action '{action}'.");
                }

                var res = await runner.RunAsync("git", "git", gitArgs, ws, timeoutSeconds: 60, ct: ct);
                return res.ExitCode == 0
                    ? ToolResult.Ok($"git worktree {action} succeeded ({wtPath}).", new { worktreePath = wtPath, executionId = res.ExecutionId, output = res.OutputExcerpt }, res.ExecutionId)
                    : FormatCommandResult(res);
            });

        yield return new ToolDefinition(
            Name: "git_stage",
            Category: "git",
            Description: "Stages specified workspace files (or all modified/new files when all=true) into the local Git index.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Updates local .git/index.",
            Parameters:
            [
                new("paths", "array", "Relative file paths to stage.", ItemsSchema: new JsonObject { ["type"] = "string" }),
                new("all", "boolean", "Stage all workspace changes (git add -A).", Default: false)
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var stageAll = args.OptionalBool("all", false);
                var relPaths = args.OptionalStringList("paths");

                if (!stageAll && relPaths.Count == 0)
                    throw new ArgumentException("Provide non-empty 'paths' or set all=true.");

                var gitArgs = new List<string> { "add" };
                if (stageAll)
                {
                    gitArgs.Add("-A");
                }
                else
                {
                    gitArgs.Add("--");
                    foreach (var p in relPaths)
                    {
                        // Resolve against workspace (file might have been deleted, so resolve path cleanly).
                        var full = guard.ResolvePath(p);
                        gitArgs.Add(guard.GetRelativePathUnix(ws, full));
                    }
                }

                var res = await runner.RunAsync("git", "git", gitArgs, ws, timeoutSeconds: 30, ct: ct);
                return FormatCommandResult(res);
            });

        yield return new ToolDefinition(
            Name: "git_commit",
            Category: "git",
            Description: "Creates a local Git commit in the active workspace without pushing externally, and records the commit SHA in session checkpoints.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Creates a local Git commit and persists a session checkpoint.",
            Parameters:
            [
                new("message", "string", "Commit message.", Required: true),
                new("stageAll", "boolean", "Automatically stage all changes (git add -A) before committing.", Default: false),
                new("authorName", "string", "Optional fallback author name if repo has no git user.name configured."),
                new("authorEmail", "string", "Optional fallback author email if repo has no git user.email configured.")
            ],
            Handler: async (args, ctx, ct) =>
            {
                if (!config.Autonomy.AllowGitCommits)
                    throw new WorkspaceViolationException("Local Git commits are disabled by autonomy configuration.");

                var ws = guard.RequireActiveWorkspace();
                var message = args.RequireString("message");

                if (args.OptionalBool("stageAll", false))
                {
                    var addRes = await runner.RunAsync("git", "git", ["add", "-A"], ws, timeoutSeconds: 30, ct: ct);
                    if (addRes.ExitCode != 0) return FormatCommandResult(addRes);
                }

                // Ensure git user.name/email exist or supply per-command -c fallback so unattended commits never fail.
                var commitArgs = new List<string>();
                var nameCheck = await runner.RunAsync("git", "git", ["config", "user.name"], ws, timeoutSeconds: 10, ct: ct);
                if (nameCheck.ExitCode != 0)
                {
                    commitArgs.Add("-c");
                    commitArgs.Add($"user.name={args.OptionalString("authorName", "LocalIdeBridge User")}");
                    commitArgs.Add("-c");
                    commitArgs.Add($"user.email={args.OptionalString("authorEmail", "user@local.invalid")}");
                }

                commitArgs.AddRange(["-c", "commit.gpgsign=false", "commit", "-m", message]);
                var res = await runner.RunAsync("git", "git", commitArgs, ws, timeoutSeconds: 30, ct: ct);
                if (res.ExitCode != 0)
                    return FormatCommandResult(res);

                var shaRes = await runner.RunAsync("git", "git", ["rev-parse", "HEAD"], ws, timeoutSeconds: 10, ct: ct);
                var brRes = await runner.RunAsync("git", "git", ["rev-parse", "--abbrev-ref", "HEAD"], ws, timeoutSeconds: 10, ct: ct);
                var sha = ExtractStdOutLines(shaRes.OutputExcerpt).FirstOrDefault() ?? "unknown";
                var branch = ExtractStdOutLines(brRes.OutputExcerpt).FirstOrDefault() ?? "HEAD";

                var cpId = $"cp_{DateTimeOffset.UtcNow:HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
                db.RecordGitCheckpoint(new GitCheckpointRecord(cpId, ctx.SessionId, ws, branch, sha, message, DateTimeOffset.UtcNow));

                return ToolResult.Ok(
                    $"Committed {sha[..Math.Min(8, sha.Length)]} on '{branch}': {message}",
                    new { commitSha = sha, shortSha = sha[..Math.Min(8, sha.Length)], branch, message, checkpointId = cpId, executionId = res.ExecutionId, outputExcerpt = res.OutputExcerpt },
                    res.ExecutionId);
            });

        yield return new ToolDefinition(
            Name: "git_checkpoint",
            Category: "git",
            Description: "Records the current local Git HEAD commit and branch as a durable named session checkpoint (or lists existing checkpoints).",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Persists a checkpoint record in data/state.db.",
            Parameters:
            [
                new("label", "string", "Human/AI-readable label for this checkpoint (omit to list recent checkpoints).")
            ],
            Handler: async (args, ctx, ct) =>
            {
                var label = args.OptionalString("label");
                if (string.IsNullOrWhiteSpace(label))
                {
                    var list = db.ListGitCheckpoints(ctx.SessionId, 25);
                    return ToolResult.Ok($"Listed {list.Count} Git checkpoint(s).", new { checkpoints = list });
                }

                var ws = guard.RequireActiveWorkspace();
                var shaRes = await runner.RunAsync("git", "git", ["rev-parse", "HEAD"], ws, timeoutSeconds: 10, ct: ct);
                if (shaRes.ExitCode != 0)
                    return ToolResult.Fail("git_error", "Cannot create Git checkpoint: workspace has no HEAD commit yet.");

                var brRes = await runner.RunAsync("git", "git", ["rev-parse", "--abbrev-ref", "HEAD"], ws, timeoutSeconds: 10, ct: ct);
                var sha = ExtractStdOutLines(shaRes.OutputExcerpt).FirstOrDefault() ?? "";
                var br = ExtractStdOutLines(brRes.OutputExcerpt).FirstOrDefault() ?? "HEAD";
                var cp = new GitCheckpointRecord(
                    $"cp_{DateTimeOffset.UtcNow:HHmmss}_{Guid.NewGuid().ToString("N")[..6]}",
                    ctx.SessionId, ws, br, sha, label, DateTimeOffset.UtcNow);
                db.RecordGitCheckpoint(cp);

                return ToolResult.Ok($"Saved Git checkpoint '{cp.Id}' at {sha[..Math.Min(8, sha.Length)]} ({label}).", cp);
            });

        yield return new ToolDefinition(
            Name: "git_push",
            Category: "git",
            Description: "Pushes local commits to a configured remote ONLY when autonomy.gitPushPolicy is explicitly set to 'allow' by the local owner (blocked by default).",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Publishes commits externally when authorized; rejected by default.",
            Parameters:
            [
                new("remote", "string", "Remote name.", Default: "origin"),
                new("branch", "string", "Optional branch name.")
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var repoPolicy = guard.LoadRepoPolicy(ws);
                var allowed = config.Autonomy.GitPushPolicy.Equals("allow", StringComparison.OrdinalIgnoreCase) &&
                              repoPolicy?.AllowGitPush != false;

                if (!allowed)
                {
                    return ToolResult.Fail(
                        "authorization_required",
                        "External Git push is blocked by default policy (autonomy.gitPushPolicy='deny'). " +
                        "Local commits remain fully permitted. To authorize pushing, the local user must set autonomy.gitPushPolicy='allow' in config.json.");
                }

                var remote = args.OptionalString("remote", "origin")!;
                var branch = args.OptionalString("branch");
                if (remote.StartsWith('-') || (branch is not null && branch.StartsWith('-')))
                    throw new ArgumentException("Invalid remote or branch name.");

                var gitArgs = new List<string> { "push", remote };
                if (!string.IsNullOrWhiteSpace(branch)) gitArgs.Add(branch);
                var res = await runner.RunAsync("git", "git", gitArgs, ws, timeoutSeconds: 120, ct: ct);
                return FormatCommandResult(res);
            });
    }

    private static string RequireBranch(string? name)
        => string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Parameter 'name' is required.") : name;

    private static List<string> BuildBranchArgs(List<string> baseArgs, string? startPoint)
    {
        if (!string.IsNullOrWhiteSpace(startPoint) && !startPoint.StartsWith('-'))
            baseArgs.Add(startPoint);
        return baseArgs;
    }

    private static List<string> ExtractStdOutLines(string excerpt)
    {
        var list = new List<string>();
        foreach (var raw in excerpt.Split('\n'))
        {
            if (raw.StartsWith("[out] ", StringComparison.Ordinal))
                list.Add(raw[6..].TrimEnd('\r'));
        }
        return list;
    }

    private static object BuildCommandPayload(CommandExecutionResult res, object? extra = null) => new
    {
        executionId = res.ExecutionId,
        category = res.Category,
        executable = res.Executable,
        workingDirectory = res.WorkingDirectory,
        startedAtUtc = res.StartedAtUtc,
        completedAtUtc = res.CompletedAtUtc,
        durationMs = res.DurationMs,
        exitCode = res.ExitCode,
        timedOut = res.TimedOut,
        cancelled = res.Cancelled,
        diagnostics = res.Diagnostics,
        outputExcerpt = res.OutputExcerpt,
        extra,
        logReference = new
        {
            execId = res.ExecutionId,
            totalLines = res.TotalLogLines,
            totalBytes = res.TotalLogBytes,
            wasCapTruncated = res.LogTruncated
        }
    };

    private static ToolResult FormatCommandResult(CommandExecutionResult res)
    {
        var payload = BuildCommandPayload(res);
        if (res.ExitCode == 0)
            return ToolResult.Ok($"{res.Category} '{res.Executable}' succeeded in {res.DurationMs}ms.", payload, res.ExecutionId);
        if (res.TimedOut)
            return ToolResult.Fail("timeout", $"{res.Category} '{res.Executable}' timed out after {res.DurationMs}ms.", payload, res.ExecutionId);
        if (res.Cancelled)
            return ToolResult.Fail("cancelled", $"{res.Category} '{res.Executable}' was cancelled.", payload, res.ExecutionId);
        return ToolResult.Fail("command_failed", $"{res.Category} '{res.Executable}' exited with code {res.ExitCode}.", payload, res.ExecutionId);
    }
}
