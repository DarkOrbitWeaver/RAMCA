using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;
using System.Runtime.InteropServices;
using System.Text;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
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
    // Selection moves through small, on-demand SQLite pages. We never retain an
    // ever-growing list of historical operations or logs in the TUI.
    private int _scrollOffset;
    private int _detailPage;
    private int _lastListSlots = 6;
    private bool _focusDetails;
    private string? _cachedDiffKey;
    private IReadOnlyList<UiLine> _cachedDiffLines = [];
    private string? _cachedLogKey;
    private IReadOnlyList<UiLine> _cachedLogLines = [];
    private enum UiTone { Normal, Muted, Good, Warning, Error, Accent }
    private readonly record struct UiLine(string Text, UiTone Tone = UiTone.Normal);
    // Only a small, redacted subset of live results is retained. Historical tool
    // calls do not store full output/arguments; never invent their details.
    private sealed record CapturedOperation(
        long CallId, DateTimeOffset TimestampUtc, string ToolName, bool Success,
        string Summary, string? Excerpt, string? Diff, string? Path,
        string? Executable, string? ExitCode, string? Tests);
    private readonly object _resultsLock = new();
    private readonly Queue<CapturedOperation> _recentResults = new();
    private const int MaxCapturedOperations = 48;

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
        void OnToolCompleted(string name, ToolResult result, long elapsed)
        {
            CaptureToolResult(name, result, elapsed);
            SignalRedraw();
        }
        _host.Logger.OnEvent += OnActivity;
        _host.Registry.OnToolCompleted += OnToolCompleted;

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
                        // Invalidate only the currently selected preview; never preload history.
                        _cachedDiffKey = null;
                        _cachedLogKey = null;
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
            _host.Registry.OnToolCompleted -= OnToolCompleted;
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
            case ConsoleKey.D1: SelectTab(ViewTab.Overview); break;
            case ConsoleKey.D2: SelectTab(ViewTab.Edits); break;
            case ConsoleKey.D3: SelectTab(ViewTab.Jobs); break;
            case ConsoleKey.D4: SelectTab(ViewTab.GitAndSessions); break;
            case ConsoleKey.D5: SelectTab(ViewTab.Errors); break;
            case ConsoleKey.UpArrow: MoveSelection(-1); break;
            case ConsoleKey.DownArrow: MoveSelection(1); break;
            case ConsoleKey.PageUp: MoveSelection(-Math.Max(1, _lastListSlots - 1)); break;
            case ConsoleKey.PageDown: MoveSelection(Math.Max(1, _lastListSlots - 1)); break;
            case ConsoleKey.Enter:
                _focusDetails = !_focusDetails;
                _detailPage = 0;
                break;
            case ConsoleKey.Escape:
                _focusDetails = false;
                _detailPage = 0;
                break;

            case ConsoleKey.E:
            {
                var ep = _host.Instructions.GetMcpEndpoint();
                var (ok, _) = ConnectionInstructions.TryCopyToClipboard(ep);
                _statusBanner = ok ? $"Copied endpoint: {ep}" : $"Endpoint: {ep}";
                break;
            }
            case ConsoleKey.C:
            {
                if (!ChatGptTunnelSetup.IsConfigured(_host.Paths))
                {
                    Console.Write("\x1b[?25h\x1b[2J\x1b[H");
                    try
                    {
                        var code = ChatGptTunnelSetup.Configure(_host.Paths, []);
                        if (code != 0)
                        {
                            _statusBanner = "Connection setup incomplete. Press C to retry.";
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _statusBanner = $"Connection setup failed: {ex.Message}";
                        break;
                    }
                    finally { Console.Write("\x1b[?25l"); }
                }
                try
                {
                    var success = _host.Tunnel.StartChatGptIfConfiguredAsync(_host.BoundLocalUrl).GetAwaiter().GetResult();
                    _statusBanner = success ? "Private ChatGPT tunnel process launched. Select existing tunnel in ChatGPT." : "Tunnel not configured.";
                }
                catch (Exception ex) { _statusBanner = $"Tunnel error: {ex.Message}"; }
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
                if (ChatGptTunnelSetup.IsConfigured(_host.Paths))
                {
                    // A tunnel profile reads the RAMCA key at startup. Reconnect after rotation.
                    _host.Tunnel.Stop();
                    try { _host.Tunnel.StartChatGptIfConfiguredAsync(_host.BoundLocalUrl).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _statusBanner = $"Key rotated, tunnel restart failed: {ex.Message}"; break; }
                }
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

    private static bool HasInterestingResult(string name) =>
        name is "run_command" or "dotnet_build" or "dotnet_test" or "msbuild_run" or
            "compiler_diagnostics" or "replace_range" or "apply_patch" or
            "create_file" or "delete_file" or "revert_edit" or "rename_file" or
            "git_commit" or "git_diff" or "git_push";

    private static string? ReadResultField(object? data, string property)
    {
        if (data is null) return null;
        try
        {
            var value = data.GetType().GetProperty(property)?.GetValue(data);
            return value?.ToString();
        }
        catch { return null; }
    }

    private void CaptureToolResult(string name, ToolResult result, long durationMs)
    {
        if (!HasInterestingResult(name)) return;
        try
        {
            // RecordToolCall runs before OnToolCompleted. Match an unambiguous
            // durable row by tool, summary and elapsed time. Never attribute a
            // result to a guessed call when concurrent operations look alike.
            var now = DateTimeOffset.UtcNow;
            var summary = _host.Credentials.Redact(result.Summary);
            var matches = _host.Database.ListToolCalls(null, 30)
                .Where(c => c.ToolName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    c.Summary == summary && Math.Abs(c.DurationMs - durationMs) <= 2 &&
                    (now - c.CreatedAtUtc).Duration() <= TimeSpan.FromSeconds(5))
                .ToArray();
            if (matches.Length != 1) return;

            string? Safe(string? value, int max = 5000)
            {
                if (value is null) return null;
                value = _host.Credentials.Redact(value);
                return value.Length <= max ? value : value[..max] + " ...[preview capped]";
            }
            var data = result.Data;
            string? tests = null;
            var passed = ReadResultField(data, "passed");
            if (passed is not null)
                tests = $"{passed} passed, {ReadResultField(data, "failed") ?? "?"} failed, {ReadResultField(data, "skipped") ?? "?"} skipped";
            var capture = new CapturedOperation(matches[0].Id, matches[0].CreatedAtUtc,
                name, result.Success, summary,
                Safe(ReadResultField(data, "outputExcerpt")),
                Safe(ReadResultField(data, "diff")),
                Safe(ReadResultField(data, "path"), 800),
                Safe(ReadResultField(data, "executable"), 300),
                Safe(ReadResultField(data, "exitCode"), 30),
                Safe(tests, 150));
            lock (_resultsLock)
            {
                if (_recentResults.Count == MaxCapturedOperations) _recentResults.Dequeue();
                _recentResults.Enqueue(capture);
            }
        }
        catch { /* Presentation capture must never interfere with a real tool. */ }
    }

    private CapturedOperation? CapturedFor(long id)
    {
        lock (_resultsLock)
            return _recentResults.FirstOrDefault(e => e.CallId == id);
    }

    private List<CapturedOperation> RecentDirectCommands()
    {
        lock (_resultsLock)
            return _recentResults.Where(e => e.ToolName is
                "run_command" or "dotnet_build" or "dotnet_test" or "msbuild_run" or
                "compiler_diagnostics" or "git_commit" or "git_diff" or "git_push")
                .ToList();
    }

    private void AddCapturedOutput(List<UiLine> lines, CapturedOperation captured, int remaining, int width)
    {
        if (remaining <= 0) return;
        if (!string.IsNullOrWhiteSpace(captured.Path))
            lines.Add(new UiLine(Fit("  File: " + captured.Path, width), UiTone.Muted));
        if (!string.IsNullOrWhiteSpace(captured.Executable))
            lines.Add(new UiLine(Fit($"  {captured.Executable} · exit {captured.ExitCode ?? "?"}", width), UiTone.Muted));
        if (!string.IsNullOrWhiteSpace(captured.Tests))
            lines.Add(new UiLine(Fit("  Tests: " + captured.Tests, width), UiTone.Good));

        var preview = captured.Diff ?? captured.Excerpt;
        if (string.IsNullOrWhiteSpace(preview)) return;
        var entries = preview.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // A short card shows the useful tail; expanded details allow the longer
        // bounded excerpt without loading an entire historical log.
        var budget = Math.Max(0, remaining - (lines.Count > 0 ? 1 : 0));
        var offset = Math.Max(0, entries.Length - (_focusDetails ? (_detailPage + 1) * Math.Max(1, budget) : budget));
        foreach (var entry in entries.Skip(offset).Take(budget))
        {
            var trimmed = entry.TrimEnd('\r');
            var tone = trimmed.StartsWith('+') && !trimmed.StartsWith("+++") ? UiTone.Good :
                trimmed.StartsWith('-') && !trimmed.StartsWith("---") ||
                trimmed.Contains("[err]", StringComparison.OrdinalIgnoreCase) ? UiTone.Error :
                trimmed.StartsWith("@@") ? UiTone.Accent :
                trimmed.Contains("warning", StringComparison.OrdinalIgnoreCase) ? UiTone.Warning : UiTone.Muted;
            lines.Add(new UiLine("  " + Fit(trimmed, width - 2), tone));
        }
    }

    private sealed record CommandItem(DateTimeOffset When, JobRecord? Job, CapturedOperation? Direct);

    private List<CommandItem> RecentCommands()
    {
        // Fixed-size, blended page: durable jobs plus live direct tool results.
        // The latter are intentionally not claimed to persist across restarts.
        var jobs = _host.Database.ListJobs(limit: 60);
        var direct = RecentDirectCommands();
        return jobs.Select(j => new CommandItem(j.CreatedAtUtc, j, null))
            .Concat(direct.Select(d => new CommandItem(d.TimestampUtc, null, d)))
            .OrderByDescending(item => item.When)
            .Take(100)
            .ToList();
    }

    private void SelectTab(ViewTab tab)
    {
        _activeTab = tab;
        _scrollOffset = 0;
        _detailPage = 0;
        _focusDetails = false;
        _cachedDiffKey = null;
        _cachedLogKey = null;
    }

    private bool HasItemAt(int index)
    {
        if (index < 0) return false;
        return _activeTab switch
        {
            ViewTab.Overview => _host.Database.ListToolCalls(_host.Database.ActiveSessionId, 1, index).Count > 0,
            ViewTab.Edits => index < _host.Database.ListRecentEdits(_host.Database.ActiveSessionId, 100).Count,
            ViewTab.Jobs => index < RecentCommands().Count,
            ViewTab.GitAndSessions => _host.Database.ListSessions(1, index).Count > 0,
            ViewTab.Errors => _host.Database.ListToolCalls(null, 1, index, errorsOnly: true).Count > 0,
            _ => false
        };
    }

    private void MoveSelection(int amount)
    {
        if (_focusDetails)
        {
            // Detail pages move through a bounded diff preview or saved command log.
            _detailPage = Math.Clamp(_detailPage - Math.Sign(amount), 0, 100_000);
            return;
        }

        var desired = (int)Math.Clamp((long)_scrollOffset + amount, 0, 100_000);
        if (desired < _scrollOffset)
            _scrollOffset = desired;
        else if (desired > _scrollOffset)
        {
            // PageDown may overshoot the last item. Find the last existing row
            // without loading an unbounded collection.
            for (var n = desired; n > _scrollOffset; n--)
            {
                if (!HasItemAt(n)) continue;
                _scrollOffset = n;
                break;
            }
        }
        _detailPage = 0;
        _cachedDiffKey = null;
        _cachedLogKey = null;
    }

    private void RenderFrame()
    {
        var w = Math.Max(20, SafeWidth());
        var h = Math.Max(16, SafeHeight());
        _lastWidth = w;
        _lastHeight = h;
        var sb = new StringBuilder(8192);
        sb.Append("\x1b[H");

        var highContrast = string.Equals(_host.Config.Tui.Theme, "high_contrast", StringComparison.OrdinalIgnoreCase);
        string Style(string s, UiTone tone) => !_useColor ? s : tone switch
        {
            UiTone.Good => $"\x1b[32m{s}\x1b[0m",
            UiTone.Warning => $"\x1b[33m{s}\x1b[0m",
            UiTone.Error => $"\x1b[31m{s}\x1b[0m",
            UiTone.Accent => $"\x1b[36m{s}\x1b[0m",
            UiTone.Muted when !highContrast => $"\x1b[2m{s}\x1b[0m",
            _ => s
        };
        void Line(string raw)
        {
            // Clip ANSI-aware text to the physical terminal width. Output never
            // wraps and leaves ghosts from a longer previous frame.
            sb.Append(ClipAnsi(raw, w - 1)).Append("\x1b[0m\x1b[K\n");
        }

        using var self = Process.GetCurrentProcess();
        var ram = self.WorkingSet64 / (1024.0 * 1024.0);
        var status = _host.Registry.IsPaused ? Style("Paused", UiTone.Warning) : Style("Online", UiTone.Good);
        var workspace = _host.Guard.ActiveWorkspace ?? "(none)";
        var currentSession = _host.Database.ActiveSessionId;
        Line($"LocalIdeBridge  {status}  {Style($"| Tools {_host.Registry.All.Count} | Calls {_host.Registry.TotalInvocations} | Jobs {_host.Jobs.ActiveJobCount} | RAM {ram:F1} MB", UiTone.Muted)}");
        Line($"Endpoint  : {Style(_host.Instructions.GetMcpEndpoint(), UiTone.Accent)}");
        Line($"Key       : {_host.Credentials.GetMasked()}  Session: {currentSession}");
        Line($"Workspace : {workspace}  Branch: {_cachedGitBranch ?? "-"}");
        Line(Style(new string('─', Math.Max(1, w - 1)), UiTone.Muted));
        string Tab(ViewTab tab, string label) =>
            _activeTab == tab ? (_useColor ? $"\x1b[1m[{label}]\x1b[0m" : $"[{label}]")
                              : Style($" {label} ", UiTone.Muted);
        Line($"{Tab(ViewTab.Overview, "1:Activity")} {Tab(ViewTab.Edits, "2:Changes")} {Tab(ViewTab.Jobs, "3:Commands")} {Tab(ViewTab.GitAndSessions, "4:Git")} {Tab(ViewTab.Errors, "5:Errors")}");

        var bodyRows = Math.Max(1, h - 11);
        var rows = BuildVisibleRows(bodyRows, w - 2);
        for (var i = 0; i < bodyRows; i++)
        {
            var row = i < rows.Count ? rows[i] : new UiLine("");
            Line(Style(row.Text, row.Tone));
        }

        Line(Style(new string('─', Math.Max(1, w - 1)), UiTone.Muted));
        Line(Style("[Up/Down] Select  [PgUp/PgDn] Page  [Enter] Details  [Esc] Back", UiTone.Muted));
        Line("[C] ChatGPT  [E] Endpoint  [K] Key  [I] Instructions  [R] Rotate");
        Line("[W] Workspace  [S] Session  [P] Pause/Resume  [N] Note  [Q] Quit");
        sb.Append(ClipAnsi(Style(Fit(_statusBanner, w - 1), UiTone.Muted), w - 1)).Append("\x1b[0m\x1b[K");
        Console.Write(sb.ToString());
    }

    private List<UiLine> BuildVisibleRows(int count, int width)
    {
        var lines = new List<UiLine>(count);
        try
        {
            switch (_activeTab)
            {
                case ViewTab.Overview: AddActivityRows(lines, count, width); break;
                case ViewTab.Edits: AddEditRows(lines, count, width); break;
                case ViewTab.Jobs: AddCommandRows(lines, count, width); break;
                case ViewTab.GitAndSessions: AddGitRows(lines, count, width); break;
                case ViewTab.Errors: AddErrorRows(lines, count, width); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            lines.Add(new UiLine("  View unavailable: " + Fit(ex.Message, width - 22), UiTone.Error));
        }
        return lines.Count <= count ? lines : lines.Take(count).ToList();
    }

    private void AddActivityRows(List<UiLine> lines, int count, int width)
    {
        var slots = _focusDetails ? 1 : Math.Clamp((count - 5) / 2, 1, 8);
        _lastListSlots = slots;
        var first = _focusDetails ? _scrollOffset : Math.Max(0, _scrollOffset - slots + 1);
        var calls = _host.Database.ListToolCalls(_host.Database.ActiveSessionId, slots, first);
        if (calls.Count == 0)
        {
            lines.Add(new UiLine("  No recorded operations for this session.", UiTone.Muted));
            return;
        }

        ToolCallRecord? selected = null;
        foreach (var (call, i) in calls.Select((v, i) => (v, i)))
        {
            var active = first + i == _scrollOffset;
            if (active) selected = call;
            if (_focusDetails && !active) continue;
            var tag = call.Success ? "OK" : "ERR";
            var friendly = FriendlyToolName(call.ToolName);
            lines.Add(new UiLine(
                Fit($"{(active ? '›' : ' ')} {tag,-3} {call.CreatedAtUtc.ToLocalTime():HH:mm:ss}  {friendly}  {FormatDuration(call.DurationMs)}", width),
                active ? UiTone.Accent : call.Success ? UiTone.Good : UiTone.Error));
            if (!_focusDetails)
                lines.Add(new UiLine(Fit($"     {call.Summary}", width), UiTone.Muted));
        }

        if (selected is null) return;
        lines.Add(new UiLine("  ── Selected operation ──", UiTone.Accent));
        lines.Add(new UiLine(Fit($"  {selected.ToolName}  ·  {selected.Category}  ·  {FormatDuration(selected.DurationMs)}", width)));
        lines.Add(new UiLine(Fit($"  {selected.Summary}", width), selected.Success ? UiTone.Normal : UiTone.Error));
        if (!string.IsNullOrEmpty(selected.ErrorCode))
            lines.Add(new UiLine("  Error: " + selected.ErrorCode, UiTone.Error));
        lines.Add(new UiLine(Fit("  Workspace: " + selected.Workspace, width), UiTone.Muted));
        var capture = CapturedFor(selected.Id);
        if (capture is not null)
            AddCapturedOutput(lines, capture, Math.Max(0, count - lines.Count - 1), width);
        else if (selected.Category == "editing")
            lines.Add(new UiLine("  [2] Changes: recorded edits and verified file diffs.", UiTone.Muted));
        else if (selected.ToolName is "run_command" or "dotnet_build" or "dotnet_test" or "msbuild_run")
            lines.Add(new UiLine("  Output from older direct calls was not persisted in call records.", UiTone.Muted));
        if (!_focusDetails && lines.Count < count)
            lines.Add(new UiLine("  Enter: expand  ·  Older operations: PgDown", UiTone.Muted));
    }

    private void AddEditRows(List<UiLine> lines, int count, int width)
    {
        // The existing edit journal API offers the 100 most recent edits. We
        // never invent older pages or scan unbounded history.
        var entries = _host.Database.ListRecentEdits(_host.Database.ActiveSessionId, 100);
        if (entries.Count == 0)
        {
            lines.Add(new UiLine("  No file edits recorded in this session.", UiTone.Muted));
            return;
        }

        _scrollOffset = Math.Min(_scrollOffset, entries.Count - 1);
        var selected = entries[_scrollOffset];
        var slots = _focusDetails ? 0 : Math.Clamp(count / 3, 2, 8);
        _lastListSlots = Math.Max(1, slots);
        if (!_focusDetails)
        {
            var from = Math.Max(0, _scrollOffset - slots + 1);
            for (var i = from; i < Math.Min(entries.Count, from + slots); i++)
            {
                var e = entries[i];
                lines.Add(new UiLine(
                    Fit($"{(i == _scrollOffset ? '›' : ' ')} {e.CreatedAtUtc.ToLocalTime():HH:mm:ss}  +{e.LinesAdded} -{e.LinesRemoved}  {CompactPath(e.RelativePath, Math.Max(8, width - 27))}", width),
                    i == _scrollOffset ? UiTone.Accent : e.Reverted ? UiTone.Warning : UiTone.Normal));
            }
        }

        lines.Add(new UiLine("  ── " + (selected.Reverted ? "Reverted edit" : "Recorded edit") + " ──", UiTone.Accent));
        lines.Add(new UiLine(Fit($"  {selected.Operation}  +{selected.LinesAdded} -{selected.LinesRemoved}  {selected.CreatedAtUtc.ToLocalTime():HH:mm:ss}", width)));
        lines.Add(new UiLine(Fit("  " + selected.RelativePath, width), UiTone.Muted));
        var preview = GetVerifiedDiff(selected);
        var visible = Math.Max(0, count - lines.Count - 1);
        var start = _focusDetails ? Math.Min(preview.Count, _detailPage * Math.Max(1, visible)) : 0;
        foreach (var line in preview.Skip(start).Take(visible))
            lines.Add(new UiLine("  " + Fit(line.Text, width - 2), line.Tone));
        if (lines.Count < count)
            lines.Add(new UiLine(
                _focusDetails ? $"  Diff lines {start + 1}–{Math.Min(preview.Count, start + visible)} of {preview.Count} · PgUp/PgDn: navigate"
                              : "  Enter: larger diff · PgDown: older edits",
                UiTone.Muted));
    }

    private IReadOnlyList<UiLine> GetVerifiedDiff(EditJournalRecord edit)
    {
        var result = new List<UiLine>();
        string cacheKey = edit.EditId;
        try
        {
            using var scope = _host.Guard.UseInvocationWorkspace(edit.Workspace);
            var full = _host.Guard.ResolvePath(edit.RelativePath, explicitWorkspace: edit.Workspace);
            var backup = Path.Combine(_host.Paths.SessionsDir, PortablePaths.SanitizeId(edit.SessionId),
                "edits", PortablePaths.SanitizeId(edit.EditId) + ".bak");
            // Cheap file metadata invalidates the preview if another editor
            // updates a file without sending RAMCA an activity notification.
            cacheKey = $"{edit.EditId}:{File.GetLastWriteTimeUtc(full).Ticks}:{File.Exists(full)}:{File.GetLastWriteTimeUtc(backup).Ticks}";
            if (_cachedDiffKey == cacheKey) return _cachedDiffLines;
            const long maxPreviewBytes = 256 * 1024;
            // Reject large previews before hashing or decoding them on every
            // TUI redraw. The edit metadata is still displayed.
            if ((File.Exists(full) && new FileInfo(full).Length > maxPreviewBytes) ||
                (edit.ExistedBefore && File.Exists(backup) && new FileInfo(backup).Length > maxPreviewBytes))
                throw new InvalidOperationException("Preview limited to 256 KiB per file; counts remain available.");
            string? currentHash = File.Exists(full) ? EditingTools.ComputeFileSha256(full) : null;
            if (edit.AfterSha256 == "deleted")
            {
                if (currentHash is not null)
                    throw new InvalidOperationException("File has been recreated since this edit; historical diff unavailable.");
            }
            else if (!string.Equals(currentHash, edit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File changed after this edit; showing its original diff would be misleading.");

            if (edit.ExistedBefore && !File.Exists(backup))
                throw new FileNotFoundException("Verified edit backup is no longer available.");
            string[] before = [];
            if (edit.ExistedBefore)
            {
                if (!string.Equals(EditingTools.ComputeFileSha256(backup), edit.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original backup hash differs; preview suppressed.");
                before = EditingTools.LoadTextFile(backup).Lines;
            }
            var after = edit.AfterSha256 == "deleted" ? [] : EditingTools.LoadTextFile(full).Lines;
            var diff = EditingTools.BuildCompactUnifiedDiff(edit.RelativePath, before, after, maxChars: 20_000);
            foreach (var raw in diff.Split('\n'))
            {
                if (raw.StartsWith("--- ") || raw.StartsWith("+++ ")) continue;
                var tone = raw.StartsWith('+') ? UiTone.Good :
                    raw.StartsWith('-') ? UiTone.Error :
                    raw.StartsWith("@@") ? UiTone.Accent : UiTone.Muted;
                result.Add(new UiLine(_host.Credentials.Redact(raw.TrimEnd('\r')), tone));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or WorkspaceViolationException or UnauthorizedAccessException or ArgumentException)
        {
            result.Add(new UiLine(ex.Message, UiTone.Warning));
            result.Add(new UiLine("Only trustworthy, matching pre/post-image diffs are displayed.", UiTone.Muted));
        }
        _cachedDiffKey = cacheKey;
        _cachedDiffLines = result;
        return result;
    }

    private void AddCommandRows(List<UiLine> lines, int count, int width)
    {
        var commands = RecentCommands();
        if (commands.Count == 0)
        {
            lines.Add(new UiLine("  No command or job results yet.", UiTone.Muted));
            return;
        }
        _scrollOffset = Math.Min(_scrollOffset, commands.Count - 1);
        var slots = _focusDetails ? 0 : Math.Clamp(count / 3, 2, 8);
        _lastListSlots = Math.Max(1, slots);
        if (!_focusDetails)
        {
            var from = Math.Max(0, _scrollOffset - slots + 1);
            for (var i = from; i < Math.Min(commands.Count, from + slots); i++)
            {
                var entry = commands[i];
                var status = entry.Job?.Status ?? (entry.Direct!.Success ? "succeeded" : "failed");
                var tool = entry.Job?.ToolName ?? entry.Direct!.ToolName;
                var duration = entry.Job?.StartedAtUtc is { } started
                    ? FormatDuration((long)Math.Max(0,
                        ((entry.Job.CompletedAtUtc ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds))
                    : entry.Job is not null ? "queued" : "direct";
                lines.Add(new UiLine(
                    Fit($"{(i == _scrollOffset ? '›' : ' ')} {status,-10} {FriendlyToolName(tool)}  {duration}", width),
                    i == _scrollOffset ? UiTone.Accent : StatusTone(status)));
            }
        }

        var selected = commands[_scrollOffset];
        lines.Add(new UiLine(selected.Job is null ? "  ── Live command result ──" : "  ── Background job details ──", UiTone.Accent));
        if (selected.Direct is { } direct)
        {
            lines.Add(new UiLine(Fit($"  {direct.ToolName}: {direct.Summary}", width),
                direct.Success ? UiTone.Good : UiTone.Error));
            AddCapturedOutput(lines, direct, Math.Max(0, count - lines.Count - 1), width);
            if (lines.Count < count)
                lines.Add(new UiLine(_focusDetails ? "  PgUp/PgDn: preview pages · Esc: list"
                    : "  Enter: expanded live output · PgDown: older commands", UiTone.Muted));
            return;
        }

        var job = selected.Job!;
        lines.Add(new UiLine(Fit($"  {job.ToolName}  [{job.Status}]  {job.Id}", width), StatusTone(job.Status)));
        lines.Add(new UiLine(Fit($"  Workspace: {job.Workspace}", width), UiTone.Muted));
        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
            lines.Add(new UiLine(Fit("  " + job.ErrorMessage, width), UiTone.Error));

        try
        {
            if (!string.IsNullOrWhiteSpace(job.ResultJson) &&
                JsonNode.Parse(job.ResultJson) is JsonObject result)
            {
                var data = result["data"] as JsonObject;
                var exe = data?["executable"]?.ToString();
                var exit = data?["exitCode"]?.ToString();
                if (!string.IsNullOrWhiteSpace(exe))
                    lines.Add(new UiLine(Fit($"  Executable: {exe}" + (exit is null ? "" : $" · exit {exit}"), width)));
                if (data?["total"] is not null)
                    lines.Add(new UiLine(Fit($"  Tests: {data["passed"]} passed · {data["failed"]} failed · {data["skipped"]} skipped", width),
                        data["failed"]?.ToString() == "0" ? UiTone.Good : UiTone.Warning));
            }
        }
        catch (JsonException) { /* Older or partial result: show durable metadata. */ }

        var remaining = Math.Max(0, count - lines.Count - 1);
        if (remaining <= 0) return;
        var output = GetJobLogPreview(job, remaining);
        foreach (var line in output.Take(remaining))
            lines.Add(new UiLine("  " + Fit(line.Text, width - 2), line.Tone));
        if (lines.Count < count)
            lines.Add(new UiLine(_focusDetails ? "  PgUp: older output · PgDown: newer · Esc: list"
                        : "  Enter: larger output · PgDown: older commands", UiTone.Muted));
    }

    private IReadOnlyList<UiLine> GetJobLogPreview(JobRecord job, int maxRows)
    {
        if (string.IsNullOrWhiteSpace(job.LogExecId))
        {
            var rows = new List<UiLine>();
            if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
                rows.Add(new UiLine(job.ErrorMessage!, UiTone.Error));
            if (rows.Count == 0)
                rows.Add(new UiLine(job.Status is "running" or "queued"
                    ? "Job active; output appears when its execution log is linked."
                    : "No execution log was linked to this job.", UiTone.Muted));
            return rows;
        }
        try
        {
            var meta = _host.Database.GetLogMeta(job.LogExecId);
            if (meta is null) return [new UiLine("Execution log metadata unavailable.", UiTone.Warning)];
            var pageSize = Math.Max(1, maxRows);
            var start = Math.Max(1, meta.Value.TotalLines - (_detailPage + 1) * pageSize + 1);
            var key = $"{job.LogExecId}:{meta.Value.TotalLines}:{pageSize}:{start}";
            if (_cachedLogKey == key) return _cachedLogLines;
            var slice = _host.Logs.ReadRange(job.LogExecId, start, pageSize);
            var rows = new List<UiLine>();
            if (meta.Value.Truncated) rows.Add(new UiLine("Output capped by RAMCA's configured log limit.", UiTone.Warning));
            if (start > 1) rows.Add(new UiLine($"Showing saved log lines {start}–{Math.Min(meta.Value.TotalLines, start + pageSize - 1)} / {meta.Value.TotalLines}.", UiTone.Muted));
            foreach (var entry in slice.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(pageSize))
            {
                var tone = entry.Contains("[err]", StringComparison.OrdinalIgnoreCase) ||
                    entry.Contains(": error ", StringComparison.OrdinalIgnoreCase) ? UiTone.Error :
                    entry.Contains("warning", StringComparison.OrdinalIgnoreCase) ? UiTone.Warning :
                    entry.Contains("[meta]", StringComparison.OrdinalIgnoreCase) ? UiTone.Muted : UiTone.Normal;
                rows.Add(new UiLine(entry.TrimEnd('\r'), tone));
            }
            _cachedLogKey = key;
            _cachedLogLines = rows;
            return rows;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return [new UiLine("Execution log unavailable: " + ex.Message, UiTone.Warning)];
        }
    }

    private void AddGitRows(List<UiLine> lines, int count, int width)
    {
        _lastListSlots = Math.Max(1, count - 5);
        lines.Add(new UiLine($"  Active workspace: {CompactPath(_host.Guard.ActiveWorkspace ?? "-", width - 20)}", UiTone.Accent));
        lines.Add(new UiLine($"  Branch: {_cachedGitBranch ?? "unknown"}", UiTone.Muted));
        var checkpoints = _host.Database.ListGitCheckpoints(limit: Math.Min(3, Math.Max(1, count / 4)));
        lines.Add(new UiLine("  Saved Git checkpoints:", UiTone.Accent));
        if (checkpoints.Count == 0) lines.Add(new UiLine("    None saved in this session.", UiTone.Muted));
        foreach (var point in checkpoints)
            lines.Add(new UiLine(Fit($"    {point.CommitSha[..Math.Min(8, point.CommitSha.Length)]}  {point.Label}", width)));

        lines.Add(new UiLine("  Sessions (bounded history):", UiTone.Accent));
        var sessions = _host.Database.ListSessions(limit: Math.Max(1, count - lines.Count), offset: _scrollOffset);
        if (sessions.Count == 0) lines.Add(new UiLine("    No sessions at this position.", UiTone.Muted));
        foreach (var (session, index) in sessions.Select((v, i) => (v, i)))
            lines.Add(new UiLine(Fit(
                $"    {(_scrollOffset + index == _scrollOffset ? '›' : ' ')} {session.Status,-7} {session.Id}  calls {session.ToolCallCount}  err {session.ErrorCount}", width)));
    }

    private void AddErrorRows(List<UiLine> lines, int count, int width)
    {
        var slots = _focusDetails ? 1 : Math.Clamp((count - 4) / 2, 1, 8);
        _lastListSlots = slots;
        var first = _focusDetails ? _scrollOffset : Math.Max(0, _scrollOffset - slots + 1);
        var errors = _host.Database.ListToolCalls(null, slots, first, errorsOnly: true);
        if (errors.Count == 0)
        {
            lines.Add(new UiLine("  No errors recorded.", UiTone.Good));
            return;
        }
        foreach (var (error, i) in errors.Select((v, i) => (v, i)))
        {
            var active = first + i == _scrollOffset;
            lines.Add(new UiLine(Fit($"{(active ? '›' : ' ')} {error.CreatedAtUtc.ToLocalTime():HH:mm:ss}  {error.ToolName}  {FormatDuration(error.DurationMs)}", width),
                active ? UiTone.Accent : UiTone.Error));
            if (!_focusDetails) lines.Add(new UiLine(Fit("     " + error.Summary, width), UiTone.Muted));
            if (active)
            {
                // Preserve the diagnostic fields, never synthesize a root cause.
                if (_focusDetails)
                {
                    lines.Add(new UiLine("  ── Recorded failure ──", UiTone.Error));
                    lines.Add(new UiLine(Fit($"  Code: {error.ErrorCode ?? "(none)"}  ·  {error.Category}", width)));
                    lines.Add(new UiLine(Fit("  " + error.Summary, width), UiTone.Error));
                    lines.Add(new UiLine(Fit("  Workspace: " + error.Workspace, width), UiTone.Muted));
                }
            }
        }
    }

    private static UiTone StatusTone(string status) => status switch
    {
        "succeeded" => UiTone.Good,
        "running" or "queued" => UiTone.Warning,
        "cancelled" or "interrupted_restart" => UiTone.Muted,
        _ => UiTone.Error
    };

    private static string FriendlyToolName(string tool) => tool switch
    {
        "replace_range" => "Edit lines",
        "apply_patch" => "Apply patch",
        "create_file" => "Create file",
        "delete_file" => "Delete file",
        "rename_file" => "Rename file",
        "revert_edit" => "Undo edit",
        "run_command" => "Run command",
        "dotnet_build" => ".NET build",
        "dotnet_test" => ".NET tests",
        "msbuild_run" => "MSBuild",
        "job_start" => "Start job",
        "job_status" => "Job status",
        "git_commit" => "Git commit",
        "git_push" => "Git push",
        _ => tool.Replace('_', ' ')
    };

    private static string FormatDuration(long ms)
    {
        ms = Math.Max(0, ms);
        if (ms < 1_000) return $"{ms / 1_000.0:0.###}s";
        if (ms < 60_000) return $"{ms / 1_000.0:0.#}s";
        var seconds = ms / 1_000;
        if (seconds < 3_600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3_600}h {(seconds % 3_600) / 60}m {seconds % 60}s";
    }

    private static string CompactPath(string path, int max)
    {
        max = Math.Max(5, max);
        var clean = path.Replace('\\', '/');
        return clean.Length <= max ? clean : "…/" + clean[^Math.Max(1, max - 2)..];
    }

    private static string ClipAnsi(string source, int maxColumns)
    {
        // Preserve only SGR color escapes. Strip arbitrary cursor/control bytes
        // from file contents and tool logs so an operation cannot rewrite the TUI.
        var output = new StringBuilder(Math.Min(source.Length, Math.Max(0, maxColumns) + 48));
        var visible = 0;
        for (var i = 0; i < source.Length && visible < maxColumns; i++)
        {
            var ch = source[i];
            if (ch == '\x1b')
            {
                if (i + 1 < source.Length && source[i + 1] == '[')
                {
                    var end = i + 2;
                    while (end < source.Length && end - i < 24 &&
                           (char.IsAsciiDigit(source[end]) || source[end] == ';')) end++;
                    if (end < source.Length && source[end] == 'm')
                    {
                        output.Append(source, i, end - i + 1);
                        i = end;
                    }
                }
                continue;
            }
            if (ch is '\r' or '\n' or '\t') { output.Append(' '); visible++; continue; }
            if (char.IsControl(ch)) continue;
            output.Append(ch);
            visible++;
        }
        return output.ToString();
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
