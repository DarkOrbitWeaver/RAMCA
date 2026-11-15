using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LocalIdeBridge.Core;
using LocalIdeBridge.Protocols;

namespace LocalIdeBridge.Cli;

/// <summary>
/// Minimal, zero-flicker, event-driven supervisory TUI with virtualized output and near-zero idle CPU.
/// Renders strictly on incoming events, keystrokes, or terminal resize—never via busy polling.
/// </summary>
public sealed class SupervisorTui
{
    private enum ViewTab { Overview = 1, Edits = 2, Jobs = 3, GitAndSessions = 4, Errors = 5 }

    private readonly WorkbenchHost _host;
    private readonly SemaphoreSlim _wakeSignal = new(1, 1);
    private ViewTab _activeTab = ViewTab.Overview;
    private int _scrollOffset;
    private string _statusBanner = "Ready. Press [I] to copy AI instructions, [E] endpoint, [K] key, or [Q] to quit.";
    private string? _cachedGitBranch;
    private DateTimeOffset _lastGitCheckUtc = DateTimeOffset.MinValue;
    private int _lastWidth;
    private int _lastHeight;
    private readonly bool _useColor;

    public SupervisorTui(WorkbenchHost host)
    {
        _host = host;
        _useColor = !string.Equals(host.Config.Tui.Theme, "mono", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Graceful headless fallback when stdin/stdout is not an interactive terminal.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            await RunHeadlessAsync(ct);
            return;
        }

        EnableWindowsAnsiIfNeeded();
        Console.OutputEncoding = Encoding.UTF8;

        void SignalRedraw()
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                try { _wakeSignal.Release(); } catch { }
            }
        }

        void OnActivity(Core.ActivityEvent _) => SignalRedraw();
        _host.Logger.OnEvent += OnActivity;

        // Enter alternate screen buffer and hide cursor for clean, flicker-free rendering.
        Console.Write("\x1b[?1049h\x1b[?25l");
        try
        {
            RefreshGitBranchIfStale(force: true);
            RenderFrame();

            while (!ct.IsCancellationRequested)
            {
                // Process any buffered user keystrokes immediately.
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (HandleKey(key))
                        return;
                    RenderFrame();
                }

                // Detect terminal resize without busy spinning (sleep up to 1.5s unless woken by event).
                var woken = await _wakeSignal.WaitAsync(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                var resized = SafeWidth() != _lastWidth || SafeHeight() != _lastHeight;

                if (woken || resized || Console.KeyAvailable)
                {
                    if (woken)
                    {
                        // Coalesce rapid bursts of tool events into a single frame.
                        await Task.Delay(Math.Min(_host.Config.Tui.RefreshIntervalMs, 250), ct).ConfigureAwait(false);
                    }
                    RefreshGitBranchIfStale(force: false);
                    RenderFrame();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _host.Logger.OnEvent -= OnActivity;
            Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
        }
    }

    public async Task RunHeadlessAsync(CancellationToken ct)
    {
        Console.WriteLine("LocalIdeBridge");
        Console.WriteLine($"Status    : Online (headless)");
        Console.WriteLine($"Endpoint  : {_host.Instructions.GetMcpEndpoint()}");
        Console.WriteLine($"HTTP API  : {_host.Instructions.GetEffectiveBaseUrl()}/api/v1");
        Console.WriteLine($"Key       : {_host.Credentials.GetMasked()}");
        Console.WriteLine($"Workspace : {_host.Guard.ActiveWorkspace ?? "(none)"}");
        Console.WriteLine($"Session   : {_host.Database.ActiveSessionId}");
        Console.WriteLine($"Tools     : {_host.Registry.All.Count} available");
        Console.WriteLine("Press Ctrl+C to stop listener and clean up owned processes.");

        void PrintEvent(Core.ActivityEvent e)
        {
            if (e.Category == "host" || e.IsError || e.Category is "editing" or "build" or "test" or "git" or "workflow" or "job")
                Console.WriteLine($"[{e.TimestampUtc:HH:mm:ss}] [{e.Category}] {e.Summary}");
        }

        _host.Logger.OnEvent += PrintEvent;
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _host.Logger.OnEvent -= PrintEvent;
        }
    }

    private bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            return true;

        switch (key.Key)
        {
            case ConsoleKey.D1: _activeTab = ViewTab.Overview; _scrollOffset = 0; break;
            case ConsoleKey.D2: _activeTab = ViewTab.Edits; _scrollOffset = 0; break;
            case ConsoleKey.D3: _activeTab = ViewTab.Jobs; _scrollOffset = 0; break;
            case ConsoleKey.D4: _activeTab = ViewTab.GitAndSessions; _scrollOffset = 0; break;
            case ConsoleKey.D5: _activeTab = ViewTab.Errors; _scrollOffset = 0; break;
            case ConsoleKey.UpArrow: _scrollOffset = Math.Max(0, _scrollOffset - 1); break;
            case ConsoleKey.DownArrow: _scrollOffset = Math.Min(200, _scrollOffset + 1); break;
            case ConsoleKey.PageUp: _scrollOffset = Math.Max(0, _scrollOffset - 10); break;
            case ConsoleKey.PageDown: _scrollOffset = Math.Min(200, _scrollOffset + 10); break;

            case ConsoleKey.E:
            {
                var ep = _host.Instructions.GetMcpEndpoint();
                var (ok, _) = ConnectionInstructions.TryCopyToClipboard(ep);
                _statusBanner = ok ? $"Copied endpoint: {ep}" : $"Endpoint: {ep}";
                break;
            }
            case ConsoleKey.K:
            {
                var secret = _host.Credentials.GetSecretForExplicitCopy();
                var (ok, msg) = ConnectionInstructions.TryCopyToClipboard(secret);
                _statusBanner = ok
                    ? $"Copied secret access key ({_host.Credentials.GetMasked()}) to clipboard."
                    : $"{msg} Read key safely from: {_host.Paths.SecretKeyFile}";
                break;
            }
            case ConsoleKey.I:
            {
                var text = _host.Instructions.BuildAgentInstructions();
                var outPath = Path.Combine(_host.Paths.DataDir, "connection-instructions.md");
                PortablePaths.AtomicWriteAllText(outPath, text + Environment.NewLine);
                var (ok, _) = ConnectionInstructions.TryCopyToClipboard(text);
                _statusBanner = ok
                    ? "Copied AI connection instructions to clipboard (secret key excluded)."
                    : $"Saved AI instructions to {outPath} (secret key excluded).";
                break;
            }
            case ConsoleKey.R:
            {
                _host.Credentials.Rotate();
                _statusBanner = $"Regenerated access key: {_host.Credentials.GetMasked()}. Prior key invalidated immediately.";
                break;
            }
            case ConsoleKey.P:
            {
                _host.Registry.IsPaused = !_host.Registry.IsPaused;
                _statusBanner = _host.Registry.IsPaused
                    ? "Paused modifying/command tools. Read-only inspection remains online."
                    : "Resumed autonomous execution.";
                break;
            }
            case ConsoleKey.W:
            {
                CycleWorkspace();
                break;
            }
            case ConsoleKey.S:
            {
                var next = _host.Database.CreateAndActivateSession(_host.Guard.ActiveWorkspace ?? "");
                _statusBanner = $"Started fresh session {next.Id}.";
                break;
            }
            case ConsoleKey.N:
            {
                PromptSteeringNote();
                break;
            }
        }
        return false;
    }

    private void CycleWorkspace()
    {
        var list = _host.Config.TrustedWorkspaces;
        if (list.Count <= 1)
        {
            _statusBanner = "Only one trusted workspace configured. Use 'LocalIdeBridge workspace add <path>' to add more.";
            return;
        }
        var active = _host.Guard.ActiveWorkspace;
        var idx = list.FindIndex(w => string.Equals(WorkspaceGuard.NormalizeRoot(w.Path), active, StringComparison.OrdinalIgnoreCase));
        var next = list[(idx + 1) % list.Count];
        try
        {
            _host.Guard.SelectWorkspace(next.Path);
            RefreshGitBranchIfStale(force: true);
            _statusBanner = $"Switched active workspace to {next.Path}.";
        }
        catch (Exception ex)
        {
            _statusBanner = $"Workspace switch failed: {ex.Message}";
        }
    }

    private void PromptSteeringNote()
    {
        Console.Write("\x1b[?25h\x1b[H\x1b[2J");
        Console.WriteLine("LocalIdeBridge — Supervisor Steering Note");
        Console.WriteLine("Note: Readable by connected agents via workspace_status and MCP resource bridge://session/active.");
        Console.WriteLine("      (Does not push unsolicited messages into closed webchat tabs.)");
        Console.Write("Enter note (blank to clear): ");
        var note = Console.ReadLine();
        Console.Write("\x1b[?25l");
        _host.Database.SetSteeringNote(note);
        _statusBanner = string.IsNullOrWhiteSpace(note) ? "Cleared steering note." : "Updated session steering note.";
    }

    private void RenderFrame()
    {
        var w = Math.Max(50, SafeWidth());
        var h = Math.Max(16, SafeHeight());
        _lastWidth = w;
        _lastHeight = h;

        var sb = new StringBuilder(4096);
        sb.Append("\x1b[H"); // Cursor home (overwrite per line with \x1b[K for zero flicker).

        var highContrast = string.Equals(_host.Config.Tui.Theme, "high_contrast", StringComparison.OrdinalIgnoreCase);
        string Bold(string s) => _useColor ? $"\x1b[1m{s}\x1b[0m" : s;
        string Dim(string s) => _useColor && !highContrast ? $"\x1b[2m{s}\x1b[0m" : s;
        string Green(string s) => _useColor ? $"\x1b[32m{s}\x1b[0m" : s;
        string Yellow(string s) => _useColor ? $"\x1b[33m{s}\x1b[0m" : s;
        string Red(string s) => _useColor ? $"\x1b[31m{s}\x1b[0m" : s;
        string Cyan(string s) => _useColor ? $"\x1b[36m{s}\x1b[0m" : s;

        void Line(string raw, int visibleLen = -1)
        {
            sb.Append(raw).Append("\x1b[K\n");
        }

        using var self = Process.GetCurrentProcess();
        var rssMb = self.WorkingSet64 / (1024.0 * 1024.0);
        var statusLabel = _host.Registry.IsPaused ? Yellow("Paused") : Green("Online");
        var ep = _host.Instructions.GetMcpEndpoint();
        var ws = _host.Guard.ActiveWorkspace ?? "(none)";
        var branch = _cachedGitBranch ?? "-";

        Line($"{Bold("LocalIdeBridge")}  Status: {statusLabel}  {Dim($"| Tools: {_host.Registry.All.Count} | Calls: {_host.Registry.TotalInvocations} | Jobs: {_host.Jobs.ActiveJobCount} | RAM: {rssMb:F1} MB")}");
        Line($"Endpoint  : {Cyan(Fit(ep, w - 13))}");
        Line($"Key       : {_host.Credentials.GetMasked()}   Session: {Fit(_host.Database.ActiveSessionId, Math.Max(12, w - 42))}");
        Line($"Workspace : {Fit(ws, Math.Max(16, w - 30))}  Branch: {Fit(branch, 18)}");
        Line(Dim(new string('─', w - 1)));

        // Navigation tabs.
        string Tab(ViewTab t, string label) => _activeTab == t ? Bold($"[{label}]") : Dim($" {label} ");
        Line($"{Tab(ViewTab.Overview, "1:Recent")} {Tab(ViewTab.Edits, "2:Edits/Diffs")} {Tab(ViewTab.Jobs, "3:Jobs/Builds")} {Tab(ViewTab.GitAndSessions, "4:Git/Sessions")} {Tab(ViewTab.Errors, "5:Errors")}");

        // Virtualized body viewport: fetch ONLY enough rows for current terminal height.
        var bodyRows = Math.Max(4, h - 10);
        var rows = BuildVirtualizedRows(bodyRows, w - 2, Green, Yellow, Red, Dim);
        for (var i = 0; i < bodyRows; i++)
        {
            Line(i < rows.Count ? rows[i] : "");
        }

        Line(Dim(new string('─', w - 1)));
        Line(Fit("[E] Copy Endpoint  [K] Copy Key  [I] Copy Instructions  [R] Rotate Key", w - 1));
        Line(Fit("[W] Workspace  [S] New Session  [P] Pause/Resume  [N] Note  [Q] Quit", w - 1));
        sb.Append(Dim(Fit(_statusBanner, w - 1))).Append("\x1b[K");

        Console.Write(sb.ToString());
    }

    private List<string> BuildVirtualizedRows(
        int count,
        int maxCols,
        Func<string, string> green,
        Func<string, string> yellow,
        Func<string, string> red,
        Func<string, string> dim)
    {
        var lines = new List<string>(count);

        switch (_activeTab)
        {
            case ViewTab.Overview:
            {
                var calls = _host.Database.ListToolCalls(_host.Database.ActiveSessionId, count, _scrollOffset);
                if (calls.Count == 0)
                {
                    lines.Add(dim("  Waiting for authorized AI agent tool calls..."));
                    lines.Add(dim("  Copy connection instructions [I] and key [K] into a compatible MCP/HTTP/CLI agent."));
                }
                foreach (var c in calls)
                {
                    var badge = c.Success ? green("OK ") : red("ERR");
                    var text = Fit($"{c.CreatedAtUtc.ToLocalTime():HH:mm:ss}  {c.ToolName,-20} {c.DurationMs,5}ms  {c.Summary}", Math.Max(20, maxCols - 5));
                    lines.Add($"{badge} {text}");
                }
                break;
            }

            case ViewTab.Edits:
            {
                var edits = _host.Database.ListRecentEdits(_host.Database.ActiveSessionId, count);
                if (edits.Count == 0) lines.Add(dim("  No file edits in current session yet."));
                foreach (var e in edits.Take(count))
                {
                    var rev = e.Reverted ? yellow("[reverted] ") : "";
                    lines.Add(Fit($"  {e.CreatedAtUtc.ToLocalTime():HH:mm:ss}  +{e.LinesAdded,-4} -{e.LinesRemoved,-4} {e.Operation,-14} {rev}{e.RelativePath}", maxCols));
                }
                break;
            }

            case ViewTab.Jobs:
            {
                var jobs = _host.Database.ListJobs(limit: count, offset: _scrollOffset);
                if (jobs.Count == 0) lines.Add(dim("  No asynchronous jobs recorded yet."));
                foreach (var j in jobs)
                {
                    var st = j.Status switch
                    {
                        "succeeded" => green("DONE"),
                        "running" or "queued" => yellow("RUN "),
                        _ => red("FAIL")
                    };
                    lines.Add($"{st} {Fit($"{j.Id}  {j.ToolName,-16}  {j.Status,-18}  {j.ErrorMessage ?? ""}", Math.Max(20, maxCols - 6))}");
                }
                break;
            }

            case ViewTab.GitAndSessions:
            {
                var cps = _host.Database.ListGitCheckpoints(limit: Math.Max(2, count / 2));
                lines.Add("  Recent Git Commits / Checkpoints:");
                if (cps.Count == 0) lines.Add(dim("    (none recorded yet)"));
                foreach (var cp in cps.Take(Math.Max(1, count / 2 - 1)))
                    lines.Add(Fit($"    {cp.CommitSha[..Math.Min(8, cp.CommitSha.Length)]} ({cp.Branch}) {cp.Label}", maxCols));

                var remaining = count - lines.Count - 1;
                if (remaining > 0)
                {
                    lines.Add("  Recent Sessions:");
                    foreach (var s in _host.Database.ListSessions(remaining, _scrollOffset))
                        lines.Add(Fit($"    {s.Id}  [{s.Status,-6}]  calls={s.ToolCallCount} err={s.ErrorCount}  {Path.GetFileName(s.Workspace)}", maxCols));
                }
                break;
            }

            case ViewTab.Errors:
            {
                var errs = _host.Database.ListToolCalls(null, count, _scrollOffset, errorsOnly: true);
                if (errs.Count == 0) lines.Add(dim("  No tool errors recorded."));
                foreach (var e in errs)
                    lines.Add($"{red("ERR")} {Fit($"{e.CreatedAtUtc.ToLocalTime():HH:mm:ss} {e.ToolName} [{e.ErrorCode}]: {e.Summary}", Math.Max(20, maxCols - 5))}");
                break;
            }
        }

        return lines;
    }

    private void RefreshGitBranchIfStale(bool force)
    {
        if (!force && (DateTimeOffset.UtcNow - _lastGitCheckUtc).TotalSeconds < 5)
            return;
        _lastGitCheckUtc = DateTimeOffset.UtcNow;

        var ws = _host.Guard.ActiveWorkspace;
        if (string.IsNullOrWhiteSpace(ws)) { _cachedGitBranch = null; return; }
        var headFile = Path.Combine(ws, ".git", "HEAD");
        if (!File.Exists(headFile)) { _cachedGitBranch = null; return; }

        try
        {
            // Read .git/HEAD directly (zero child process spawn on idle!).
            var head = File.ReadAllText(headFile).Trim();
            const string prefix = "ref: refs/heads/";
            _cachedGitBranch = head.StartsWith(prefix, StringComparison.Ordinal)
                ? head[prefix.Length..]
                : head[..Math.Min(8, head.Length)];
        }
        catch
        {
            _cachedGitBranch = null;
        }
    }

    private static int SafeWidth() { try { return Console.WindowWidth > 0 ? Console.WindowWidth : 100; } catch { return 100; } }
    private static int SafeHeight() { try { return Console.WindowHeight > 0 ? Console.WindowHeight : 28; } catch { return 28; } }

    private static string Fit(string s, int max)
    {
        if (max <= 4) return "";
        var clean = s.ReplaceLineEndings(" ");
        return clean.Length <= max ? clean : clean[..(max - 3)] + "...";
    }

    private static void EnableWindowsAnsiIfNeeded()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int STD_OUTPUT_HANDLE = -11;
            const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (GetConsoleMode(handle, out var mode))
                SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwIoMode);
}
