using System.Security.Cryptography;
using System.Text;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Core;

/// <summary>
/// Durable first-touch projection for RAMCA edit-journal paths. This is NOT a
/// whole-workspace or author-attributed net diff; unobserved shell/external edits
/// remain outside its coverage and must always be disclosed in the UI.
/// </summary>
public static class SessionChangeProjection
{
    public static SessionChangeSummary Compute(PortablePaths paths, WorkspaceGuard guard,
        StateDatabase db, string sessionId, CancellationToken ct = default)
    {
        const int maxEntries = 2500;
        const int maxFileBytes = 512 * 1024;
        const long maxComparisons = 10_000_000;

        var edits = db.ListSessionEdits(sessionId, maxEntries + 1);
        var incomplete = edits.Count > maxEntries;
        var limited = edits.Take(maxEntries).ToList();
        var byPath = limited.GroupBy(e =>
            WorkspaceGuard.NormalizeRoot(e.Workspace) + "\u0001" +
            (OperatingSystem.IsWindows() ? e.RelativePath.ToUpperInvariant() : e.RelativePath));
        var changed = 0;
        var added = 0;
        var removed = 0;
        var modified = 0;
        var created = 0;
        var deleted = 0;
        var byteOnly = 0;
        var unknown = 0;
        long comparisons = 0;
        foreach (var group in byPath)
        {
            ct.ThrowIfCancellationRequested();
            var first = group.First();
            var last = group.Last();
            try
            {
                using var scope = guard.UseInvocationWorkspace(first.Workspace);
                var full = guard.ResolvePath(first.RelativePath, explicitWorkspace: first.Workspace);
                var fileExists = File.Exists(full);
                if (fileExists &&
                    !string.Equals(last.AfterSha256, "deleted", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(last.AfterSha256, "none", StringComparison.OrdinalIgnoreCase))
                {
                    var hash = FileHash(full, maxFileBytes);
                    if (!string.Equals(hash, last.AfterSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        unknown++; // External modification or unjournaled operation.
                        continue;
                    }
                }
                else if (fileExists || last.AfterSha256 is not ("deleted" or "none"))
                {
                    unknown++;
                    continue;
                }

                byte[] before = [];
                if (first.ExistedBefore)
                {
                    var backup = Path.Combine(paths.GetSessionEditsDir(first.SessionId), first.EditId + ".bak");
                    if (!File.Exists(backup)) { unknown++; continue; }
                    before = ReadSmallBytes(backup, maxFileBytes);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(before));
                    if (!string.Equals(hash, first.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                    { unknown++; continue; }
                }
                var after = fileExists ? ReadSmallBytes(full, maxFileBytes) : [];
                if (fileExists && !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(after)), last.AfterSha256,
                    StringComparison.OrdinalIgnoreCase))
                { unknown++; continue; } // File changed during the comparison.
                if (before.AsSpan().SequenceEqual(after)) continue; // Reverts count as zero net.

                var beforeLines = DecodeTextLines(before);
                var afterLines = DecodeTextLines(after);
                if (beforeLines is null || afterLines is null ||
                    (long)beforeLines.Length * afterLines.Length > maxComparisons - comparisons)
                {
                    unknown++; // Binary/large files still have changed bytes, but exact text totals unavailable.
                    changed++;
                    continue;
                }

                comparisons += (long)beforeLines.Length * afterLines.Length;
                var common = LongestCommonSubsequence(beforeLines, afterLines, ct);
                added += afterLines.Length - common;
                removed += beforeLines.Length - common;
                changed++;
                if (!first.ExistedBefore && fileExists) created++;
                else if (first.ExistedBefore && !fileExists) deleted++;
                else modified++;
                if (beforeLines.Length == afterLines.Length && common == beforeLines.Length)
                    byteOnly++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                WorkspaceViolationException or InvalidOperationException or DecoderFallbackException)
            {
                unknown++;
            }
        }

        // Never claim a complete session total: arbitrary shells, builds, other
        // clients, and external editors can modify paths without journal entries.
        return new SessionChangeSummary(sessionId, changed, added, removed,
            created, modified, deleted, byteOnly, unknown + (incomplete ? 1 : 0),
            limited.Count, "first_touch_recorded_edits_only", DateTimeOffset.UtcNow);
    }

    private static string FileHash(string path, int limit)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.SequentialScan);
        if (fs.Length > limit) throw new IOException("Changed file exceeds diff budget.");
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static byte[] ReadSmallBytes(string path, int limit)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.SequentialScan);
        if (fs.Length > limit) throw new IOException("Session diff file exceeds 512 KiB limit.");
        using var output = new MemoryStream((int)fs.Length);
        fs.CopyTo(output);
        if (output.Length > limit) throw new IOException("File grew beyond session diff limit.");
        return output.ToArray();
    }

    private static string[]? DecodeTextLines(byte[] bytes)
    {
        if (bytes.Length == 0) return [];
        var bom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                                        (bytes[0] == 0xFE && bytes[1] == 0xFF));
        if (!bom && bytes.AsSpan(0, Math.Min(4096, bytes.Length)).Contains((byte)0)) return null;
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false),
            new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.Length == 0) return [];
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return normalized.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static int LongestCommonSubsequence(string[] a, string[] b, CancellationToken ct)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            if ((i & 63) == 0) ct.ThrowIfCancellationRequested();
            for (var j = 1; j <= b.Length; j++)
                curr[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1 : Math.Max(curr[j - 1], prev[j]);
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

public sealed record SessionChangeSummary(
    string SessionId, int ChangedFiles, int AddedLines, int RemovedLines,
    int CreatedFiles, int ModifiedFiles, int DeletedFiles, int ByteOnlyFiles,
    int UnknownFiles, int RecordedOperations, string Scope,
    DateTimeOffset ObservedAtUtc);
