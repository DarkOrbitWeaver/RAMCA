using System.Text;

namespace LocalIdeBridge.Core;

public sealed record ActivityEvent(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string Summary,
    string? Detail = null,
    long? DurationMs = null,
    bool IsError = false);

/// <summary>
/// Low-overhead, streaming rotating file logger with automatic secret redaction
/// and a fixed-capacity ring buffer for event-driven TUI updates.
/// </summary>
public sealed class RedactingLogger : IDisposable
{
    private readonly PortablePaths _paths;
    private readonly CredentialStore _credentials;
    private readonly RetentionSettings _retention;
    private readonly object _fileLock = new();
    private StreamWriter? _writer;
    private string _currentDateKey = "";
    private long _currentFileBytes;
    private int _rollIndex;
    private bool _disposed;

    public BoundedRingBuffer<ActivityEvent> RecentEvents { get; }
    public event Action<ActivityEvent>? OnEvent;

    public RedactingLogger(PortablePaths paths, CredentialStore credentials, RetentionSettings retention, int eventBufferSize)
    {
        _paths = paths;
        _credentials = credentials;
        _retention = retention;
        RecentEvents = new BoundedRingBuffer<ActivityEvent>(eventBufferSize);
    }

    public void Info(string category, string message, string? detail = null, long? durationMs = null)
        => Write("INFO", category, message, detail, durationMs, isError: false);

    public void Warn(string category, string message, string? detail = null, long? durationMs = null)
        => Write("WARN", category, message, detail, durationMs, isError: false);

    public void Error(string category, string message, string? detail = null, long? durationMs = null)
        => Write("ERROR", category, message, detail, durationMs, isError: true);

    public void Write(string level, string category, string message, string? detail = null, long? durationMs = null, bool isError = false)
    {
        var now = DateTimeOffset.UtcNow;
        var safeMsg = Truncate(_credentials.Redact(message), 600);
        var safeDetail = detail is null ? null : Truncate(_credentials.Redact(detail), 1200);

        var evt = new ActivityEvent(now, level, category, safeMsg, safeDetail, durationMs, isError);
        RecentEvents.Push(evt);

        var line = durationMs.HasValue
            ? $"{now:O} [{level}] [{category}] {safeMsg} ({durationMs.Value}ms){(safeDetail is null ? "" : " | " + safeDetail)}"
            : $"{now:O} [{level}] [{category}] {safeMsg}{(safeDetail is null ? "" : " | " + safeDetail)}";

        lock (_fileLock)
        {
            if (!_disposed)
            {
                try
                {
                    EnsureWriterLocked(now);
                    _writer!.WriteLine(line);
                    _currentFileBytes += Encoding.UTF8.GetByteCount(line) + 1;
                }
                catch
                {
                    // Never allow logging I/O issues to disrupt the host.
                }
            }
        }

        try { OnEvent?.Invoke(evt); } catch { }
    }

    private void EnsureWriterLocked(DateTimeOffset now)
    {
        var dateKey = now.ToString("yyyyMMdd");
        if (_writer is not null && dateKey == _currentDateKey && _currentFileBytes < _retention.MaxSingleLogBytes)
            return;

        _writer?.Dispose();
        _writer = null;

        if (dateKey != _currentDateKey)
        {
            _currentDateKey = dateKey;
            _rollIndex = 0;
        }
        else
        {
            _rollIndex++;
        }

        Directory.CreateDirectory(_paths.LogsDir);
        var suffix = _rollIndex == 0 ? "" : $".{_rollIndex}";
        var filePath = Path.Combine(_paths.LogsDir, $"bridge-{dateKey}{suffix}.log");
        var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
        _currentFileBytes = fs.Length;
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public int PruneExpiredLogs()
    {
        if (!Directory.Exists(_paths.LogsDir)) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retention.MaxLogAgeDays);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.LogsDir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch { }
        }
        return removed;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s.ReplaceLineEndings(" ") : string.Concat(s.AsSpan(0, max), "...").ReplaceLineEndings(" ");

    public void Dispose()
    {
        lock (_fileLock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
