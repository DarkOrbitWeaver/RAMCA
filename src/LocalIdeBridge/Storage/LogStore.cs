using System.Text;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Storage;

public sealed record LogSliceResult(
    string ExecutionId,
    string Category,
    int StartLine,
    int EndLine,
    int ReturnedLines,
    int TotalLines,
    long TotalBytes,
    bool HasMore,
    bool WasCapTruncated,
    string Content);

/// <summary>
/// Disk-backed streaming command log writer and paginated line-range reader.
/// Never loads full command outputs into memory.
/// </summary>
public sealed class LogStore
{
    private readonly PortablePaths _paths;
    private readonly StateDatabase _db;
    private readonly CredentialStore _credentials;
    private readonly RetentionSettings _retention;

    public LogStore(PortablePaths paths, StateDatabase db, CredentialStore credentials, RetentionSettings retention)
    {
        _paths = paths;
        _db = db;
        _credentials = credentials;
        _retention = retention;
    }

    public StreamingLogWriter OpenWriter(string execId, string sessionId, string category)
    {
        var safeExec = PortablePaths.SanitizeId(execId);
        var logsDir = _paths.GetSessionLogsDir(sessionId);
        var filePath = Path.Combine(logsDir, $"{safeExec}.log");
        return new StreamingLogWriter(safeExec, sessionId, category, filePath, _db, _credentials, _retention);
    }

    public LogSliceResult ReadRange(string execId, int startLine = 1, int maxLines = 150, string? containsFilter = null)
    {
        var safeExec = PortablePaths.SanitizeId(execId);
        var meta = _db.GetLogMeta(safeExec)
            ?? throw new KeyNotFoundException($"Execution log '{safeExec}' not found.");

        // The DB stores a legacy absolute path, but the portable folder may have
        // moved. Derive the authoritative local path from session + execution IDs.
        var localPath = Path.Combine(_paths.GetSessionLogsDir(meta.SessionId), safeExec + ".log");
        if (!File.Exists(localPath))
            throw new FileNotFoundException($"Log file for execution '{safeExec}' is no longer on disk.");

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 500);
        var maxChars = _retention.MaxToolOutputChars;

        var sb = new StringBuilder(Math.Min(maxChars, 4096));
        var lineNo = 0;
        var returned = 0;
        var endLine = startLine - 1;
        var hasMore = false;

        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            lineNo++;
            if (lineNo < startLine) continue;

            if (!string.IsNullOrEmpty(containsFilter) &&
                !line.Contains(containsFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (returned >= maxLines || sb.Length >= maxChars)
            {
                hasMore = true;
                break;
            }

            var displayLine = line.Length > 2000 ? line[..2000] + " ...[line truncated]" : line;
            var formatted = $"{lineNo,6}: {displayLine}";
            if (sb.Length + formatted.Length + 1 > maxChars && returned > 0)
            {
                hasMore = true;
                break;
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(formatted);
            returned++;
            endLine = lineNo;
        }

        return new LogSliceResult(
            safeExec,
            meta.Category,
            startLine,
            returned > 0 ? endLine : startLine,
            returned,
            Math.Max(meta.TotalLines, lineNo),
            meta.TotalBytes,
            hasMore,
            meta.Truncated,
            sb.ToString());
    }
}

public sealed class StreamingLogWriter : IDisposable
{
    private readonly string _execId;
    private readonly string _sessionId;
    private readonly string _category;
    private readonly string _filePath;
    private readonly StateDatabase _db;
    private readonly CredentialStore _credentials;
    private readonly long _maxBytes;
    private readonly int _maxExcerptChars;
    private readonly object _lock = new();
    private readonly FileStream _fs;
    private readonly StreamWriter _sw;
    private readonly BoundedRingBuffer<string> _tailLines = new(60);
    private readonly List<string> _headLines = new(20);
    private int _totalLines;
    private long _totalBytes;
    private bool _truncated;
    private bool _completed;

    public string ExecutionId => _execId;
    public string FilePath => _filePath;
    public int TotalLines { get { lock (_lock) return _totalLines; } }
    public long TotalBytes { get { lock (_lock) return _totalBytes; } }
    public bool WasTruncated { get { lock (_lock) return _truncated; } }

    public StreamingLogWriter(
        string execId,
        string sessionId,
        string category,
        string filePath,
        StateDatabase db,
        CredentialStore credentials,
        RetentionSettings retention)
    {
        _execId = execId;
        _sessionId = sessionId;
        _category = category;
        _filePath = filePath;
        _db = db;
        _credentials = credentials;
        _maxBytes = retention.MaxSingleLogBytes;
        _maxExcerptChars = Math.Min(retention.MaxToolOutputChars, 6000);

        _fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192);
        _sw = new StreamWriter(_fs, new UTF8Encoding(false)) { AutoFlush = false };
        _db.RegisterLog(_execId, _sessionId, _filePath, _category, 0, 0, false);
    }

    public void WriteLine(string streamPrefix, string line)
    {
        var safe = _credentials.Redact(line);
        var tagged = string.IsNullOrEmpty(streamPrefix) ? safe : $"[{streamPrefix}] {safe}";

        lock (_lock)
        {
            if (_completed) return;
            _totalLines++;

            var shortLine = tagged.Length > 500 ? tagged[..500] + "..." : tagged;
            if (_headLines.Count < 20)
                _headLines.Add(shortLine);
            else
                _tailLines.Push(shortLine);

            if (_truncated) return;

            var byteEstimate = Encoding.UTF8.GetByteCount(tagged) + 1;
            if (_totalBytes + byteEstimate > _maxBytes)
            {
                _truncated = true;
                _sw.WriteLine($"--- [LocalIdeBridge: log capped at {_maxBytes} bytes] ---");
                _sw.Flush();
                return;
            }

            _sw.WriteLine(tagged);
            _totalBytes += byteEstimate;
            if (_totalLines % 32 == 0)
                _sw.Flush();
        }
    }

    public string BuildBoundedExcerpt()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var h in _headLines)
            {
                if (sb.Length + h.Length + 1 > _maxExcerptChars) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(h);
            }

            var tail = _tailLines.SnapshotLatest(40);
            if (tail.Count > 0)
            {
                var omitted = Math.Max(0, _totalLines - _headLines.Count - tail.Count);
                if (omitted > 0)
                    sb.Append($"\n... [{omitted} middle lines omitted; use read_log with execId='{_execId}' to inspect any range] ...");

                foreach (var t in tail)
                {
                    if (sb.Length + t.Length + 1 > _maxExcerptChars)
                    {
                        sb.Append($"\n... [excerpt capped; full log stored under execId='{_execId}']");
                        break;
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t);
                }
            }
            return sb.ToString();
        }
    }

    public void Complete()
    {
        int lines;
        long bytes;
        bool trunc;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            _sw.Flush();
            _sw.Dispose();
            _fs.Dispose();
            lines = _totalLines;
            bytes = _totalBytes;
            trunc = _truncated;
        }
        _db.RegisterLog(_execId, _sessionId, _filePath, _category, lines, bytes, trunc);
    }

    public void Dispose() => Complete();
}
