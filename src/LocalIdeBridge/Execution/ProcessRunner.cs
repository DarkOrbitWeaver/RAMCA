using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Execution;

public sealed record CompilerDiagnostic(
    string File,
    int Line,
    int Column,
    string Severity,
    string Code,
    string Message);

public sealed record TestCaseFailure(
    string TestName,
    string Message,
    string? StackTrace);

public sealed record TestRunSummary(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestCaseFailure> Failures);

public sealed record CommandExecutionResult(
    string ExecutionId,
    string Category,
    string Executable,
    string WorkingDirectory,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMs,
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    string OutputExcerpt,
    int TotalLogLines,
    long TotalLogBytes,
    bool LogTruncated,
    IReadOnlyList<CompilerDiagnostic> Diagnostics,
    string? StdoutCapture = null,
    bool StdoutCaptureTruncated = false);

/// <summary>
/// Policy-enforced, cancellable, bounded-memory command runner with disk-backed streaming logs,
/// MSBuild/Roslyn diagnostic extraction, TRX test result parsing, and OS child-process supervision.
/// </summary>
public sealed partial class ProcessRunner
{
    private readonly PortablePaths _paths;
    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly StateDatabase _db;
    private readonly LogStore _logs;
    private readonly ChildProcessSupervisor _supervisor;
    private readonly RedactingLogger _logger;

    // Matches standard MSBuild / Roslyn diagnostic format: path(line,col): error CS0103: message [proj]
    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.+?)(?:\s*\[[^\]]+\])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildDiagRegex();

    public ProcessRunner(
        PortablePaths paths,
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        LogStore logs,
        ChildProcessSupervisor supervisor,
        RedactingLogger logger)
    {
        _paths = paths;
        _config = config;
        _guard = guard;
        _db = db;
        _logs = logs;
        _supervisor = supervisor;
        _logger = logger;
    }

    public async Task<CommandExecutionResult> RunAsync(
        string category,
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        bool parseCompilerDiagnostics = false,
        bool allowWorkspaceExecutable = false,
        CancellationToken ct = default,
        int stdoutCaptureLimit = 0)
    {
        if (!_config.Autonomy.AllowCommands)
            throw new WorkspaceViolationException("Command execution is disabled by autonomy configuration.");

        var wsRoot = _guard.RequireActiveWorkspace();
        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? wsRoot
            : _guard.ResolvePath(workingDirectory, forWrite: false, explicitWorkspace: wsRoot);

        if (!Directory.Exists(workDir))
            throw new DirectoryNotFoundException($"Working directory does not exist: {workDir}");

        var resolvedExe = ValidateAndResolveExecutable(executable, arguments, wsRoot, allowWorkspaceExecutable);
        var effectiveTimeout = Math.Clamp(
            timeoutSeconds ?? _config.Resources.DefaultCommandTimeoutSeconds,
            1,
            _config.Resources.MaxCommandTimeoutSeconds);

        var execId = $"exec_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var sessionId = _db.ActiveSessionId;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var diagnostics = new List<CompilerDiagnostic>();
        stdoutCaptureLimit = Math.Clamp(stdoutCaptureLimit, 0, 2_000_000);
        // Bounded structured capture only when a caller needs exact stdout, e.g. git status.
        var capturedStdout = stdoutCaptureLimit > 0 ? new StringBuilder(Math.Min(4096, stdoutCaptureLimit)) : null;
        var stdoutCaptureTruncated = false;
        var diagSeen = new HashSet<string>(StringComparer.Ordinal);

        using var logWriter = _logs.OpenWriter(execId, sessionId, category);
        logWriter.WriteLine("meta", $"exe={resolvedExe} cwd={workDir} timeout={effectiveTimeout}s");

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        _paths.ApplyPortableChildEnvironment(psi.Environment, _config.Autonomy.RedirectChildTempToPortable);
        // Never leak interactive prompts during unattended automation.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        if (!string.IsNullOrWhiteSpace(_config.Connection.TlsCertPasswordEnvVar))
            psi.Environment.Remove(_config.Connection.TlsCertPasswordEnvVar);

        // On Linux/macOS test hosts, wrap long child execution cleanly; on Windows, Job Objects kill the full tree.
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start process '{executable}'.");

        var pid = proc.Id;
        try { _supervisor.Register(proc); }
        catch
        {
            ChildProcessSupervisor.TryKillTree(proc);
            throw;
        }
        try { proc.StandardInput.Close(); } catch { }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeout));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        var cancelled = false;
        int exitCode;

        async Task PumpStreamAsync(StreamReader reader, string prefix)
        {
            // Read fixed-size chunks; StreamReader.ReadLineAsync allocates one enormous
            // string if a compiler or malicious child prints a newline-free 50MB line.
            var buffer = new char[4096];
            var current = new StringBuilder(4096);
            void Emit(string line)
            {
                logWriter.WriteLine(prefix, line);
                if (prefix == "out" && capturedStdout is not null)
                {
                    var remaining = stdoutCaptureLimit - capturedStdout.Length;
                    if (line.Length + 1 > remaining) stdoutCaptureTruncated = true;
                    if (remaining > 0)
                    {
                        capturedStdout.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                        if (remaining > line.Length) capturedStdout.Append('\n');
                    }
                }
                if (!parseCompilerDiagnostics || diagnostics.Count >= 100) return;
                var m = MsBuildDiagRegex().Match(line.Trim());
                if (!m.Success) return;
                var d = new CompilerDiagnostic(m.Groups["file"].Value.Trim(),
                    int.TryParse(m.Groups["line"].Value, out var ln) ? ln : 0,
                    int.TryParse(m.Groups["col"].Value, out var col) ? col : 0,
                    m.Groups["sev"].Value.ToLowerInvariant(), m.Groups["code"].Value,
                    m.Groups["msg"].Value.Trim());
                var key = $"{d.File}:{d.Line}:{d.Column}:{d.Code}";
                lock (diagnostics)
                {
                    if (diagnostics.Count < 100 && diagSeen.Add(key)) diagnostics.Add(d);
                }
            }
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), linkedCts.Token).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] == '\n')
                        {
                            Emit(current.ToString().TrimEnd('\r'));
                            current.Clear();
                        }
                        else
                        {
                            current.Append(buffer[i]);
                            if (current.Length >= 4096)
                            {
                                Emit(current.ToString() + " [continued]");
                                current.Clear();
                            }
                        }
                    }
                }
                if (current.Length > 0) Emit(current.ToString());
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        var stdoutTask = PumpStreamAsync(proc.StandardOutput, "out");
        var stderrTask = PumpStreamAsync(proc.StandardError, "err");

        try
        {
            await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            // Allow stream pumps a short grace window to drain buffered lines.
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(1500, CancellationToken.None)).ConfigureAwait(false);
            exitCode = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            ChildProcessSupervisor.TryKillTree(proc);
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            logWriter.WriteLine("meta", timedOut
                ? $"Terminated after exceeding timeout of {effectiveTimeout}s."
                : "Terminated due to cancellation request.");
            exitCode = timedOut ? -124 : -130;
        }
        finally
        {
            _supervisor.Unregister(pid);
            logWriter.Complete();
            sw.Stop();
        }

        var completedAt = DateTimeOffset.UtcNow;
        var result = new CommandExecutionResult(
            execId,
            category,
            Path.GetFileName(resolvedExe),
            workDir,
            startedAt,
            completedAt,
            sw.ElapsedMilliseconds,
            exitCode,
            timedOut,
            cancelled,
            logWriter.BuildBoundedExcerpt(),
            logWriter.TotalLines,
            logWriter.TotalBytes,
            logWriter.WasTruncated,
            diagnostics,
            capturedStdout?.ToString(),
            stdoutCaptureTruncated || (capturedStdout is not null && (!stdoutTask.IsCompletedSuccessfully || !stderrTask.IsCompletedSuccessfully)));

        if (exitCode != 0)
            _logger.Warn("exec", $"{category} '{Path.GetFileName(resolvedExe)}' exited with {exitCode}", $"execId={execId}", sw.ElapsedMilliseconds);
        else
            _logger.Info("exec", $"{category} '{Path.GetFileName(resolvedExe)}' succeeded", $"execId={execId}", sw.ElapsedMilliseconds);

        return result;
    }

    public string ResolvePreferredShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";
        }
        return FindOnPath("pwsh") ?? FindOnPath("bash") ?? "/bin/sh";
    }

    public IReadOnlyList<string> BuildShellArguments(string shellExe, string script)
    {
        var name = Path.GetFileNameWithoutExtension(shellExe).ToLowerInvariant();
        if (name is "pwsh" or "powershell")
        {
            return ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script];
        }
        if (name == "cmd")
        {
            return ["/d", "/s", "/c", script];
        }
        return ["-c", script];
    }

    private string ValidateAndResolveExecutable(string executable, IReadOnlyList<string> arguments, string wsRoot, bool allowWorkspaceExecutable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new WorkspaceViolationException("Executable name must not be empty.");

        var joinedCommand = executable + " " + string.Join(' ', arguments);
        foreach (var blocked in _config.Autonomy.BlockedCommandPatterns)
        {
            if (!string.IsNullOrWhiteSpace(blocked) &&
                joinedCommand.Contains(blocked, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceViolationException(
                    $"Command matches blocked administrative/destructive policy pattern '{blocked.Trim()}'.");
            }
        }

        var repoPolicy = _guard.LoadRepoPolicy(wsRoot);
        var baseName = Path.GetFileNameWithoutExtension(executable);
        if (baseName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("sh", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.Autonomy.AllowShellScripts || repoPolicy?.AllowShellScripts == false)
                throw new WorkspaceViolationException("Shell script execution is disabled by policy.");
        }

        if (baseName.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            _config.Autonomy.GitPushPolicy.Equals("deny", StringComparison.OrdinalIgnoreCase) &&
            arguments.Any(arg => arg.Equals("push", StringComparison.OrdinalIgnoreCase)))
            throw new WorkspaceViolationException("Direct git push is disabled by gitPushPolicy. Use an explicitly authorized Git workflow.");

        // Check if executable is an explicit path inside the workspace.
        if (executable.Contains('/') || executable.Contains('\\'))
        {
            // Allow explicitly resolved system shell/dotnet path passed internally, or approved workspace binary.
            if (Path.IsPathRooted(executable) && File.Exists(executable))
            {
                // Only portable tools are a trusted path-based exception. Otherwise
                // a workspace executable must be explicitly opted into by this call.
                if (WorkspaceGuard.IsEqualOrInside(executable, _paths.ToolsDir) && IsAllowedToolName(baseName, repoPolicy))
                    return executable;
                var foundOnPath = FindOnPath(Path.GetFileName(executable));
                if (!string.IsNullOrWhiteSpace(foundOnPath) &&
                    string.Equals(Path.GetFullPath(foundOnPath), Path.GetFullPath(executable),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                    !WorkspaceGuard.IsEqualOrInside(executable, wsRoot) &&
                    IsAllowedToolName(baseName, repoPolicy))
                    return executable;
                if (!WorkspaceGuard.IsEqualOrInside(executable, wsRoot) && !allowWorkspaceExecutable)
                    throw new WorkspaceViolationException("Untrusted absolute executable path. Use an approved executable name on PATH.");
            }

            if (!allowWorkspaceExecutable)
                throw new WorkspaceViolationException($"Path-based executable '{executable}' requires allowWorkspaceExecutable.");

            var wsExe = _guard.ResolvePath(executable, forWrite: false, explicitWorkspace: wsRoot);
            if (!File.Exists(wsExe))
                throw new FileNotFoundException($"Workspace executable not found: {wsExe}");
            return wsExe;
        }

        if (!IsAllowedToolName(baseName, repoPolicy))
        {
            throw new WorkspaceViolationException(
                $"Executable '{executable}' is not in autonomy.allowedExecutables or repository approvedProjectExecutables.");
        }

        // Prefer portable tools/ directory first, then PATH, then ~/.dotnet fallback.
        var portableTool = FindInDirectory(_paths.ToolsDir, executable);
        if (portableTool is not null) return portableTool;

        var onPath = FindOnPath(executable);
        if (onPath is not null)
        {
            if (WorkspaceGuard.IsEqualOrInside(onPath, wsRoot))
                throw new WorkspaceViolationException("PATH resolved a program inside the workspace. Supply allowWorkspaceExecutable explicitly.");
            return onPath;
        }

        if (baseName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) &&
                Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return processPath;

            var homeDotnet = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(homeDotnet)) return homeDotnet;
        }

        return executable;
    }

    private bool IsAllowedToolName(string baseName, RepoPolicy? repoPolicy)
    {
        if (_config.Autonomy.AllowedExecutables.Any(a =>
                string.Equals(Path.GetFileNameWithoutExtension(a), baseName, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!OperatingSystem.IsWindows() && baseName is "bash" or "sh")
            return _config.Autonomy.AllowShellScripts;

        if (repoPolicy?.ApprovedProjectExecutables.Any(a =>
                string.Equals(Path.GetFileNameWithoutExtension(a), baseName, StringComparison.OrdinalIgnoreCase)) == true)
            return true;

        return false;
    }

    public static TestRunSummary ParseTrxFile(string trxPath)
    {
        if (!File.Exists(trxPath))
            return new TestRunSummary(0, 0, 0, 0, []);

        var doc = XDocument.Load(trxPath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var counters = doc.Descendants(ns + "Counters").FirstOrDefault()
            ?? throw new InvalidDataException($"TRX report has no test counters: {trxPath}");

        int Attr(string name) => int.TryParse(counters?.Attribute(name)?.Value, out var v) ? v : 0;

        var total = Attr("total");
        var passed = Attr("passed");
        var failed = Attr("failed") + Attr("error") + Attr("timeout");
        var executed = Attr("executed");
        var notExecuted = Attr("notExecuted");
        var skipped = notExecuted > 0 ? notExecuted : Math.Max(0, total - executed);

        var failures = new List<TestCaseFailure>();
        foreach (var result in doc.Descendants(ns + "UnitTestResult"))
        {
            var outcome = result.Attribute("outcome")?.Value ?? "";
            if (!outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) &&
                !outcome.Equals("Error", StringComparison.OrdinalIgnoreCase) &&
                !outcome.Equals("Timeout", StringComparison.OrdinalIgnoreCase))
                continue;

            if (failures.Count >= 30) break;
            var testName = result.Attribute("testName")?.Value ?? "UnknownTest";
            var errInfo = result.Descendants(ns + "ErrorInfo").FirstOrDefault();
            var msg = errInfo?.Element(ns + "Message")?.Value?.Trim() ?? outcome;
            var stack = errInfo?.Element(ns + "StackTrace")?.Value?.Trim();
            if (msg.Length > 1000) msg = msg[..1000] + "...";
            if (stack is not null && stack.Length > 1500) stack = stack[..1500] + "...";
            failures.Add(new TestCaseFailure(testName, msg, stack));
        }

        return new TestRunSummary(total, passed, failed, skipped, failures);
    }

    public static (TestRunSummary Summary, int ReportFiles) ParseTrxDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return (new TestRunSummary(0, 0, 0, 0, []), 0);
        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        var total = 0; var passed = 0; var failed = 0; var skipped = 0;
        var failures = new List<TestCaseFailure>();
        foreach (var file in files)
        {
            var report = ParseTrxFile(file); // malformed files must fail, not report 0 passing tests
            total += report.Total;
            passed += report.Passed;
            failed += report.Failed;
            skipped += report.Skipped;
            foreach (var item in report.Failures.Take(Math.Max(0, 50 - failures.Count))) failures.Add(item);
        }
        return (new TestRunSummary(total, passed, failed, skipped, failures), files.Length);
    }

    public static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var found = FindInDirectory(dir.Trim(), exeName);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindInDirectory(string dir, string exeName)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var direct = Path.Combine(dir, exeName);
            if (File.Exists(direct)) return direct;
            if (OperatingSystem.IsWindows() && !Path.HasExtension(exeName))
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                {
                    var candidate = direct + ext;
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    public static IReadOnlyList<object> ProbeInstalledTools(PortablePaths paths)
    {
        var probes = new (string Name, string Exe, string[] Args, bool WindowsOnly)[]
        {
            ("dotnet", "dotnet", ["--version"], false),
            ("git", "git", ["--version"], false),
            ("pwsh", "pwsh", ["--version"], false),
            ("powershell", "powershell", ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"], true),
            ("msbuild", "msbuild", ["-version", "-nologo"], true),
            ("cloudflared (optional tunnel)", "cloudflared", ["--version"], false),
            ("ssh (optional tunnel)", "ssh", ["-V"], false)
        };

        var results = new List<object>();
        foreach (var p in probes)
        {
            if (p.WindowsOnly && !OperatingSystem.IsWindows()) continue;
            var path = FindInDirectory(paths.ToolsDir, p.Exe) ?? FindOnPath(p.Exe);
            if (path is null && p.Name == "dotnet")
            {
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet",
                    OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(fallback)) path = fallback;
            }

            if (path is null)
            {
                results.Add(new { tool = p.Name, installed = false, path = (string?)null, version = (string?)null });
                continue;
            }

            string? version = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var a in p.Args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    var outText = proc.StandardOutput.ReadToEnd();
                    var errText = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(3000);
                    var combined = string.IsNullOrWhiteSpace(outText) ? errText : outText;
                    version = combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                }
            }
            catch { }

            results.Add(new { tool = p.Name, installed = true, path, version });
        }

        return results;
    }
}
