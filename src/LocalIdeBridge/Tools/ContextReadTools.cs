using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Single MCP call for many independent file ranges. No retained source index,
/// no workspace mutations, one bounded immutable byte read per distinct file.
/// </summary>
public static class ContextReadTools
{
    private sealed record RequestedRange(int Start, int End);
    private sealed record RequestedFile(string Path, List<RequestedRange> Ranges);

    public static IEnumerable<ToolDefinition> Create(WorkspaceGuard guard, AppConfig config)
    {
        yield return new ToolDefinition(
            Name: "read_context",
            Category: "editing",
            Description: "Reads up to 32 workspace files and 64 bounded line ranges in one call. One canonical source representation per range, one SHA-256 per file, independent errors, and a strict total serialized UTF-8 byte cap. Prefer for related code inspection.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None; snapshot-like read with per-file version and partial-read reporting.",
            Parameters:
            [
                new("items", "array", "Ordered [{path, ranges?:[[startLine,endLine], ...]}]. Omitted ranges reads from line 1 up to the output cap.", Required: true,
                    ItemsSchema: new JsonObject { ["type"] = "object" }),
                new("maxBytes", "integer", "Hard cap on serialized tool data, 2048..65536 bytes. Defaults to 24000.", Default: 24000),
                new("includeVersion", "boolean", "Include the SHA-256 of each exact byte buffer, usable as an edit precondition.", Default: true),
                new("includeTotalLines", "boolean", "Return exact total line count from the decoded buffer.", Default: false)
            ],
            Handler: (args, _, ct) =>
            {
                var requested = ParseItems(args);
                var maxBytes = Math.Clamp(args.OptionalInt("maxBytes", 24_000), 2_048, 65_536);
                var includeVersion = args.OptionalBool("includeVersion", true);
                var includeTotal = args.OptionalBool("includeTotalLines", false);
                var ws = guard.RequireActiveWorkspace();

                // Keep file buffers transient and strictly bounded; no global cache
                // or retained source text survives this invocation.
                var files = new JsonArray();
                var data = new JsonObject
                {
                    ["root"] = ws,
                    ["files"] = files,
                    ["requestedCount"] = requested.Count,
                    ["complete"] = true,
                    ["maxBytes"] = maxBytes
                };
                var mapped = new Dictionary<string, JsonObject>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                const long maxFileBytes = 4L * 1024 * 1024;
                var totalRanges = 0;

                foreach (var entry in requested)
                {
                    ct.ThrowIfCancellationRequested();
                    string full, relative;
                    try
                    {
                        full = guard.ResolvePath(entry.Path);
                        relative = guard.GetRelativePathUnix(ws, full);
                    }
                    catch (WorkspaceViolationException)
                    {
                        files.Add(new JsonObject { ["path"] = "denied", ["status"] = "denied" });
                        data["complete"] = false;
                        continue;
                    }

                    if (mapped.TryGetValue(relative, out var existing))
                    {
                        // Avoid re-reading the same file; duplicate request is
                        // reported with a canonical reference, not repeated source.
                        files.Add(new JsonObject { ["path"] = relative, ["status"] = "duplicate",
                            ["sameAs"] = existing["path"]?.ToString() });
                        continue;
                    }

                    var fileEntry = new JsonObject { ["path"] = relative };
                    mapped[relative] = fileEntry;
                    files.Add(fileEntry);
                    try
                    {
                        if (!File.Exists(full))
                        {
                            fileEntry["status"] = "not_found";
                            data["complete"] = false;
                            continue;
                        }
                        var info = new FileInfo(full);
                        if (info.Length > maxFileBytes)
                        {
                            fileEntry["status"] = "too_large";
                            fileEntry["sizeBytes"] = info.Length;
                            data["complete"] = false;
                            continue;
                        }
                        // Hold one read handle and obtain the hash from exactly
                        // the same bytes that the decoder consumes.
                        byte[] bytes;
                        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.SequentialScan))
                        {
                            using var buffer = new MemoryStream((int)info.Length);
                            stream.CopyTo(buffer);
                            bytes = buffer.ToArray();
                        }
                        if (bytes.Length > maxFileBytes)
                            throw new IOException("File grew beyond the configured context-read cap.");
                        if (LooksBinary(bytes))
                        {
                            fileEntry["status"] = "binary";
                            data["complete"] = false;
                            continue;
                        }
                        string[] lines;
                        using (var sr = new StreamReader(new MemoryStream(bytes, writable: false),
                                   new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
                        {
                            var content = sr.ReadToEnd();
                            if (content.Length == 0) lines = [];
                            else
                            {
                                var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
                                var split = normalized.Split('\n');
                                lines = normalized.EndsWith('\n') ? split[..^1] : split;
                            }
                        }
                        if (includeVersion)
                            fileEntry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                        fileEntry["sizeBytes"] = bytes.Length;
                        if (includeTotal) fileEntry["totalLines"] = lines.Length;
                        fileEntry["status"] = "complete";
                        var ranges = new JsonArray();
                        fileEntry["ranges"] = ranges;
                        foreach (var range in entry.Ranges)
                        {
                            ct.ThrowIfCancellationRequested();
                            var startIndex = Math.Min(lines.Length, range.Start - 1);
                            var stopExclusive = Math.Min(lines.Length, range.End);
                            var block = stopExclusive > startIndex
                                ? string.Join("\n", lines[startIndex..stopExclusive])
                                : "";
                            ranges.Add(new JsonObject
                            {
                                ["startLine"] = range.Start,
                                ["endLine"] = stopExclusive,
                                ["content"] = block,
                                ["truncated"] = false,
                                ["pastEof"] = range.Start > lines.Length
                            });
                            totalRanges++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
                    {
                        fileEntry.Clear();
                        fileEntry["path"] = relative;
                        fileEntry["status"] = ex is DecoderFallbackException ? "binary" : "unavailable";
                        data["complete"] = false;
                    }
                }

                data["rangeCount"] = totalRanges;
                // Entire *data* is capped, including metadata and JSON escaping.
                // Trim large ranges in reverse, not only the first requested file.
                var partial = false;
                var iterations = 0;
                while (JsonSerializer.SerializeToUtf8Bytes(data, AppConfig.JsonOptions).Length > maxBytes)
                {
                    if (++iterations > 1024)
                        return Task.FromResult(ToolResult.Fail("budget_too_small",
                            "The context request metadata exceeds its maxBytes; request fewer files or a larger cap."));
                    JsonObject? selected = null;
                    var largest = 0;
                    foreach (var f in files.OfType<JsonObject>())
                        if (f["ranges"] is JsonArray arr)
                            foreach (var r in arr.OfType<JsonObject>())
                            {
                                var size = r["content"]?.GetValue<string>().Length ?? 0;
                                if (size <= largest) continue;
                                selected = r;
                                largest = size;
                            }
                    if (selected is null || largest == 0)
                        return Task.FromResult(ToolResult.Fail("budget_too_small",
                            "Even the minimal context metadata cannot fit maxBytes; reduce the number of files."));
                    var text = selected["content"]!.GetValue<string>();
                    var target = Math.Max(0, text.Length - Math.Max(128, text.Length / 3));
                    if (target > 0 && char.IsHighSurrogate(text[target - 1])) target--;
                    selected["content"] = text[..target];
                    selected["truncated"] = true;
                    partial = true;
                }
                if (partial) data["complete"] = false;
                return Task.FromResult(ToolResult.Ok(
                    $"Read context for {requested.Count} file request(s); {totalRanges} range(s)" +
                    (data["complete"]?.GetValue<bool>() == true ? "." : " (partial)."), data));
            });
    }

    private static List<RequestedFile> ParseItems(JsonObject args)
    {
        if (args["items"] is not JsonArray items || items.Count < 1 || items.Count > 32)
            throw new ArgumentException("items must contain 1..32 file request objects.");
        var files = new List<RequestedFile>(items.Count);
        var totalRanges = 0;
        foreach (var item in items)
        {
            if (item is not JsonObject obj)
                throw new ArgumentException("Each context item must be an object with path and optional ranges.");
            var path = obj.RequireString("path");
            if (path.Length > 512) throw new ArgumentException("Context path length cannot exceed 512 characters.");
            var ranges = new List<RequestedRange>();
            if (obj["ranges"] is JsonArray given)
            {
                if (given.Count == 0) throw new ArgumentException("ranges cannot be an empty array.");
                foreach (var value in given)
                {
                    if (value is not JsonArray pair || pair.Count != 2 ||
                        pair[0]?.GetValueKind() != JsonValueKind.Number ||
                        pair[1]?.GetValueKind() != JsonValueKind.Number)
                        throw new ArgumentException("Every range must be [startLine,endLine] with integer values.");
                    var start = pair[0]!.GetValue<int>();
                    var end = pair[1]!.GetValue<int>();
                    if (start < 1 || end < start || end - start > 2000)
                        throw new ArgumentException("Each context range must be positive and no longer than 2001 lines.");
                    ranges.Add(new RequestedRange(start, end));
                }
            }
            else if (obj.ContainsKey("ranges") && obj["ranges"] is not null)
                throw new ArgumentException("ranges must be an array.");
            else
                ranges.Add(new RequestedRange(1, 2000));
            totalRanges += ranges.Count;
            if (totalRanges > 64) throw new ArgumentException("At most 64 ranges are allowed per context request.");
            files.Add(new RequestedFile(path, ranges));
        }
        return files;
    }

    private static bool LooksBinary(byte[] bytes)
    {
        // Preserve BOM-marked UTF-16/UTF-32 text. Without a BOM, NUL bytes
        // indicate likely binary content and should not be sent as code.
        var hasBom = bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF));
        if (hasBom) return false;
        return bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).Contains((byte)0);
    }
}
