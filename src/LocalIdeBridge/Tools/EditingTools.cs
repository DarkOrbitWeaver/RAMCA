using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Precise, atomic file-editing operations with SHA-256 optimistic concurrency checks,
/// line-ending and encoding preservation, unified diff parsing/preview, and safe verified revert.
/// </summary>
public static partial class EditingTools
{
    private const long MaxEditableFileBytes = 8 * 1024 * 1024; // 8 MB safety ceiling for text editing
    private const long MaxReversibleDeleteBytes = 256L * 1024 * 1024; // bounded portable undo storage for deleted files

    [GeneratedRegex(@"^@@\s+-(?<oldStart>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s+@@", RegexOptions.Compiled)]
    private static partial Regex HunkHeaderRegex();

    public sealed record LoadedTextFile(
        string Content,
        string[] Lines,
        Encoding Encoding,
        string EncodingName,
        string NewLine,
        bool EndsWithNewLine,
        string Sha256,
        long SizeBytes);

    private sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);
    private sealed record FilePatch(string? OldPath, string? NewPath, List<DiffHunk> Hunks);

    public static IEnumerable<ToolDefinition> Create(
        PortablePaths paths,
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        Action<string>? onFileMutated = null)
    {
        yield return new ToolDefinition(
            Name: "read_file",
            Category: "editing",
            Description: "Reads a text file inside the workspace, returning content, line count, encoding, line ending style, and SHA-256 version token for safe edits.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("path", "string", "Relative file path inside the workspace.", Required: true),
                new("maxChars", "integer", "Maximum characters to return before truncating (use read_range for huge files).", Default: 32000),
                new("includeLineNumbers", "boolean", "Prefix each returned line with its 1-based line number.", Default: false)
            ],
            Handler: (args, _, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var full = guard.ResolvePath(args.RequireString("path"));
                if (!File.Exists(full))
                    throw new FileNotFoundException($"File not found: {args.RequireString("path")}");

                var loaded = LoadTextFile(full);
                var maxChars = Math.Clamp(args.OptionalInt("maxChars", 32000), 256, 128000);
                var numbered = args.OptionalBool("includeLineNumbers", false);

                string text;
                bool truncated = false;
                if (numbered)
                {
                    var sb = new StringBuilder(Math.Min(loaded.Content.Length + 256, maxChars));
                    for (var i = 0; i < loaded.Lines.Length; i++)
                    {
                        var line = $"{i + 1,5}: {loaded.Lines[i]}";
                        var remaining = maxChars - sb.Length - (sb.Length > 0 ? 1 : 0);
                        if (remaining <= 0) { truncated = true; break; }
                        if (sb.Length > 0) sb.Append('\n');
                        if (line.Length > remaining)
                        {
                            // The first numbered line must obey the same budget
                            // as all subsequent lines (including minified files).
                            if (remaining > 0 && remaining < line.Length && char.IsHighSurrogate(line[remaining - 1]))
                                remaining--;
                            sb.Append(line.AsSpan(0, Math.Max(0, remaining)));
                            truncated = true;
                            break;
                        }
                        sb.Append(line);
                    }
                    text = sb.ToString();
                }
                else if (loaded.Content.Length > maxChars)
                {
                    text = loaded.Content[..maxChars];
                    truncated = true;
                }
                else
                {
                    text = loaded.Content;
                }

                var rel = guard.GetRelativePathUnix(ws, full);
                return Task.FromResult(ToolResult.Ok(
                    $"Read '{rel}' ({loaded.Lines.Length} lines, {loaded.SizeBytes} bytes{(truncated ? ", truncated" : "")}).",
                    new
                    {
                        path = rel,
                        sha256 = loaded.Sha256,
                        encoding = loaded.EncodingName,
                        lineEnding = loaded.NewLine == "\r\n" ? "CRLF" : "LF",
                        totalLines = loaded.Lines.Length,
                        sizeBytes = loaded.SizeBytes,
                        truncated,
                        content = text
                    }));
            });

        yield return new ToolDefinition(
            Name: "read_range",
            Category: "editing",
            Description: "Streams a bounded 1-based inclusive line range [startLine, endLine] from a workspace file without loading huge files entirely into memory.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("path", "string", "Relative file path inside the workspace.", Required: true),
                new("startLine", "integer", "1-based starting line number.", Required: true),
                new("endLine", "integer", "1-based inclusive ending line number.", Required: true),
                new("format", "string", "formatted (compact), lines (structured), or both (legacy default).", Default: "both", EnumValues: ["formatted", "lines", "both"])
            ],
            Handler: (args, _, ct) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var full = guard.ResolvePath(args.RequireString("path"));
                if (!File.Exists(full))
                    throw new FileNotFoundException($"File not found: {args.RequireString("path")}");

                var start = Math.Max(1, args.OptionalInt("startLine", 1));
                var end = Math.Max(start, args.OptionalInt("endLine", start + 99));
                if (end - start > 1000) end = start + 1000;

                var format = args.OptionalString("format", "both");
                if (format is not ("formatted" or "lines" or "both"))
                    throw new ArgumentException("format must be formatted, lines, or both.");
                var sha256 = ComputeFileSha256(full);
                var lines = new List<object>();
                var sb = new StringBuilder();
                var totalLines = 0;

                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192))
                using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    string? line;
                    while ((line = sr.ReadLine()) is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        totalLines++;
                        if (totalLines >= start && totalLines <= end)
                        {
                            if (format != "formatted") lines.Add(new { line = totalLines, text = line });
                            if (format != "lines")
                            {
                                if (sb.Length > 0) sb.Append('\n');
                                sb.Append($"{totalLines,5}: {line}");
                            }
                        }
                    }
                }

                var rel = guard.GetRelativePathUnix(ws, full);
                return Task.FromResult(ToolResult.Ok(
                    $"Read lines {start}..{Math.Min(end, totalLines)} of '{rel}' (total {totalLines} lines).",
                    new
                    {
                        path = rel,
                        sha256,
                        startLine = start,
                        endLine = Math.Min(end, totalLines),
                        totalLines,
                        formatted = format == "lines" ? null : sb.ToString(),
                        lines = format == "formatted" ? null : lines
                    }));
            });

        yield return new ToolDefinition(
            Name: "create_file",
            Category: "editing",
            Description: "Creates a new file (or atomically overwrites an existing file when overwrite=true) inside the trusted workspace and records a reversible edit entry.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Writes a file inside the trusted workspace and stores a journal entry in data/sessions.",
            Parameters:
            [
                new("path", "string", "Relative file path inside the workspace.", Required: true),
                new("content", "string", "Full text content to write.", Required: true),
                new("overwrite", "boolean", "Allow replacing an existing file.", Default: false),
                new("lineEnding", "string", "preserve | CRLF | LF (defaults to preserve existing or platform default).", EnumValues: ["preserve", "CRLF", "LF"], Default: "preserve"),
                new("expectedSha256", "string", "Optional SHA-256 of existing file when overwriting to detect concurrent edits.")
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var full = guard.ResolvePath(args.RequireString("path"), forWrite: true);
                var content = args.RequireRawString("content");
                var overwrite = args.OptionalBool("overwrite", false);
                var leMode = args.OptionalString("lineEnding", "preserve")!;
                var expectedSha = args.OptionalString("expectedSha256");

                var existed = File.Exists(full);
                if (existed && !overwrite)
                    return Task.FromResult(ToolResult.Fail("already_exists", $"File '{args.RequireString("path")}' already exists. Pass overwrite=true or use replace_range/apply_patch."));

                Encoding encoding = new UTF8Encoding(false);
                var targetNewLine = OperatingSystem.IsWindows() ? "\r\n" : "\n";
                var beforeSha = "none";
                var oldLineCount = 0;
                string? backupPath = null;
                var editId = NewEditId();

                if (existed)
                {
                    var current = LoadTextFile(full);
                    if (!string.IsNullOrWhiteSpace(expectedSha) &&
                        !string.Equals(current.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(ToolResult.Fail("stale_file",
                            $"Stale file detected for '{args.RequireString("path")}': expected sha256={expectedSha}, actual={current.Sha256}. Re-read the file before modifying."));
                    }
                    encoding = current.Encoding;
                    targetNewLine = current.NewLine;
                    beforeSha = current.Sha256;
                    oldLineCount = current.Lines.Length;
                    backupPath = SaveBackupBytes(paths, ctx.SessionId, editId, full);
                }

                if (leMode.Equals("CRLF", StringComparison.OrdinalIgnoreCase)) targetNewLine = "\r\n";
                else if (leMode.Equals("LF", StringComparison.OrdinalIgnoreCase)) targetNewLine = "\n";

                var normalized = NormalizeLineEndings(content, targetNewLine);
                var afterSha = Convert.ToHexStringLower(SHA256.HashData(EncodeText(normalized, encoding)));
                var newLineCount = CountLines(normalized);
                var rel = guard.GetRelativePathUnix(ws, full);
                var transactionResult = CommitSingleFileEdit(paths, guard, db, ctx.SessionId, ws, rel, full,
                    editId, existed ? "overwrite_file" : "create_file", beforeSha, afterSha,
                    backupPath, existed, newLineCount, oldLineCount,
                    () => PortablePaths.AtomicWriteAllText(full, normalized, encoding));
                if (transactionResult is not null) return Task.FromResult(transactionResult);

                onFileMutated?.Invoke(full);
                return Task.FromResult(ToolResult.Ok(
                    $"{(existed ? "Overwrote" : "Created")} '{rel}' (+{newLineCount}/-{oldLineCount} lines).",
                    new
                    {
                        editId,
                        path = rel,
                        operation = existed ? "overwrite_file" : "create_file",
                        beforeSha256 = beforeSha,
                        afterSha256 = afterSha,
                        linesAdded = newLineCount,
                        linesRemoved = oldLineCount,
                        changedFiles = new[] { rel }
                    }));
            });

        yield return new ToolDefinition(
            Name: "replace_range",
            Category: "editing",
            Description: "Atomically replaces 1-based inclusive lines [startLine, endLine] in a file while preserving encoding and line endings, with optional SHA-256 and old-text verification.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Atomically replaces target file inside workspace and records a reversible edit.",
            Parameters:
            [
                new("path", "string", "Relative file path inside the workspace.", Required: true),
                new("startLine", "integer", "1-based inclusive start line to replace.", Required: true),
                new("endLine", "integer", "1-based inclusive end line to replace.", Required: true),
                new("newText", "string", "Replacement text for the specified line range.", Required: true),
                new("expectedSha256", "string", "Optional SHA-256 returned by read_file/read_range to prevent overwriting concurrent changes."),
                new("expectedOldText", "string", "Optional exact text expected in the target range as an additional precondition check.")
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var relInput = args.RequireString("path");
                var full = guard.ResolvePath(relInput, forWrite: true);
                if (!File.Exists(full))
                    throw new FileNotFoundException($"File not found: {relInput}");

                var current = LoadTextFile(full);
                var expectedSha = args.OptionalString("expectedSha256");
                if (!string.IsNullOrWhiteSpace(expectedSha) &&
                    !string.Equals(current.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ToolResult.Fail("stale_file",
                        $"File '{relInput}' changed since read (expected {expectedSha}, actual {current.Sha256})."));
                }

                var start = args.OptionalInt("startLine", 0);
                var end = args.OptionalInt("endLine", 0);
                if (start < 1 || end < start || end > current.Lines.Length)
                {
                    return Task.FromResult(ToolResult.Fail("range_out_of_bounds",
                        $"Invalid range [{start}, {end}] for file '{relInput}' containing {current.Lines.Length} lines."));
                }

                var expectedOld = args.OptionalString("expectedOldText");
                if (expectedOld is not null)
                {
                    var actualSlice = string.Join("\n", current.Lines[(start - 1)..end]);
                    var expectedNorm = NormalizeLineEndings(expectedOld, "\n").TrimEnd('\n');
                    if (!string.Equals(actualSlice, expectedNorm, StringComparison.Ordinal))
                    {
                        return Task.FromResult(ToolResult.Fail("precondition_failed",
                            $"Lines {start}..{end} in '{relInput}' did not match expectedOldText."));
                    }
                }

                var newText = args.RequireRawString("newText");
                var replacementLines = SplitLinesNoEnding(newText);
                var updated = new List<string>(current.Lines.Length - (end - start + 1) + replacementLines.Length);
                updated.AddRange(current.Lines[..(start - 1)]);
                updated.AddRange(replacementLines);
                updated.AddRange(current.Lines[end..]);

                var joined = JoinLinesPreservingStyle(updated, current.NewLine, current.EndsWithNewLine);
                var editId = NewEditId();
                var backupPath = SaveBackupBytes(paths, ctx.SessionId, editId, full);
                var afterSha = Convert.ToHexStringLower(SHA256.HashData(EncodeText(joined, current.Encoding)));
                var rel = guard.GetRelativePathUnix(ws, full);
                var removed = end - start + 1;
                var added = replacementLines.Length;
                var transactionResult = CommitSingleFileEdit(paths, guard, db, ctx.SessionId, ws, rel, full,
                    editId, "replace_range", current.Sha256, afterSha, backupPath, true, added, removed,
                    () => PortablePaths.AtomicWriteAllText(full, joined, current.Encoding));
                if (transactionResult is not null) return Task.FromResult(transactionResult);

                onFileMutated?.Invoke(full);
                return Task.FromResult(ToolResult.Ok(
                    $"Updated '{rel}' lines {start}..{end} (+{added}/-{removed}).",
                    new
                    {
                        editId,
                        path = rel,
                        beforeSha256 = current.Sha256,
                        afterSha256 = afterSha,
                        linesAdded = added,
                        linesRemoved = removed,
                        newTotalLines = updated.Count,
                        diff = BuildCompactUnifiedDiff(rel, current.Lines, updated),
                        changedFiles = new[] { rel }
                    }));
            });

        yield return new ToolDefinition(
            Name: "apply_patch",
            Category: "editing",
            Description: "Applies a standard unified diff (single-file or multi-file) atomically after verifying all hunk context preconditions. Preserves encoding and line endings.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Atomically modifies/creates/deletes patched files in workspace and records reversible edit entries.",
            Parameters:
            [
                new("patch", "string", "Unified diff text (with --- / +++ file headers and @@ hunk headers).", Required: true),
                new("expectedSha256", "string", "Optional SHA-256 precondition (for single-file patches)."),
                new("expectedSha256Map", "object", "Optional file-relative-path to SHA-256 map for multi-file optimistic concurrency.")
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var patchText = args.RequireRawString("patch");
                if (patchText.Length > 1_000_000)
                    return Task.FromResult(ToolResult.Fail("patch_too_large",
                        "Patch exceeds the 1,000,000-character limit; split it into smaller reviewed transactions."));
                var expectedSha = args.OptionalString("expectedSha256");

                var filePatches = ParseUnifiedDiff(patchText);
                if (filePatches.Count > 32)
                    return Task.FromResult(ToolResult.Fail("too_many_patch_files",
                        "A single patch transaction may change at most 32 files."));
                if (filePatches.Count == 0)
                    return Task.FromResult(ToolResult.Fail("invalid_patch", "No valid unified diff file hunks found. Include --- a/file, +++ b/file, and @@ -l,c +l,c @@ headers."));

                // A patch is a transaction, not a series of unrelated writes. Reject ambiguous
                // file headers / repeated paths, compute all outputs, and preserve every source.
                var staged = new List<(string RelPath, string FullPath, string Op, LoadedTextFile? Before, string? NewContent, Encoding Enc, int Added, int Removed)>();
                long stagedBytes = 0;
                var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                if (args["expectedSha256Map"] is not null && args["expectedSha256Map"] is not JsonObject)
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "expectedSha256Map must be a JSON object."));
                var shaMap = args["expectedSha256Map"] as JsonObject;
                if (expectedSha is not null && (filePatches.Count != 1 || shaMap is not null))
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "A scalar expectedSha256 is permitted only for a single-file patch without expectedSha256Map."));
                static bool ValidSha(string? value) => value is { Length: 64 } &&
                    value.All(char.IsAsciiHexDigit);
                if (expectedSha is not null && !ValidSha(expectedSha))
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "expectedSha256 must contain exactly 64 hex digits."));
                var normalizedMap = new Dictionary<string, string>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                if (shaMap is not null)
                {
                    foreach (var (key, value) in shaMap)
                    {
                        if (value?.GetValueKind() != JsonValueKind.String ||
                            !ValidSha(value.GetValue<string>()))
                            return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Invalid expected SHA-256 for '{key}'."));
                        var resolved = guard.ResolvePath(key);
                        var cleanKey = guard.GetRelativePathUnix(ws, resolved);
                        if (!normalizedMap.TryAdd(cleanKey, value.GetValue<string>()))
                            return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Duplicate normalized SHA-256 key '{cleanKey}'."));
                    }
                }

                foreach (var fp in filePatches)
                {
                    var isCreate = fp.OldPath is null or "/dev/null";
                    var isDelete = fp.NewPath is null or "/dev/null";
                    if (!isCreate && !isDelete && !string.Equals(fp.OldPath, fp.NewPath, StringComparison.Ordinal))
                        return Task.FromResult(ToolResult.Fail("unsupported_patch", "Rename patches require the guarded rename_file tool, not apply_patch."));
                    var targetRel = isDelete ? fp.OldPath! : fp.NewPath!;
                    var full = guard.ResolvePath(targetRel, forWrite: !isDelete, forDelete: isDelete);
                    // A path that exists in both `a/path` and `path` is ambiguous in
                    // Git-format patches. Never silently target the stripped root path.
                    if (File.Exists(Path.Combine(ws, "a", targetRel)) ||
                        File.Exists(Path.Combine(ws, "b", targetRel)))
                        return Task.FromResult(ToolResult.Fail("ambiguous_patch_path", $"Patch path '{targetRel}' is ambiguous with an a/ or b/ directory; use an unprefixed root-relative header."));
                    var cleanRel = guard.GetRelativePathUnix(ws, full);
                    if (!seen.Add(full))
                        return Task.FromResult(ToolResult.Fail("invalid_patch", $"Duplicate patch target '{cleanRel}'; combine hunks into one file section."));
                    if (Directory.Exists(full))
                        return Task.FromResult(ToolResult.Fail("invalid_patch", $"Patch target '{cleanRel}' is a directory."));

                    if (isCreate)
                    {
                        if (File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("precondition_failed", $"File '{cleanRel}' already exists."));
                        if (normalizedMap.ContainsKey(cleanRel))
                            return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Cannot supply a prior hash for new file '{cleanRel}'."));
                        var applied = ApplyHunksToLines([], fp.Hunks, cleanRel, out var added, out var removed, out var err);
                        if (err is not null) return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                        var nl = OperatingSystem.IsWindows() ? "\r\n" : "\n";
                        staged.Add((cleanRel, full, "patch_create", null, JoinLinesPreservingStyle(applied!, nl, true), new UTF8Encoding(false), added, removed));
                    }
                    else
                    {
                        if (!File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("not_found", $"File '{cleanRel}' does not exist."));
                        var before = LoadTextFile(full);
                        stagedBytes += before.SizeBytes;
                        if (stagedBytes > 64L * 1024 * 1024)
                            return Task.FromResult(ToolResult.Fail("patch_source_budget",
                                "Patch source files exceed the 64 MiB staging limit; split the operation."));
                        var requiredSha = filePatches.Count == 1 ? expectedSha : null;
                        if (normalizedMap.TryGetValue(cleanRel, out var mapHash))
                            requiredSha = mapHash;
                        if (!string.IsNullOrWhiteSpace(requiredSha) &&
                            !string.Equals(before.Sha256, requiredSha, StringComparison.OrdinalIgnoreCase))
                            return Task.FromResult(ToolResult.Fail("stale_file", $"File '{cleanRel}' no longer matches SHA-256 precondition."));
                        var applied = ApplyHunksToLines(before.Lines, fp.Hunks, cleanRel, out var added, out var removed, out var err);
                        if (err is not null) return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                        if (isDelete && applied!.Count != 0)
                            return Task.FromResult(ToolResult.Fail("invalid_patch", $"Deletion of '{cleanRel}' did not remove every source line."));
                        var newContent = isDelete ? null : JoinLinesPreservingStyle(applied!, before.NewLine, before.EndsWithNewLine);
                        staged.Add((cleanRel, full, isDelete ? "patch_delete" : "apply_patch", before, newContent, before.Encoding, added, removed));
                    }
                }

                // Reject unused map keys before any backup or filesystem write.
                var patchTargets = new HashSet<string>(staged.Select(s => s.RelPath),
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var key in normalizedMap.Keys)
                    if (!patchTargets.Contains(key))
                        return Task.FromResult(ToolResult.Fail("invalid_arguments",
                            $"expectedSha256Map contains an unused path '{key}'."));

                // Persist a recovery manifest and *all* durable backups BEFORE the first write.
                // On crash the next startup rolls back only matching pre/post-image hashes; it
                // will fail closed on an external edit rather than overwrite unexpected bytes.
                var tx = new PatchTransaction { Id = $"patch_{Guid.NewGuid():N}", Workspace = ws, SessionId = ctx.SessionId };
                var fileResults = new List<object>();
                var journalEntries = new List<EditJournalRecord>();
                var totalAdded = 0;
                var totalRemoved = 0;
                try
                {
                    foreach (var item in staged)
                    {
                        var editId = NewEditId();
                        var backup = item.Before is null ? null : SaveBackupBytes(paths, ctx.SessionId, editId, item.FullPath);
                        var proposedHash = item.Op == "patch_delete" ? "deleted" :
                            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(EncodeText(item.NewContent!, item.Enc)));
                        tx.Items.Add(new PatchTransactionItem
                        {
                            RelativePath = item.RelPath, EditId = editId, BackupPath = backup,
                            BeforeHash = item.Before?.Sha256 ?? "none", AfterHash = proposedHash
                        });
                    }
                }
                catch
                {
                    // No workspace mutation has begun. Do not leave orphaned snapshots
                    // when a later file cannot be backed up.
                    foreach (var item in tx.Items)
                        if (item.BackupPath is { } backup)
                            try { File.Delete(backup); } catch (IOException) { }
                    throw;
                }
                var txPath = GetTransactionPath(paths, tx.Id);
                PortablePaths.AtomicWriteAllText(txPath, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                try
                {
                    for (var i = 0; i < staged.Count; i++)
                    {
                        var item = staged[i];
                        var t = tx.Items[i];
                        // Recheck immediately before mutation, not only during patch parsing.
                        var actual = File.Exists(item.FullPath) ? ComputeFileSha256(item.FullPath) : "none";
                        if (!string.Equals(actual, t.BeforeHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"Concurrent edit detected for '{item.RelPath}'; transaction must roll back.");
                        if (item.Op == "patch_delete")
                            File.Delete(item.FullPath);
                        else
                            PortablePaths.AtomicWriteAllText(item.FullPath, item.NewContent!, item.Enc);
                        var afterSha = File.Exists(item.FullPath) ? ComputeFileSha256(item.FullPath) : "deleted";
                        if (!string.Equals(afterSha, t.AfterHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"Post-write checksum mismatch for '{item.RelPath}'.");
                        totalAdded += item.Added;
                        totalRemoved += item.Removed;
                    }
                    for (var i = 0; i < staged.Count; i++)
                    {
                        var item = staged[i];
                        var t = tx.Items[i];
                        journalEntries.Add(new EditJournalRecord(
                            t.EditId, ctx.SessionId, ws, item.RelPath, item.Op,
                            t.BeforeHash, t.AfterHash, item.Before is not null, t.BackupPath,
                            item.Added, item.Removed, false, DateTimeOffset.UtcNow));
                        fileResults.Add(new
                        {
                            editId = t.EditId, path = item.RelPath, operation = item.Op,
                            beforeSha256 = t.BeforeHash, afterSha256 = t.AfterHash,
                            linesAdded = item.Added, linesRemoved = item.Removed
                        });
                    }
                    db.RecordEditBatch(journalEntries);
                    tx.Committed = true;
                    PortablePaths.AtomicWriteAllText(txPath, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                    // Garbage collection is best-effort AFTER durable commit. Failure to
                    // remove the manifest must not trigger a destructive rollback.
                    try { File.Delete(txPath); } catch (IOException) { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        RecoverTransaction(paths, guard, tx, db);
                        File.Delete(txPath);
                        return Task.FromResult(ToolResult.Fail("patch_rolled_back", $"Patch aborted and pre-image checksums restored: {ex.Message}"));
                    }
                    catch (Exception recovery)
                    {
                        // Never erase this manifest. Explicit recovery requires inspecting
                        // the conflicting file; startup refuses to work on ambiguous state.
                        return Task.FromResult(ToolResult.Fail("patch_recovery_required",
                            $"Patch failed ({ex.Message}); automatic recovery blocked ({recovery.Message}). Do NOT retry blindly. Manifest: {txPath}"));
                    }
                }
                foreach (var item in staged)
                    try { onFileMutated?.Invoke(item.FullPath); } catch { /* cache invalidation must not fail a committed transaction */ }

                return Task.FromResult(ToolResult.Ok(
                    $"Applied patch across {fileResults.Count} file(s) (+{totalAdded}/-{totalRemoved} lines).",
                    new { transactionId = tx.Id, totalLinesAdded = totalAdded,
                          totalLinesRemoved = totalRemoved, changedFiles = staged.Select(s => s.RelPath).ToArray(), edits = fileResults }));
            });

        yield return new ToolDefinition(
            Name: "preview_edit",
            Category: "editing",
            Description: "Validates and previews a unified diff or bounded line replacement without modifying any file on disk.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None (dry-run only).",
            Parameters:
            [
                new("patch", "string", "Optional unified diff to validate and preview."),
                new("path", "string", "File path (when previewing a range replacement instead of a patch)."),
                new("startLine", "integer", "1-based start line (for range preview)."),
                new("endLine", "integer", "1-based end line (for range preview)."),
                new("newText", "string", "Proposed replacement text (for range preview)."),
                new("expectedSha256", "string", "Optional expected file SHA-256 (single file patch or range)."),
                new("expectedSha256Map", "object", "Optional file-to-SHA map (multi-file patch preview).")
            ],
            Handler: (args, _, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var patch = args.OptionalString("patch");
                if (!string.IsNullOrWhiteSpace(patch))
                {
                    var fps = ParseUnifiedDiff(patch);
                    if (fps.Count == 0)
                        return Task.FromResult(ToolResult.Fail("invalid_patch", "No valid unified diff hunks found."));

                    var expectedSha = args.OptionalString("expectedSha256");
                    var mapNode = args["expectedSha256Map"];
                    if (mapNode is not null && mapNode is not JsonObject)
                        return Task.FromResult(ToolResult.Fail("invalid_arguments", "expectedSha256Map must be an object."));
                    if (expectedSha is not null && (fps.Count != 1 || mapNode is not null))
                        return Task.FromResult(ToolResult.Fail("invalid_arguments", "Scalar expectedSha256 is only valid for a single-file patch."));
                    static bool ValidSha(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
                    if (expectedSha is not null && !ValidSha(expectedSha))
                        return Task.FromResult(ToolResult.Fail("invalid_arguments", "Expected SHA-256 must be 64 hex digits."));
                    var map = new Dictionary<string, string>(
                        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    if (mapNode is JsonObject mapObject)
                        foreach (var (key, value) in mapObject)
                        {
                            if (value?.GetValueKind() != JsonValueKind.String || !ValidSha(value.GetValue<string>()))
                                return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Invalid hash for '{key}'."));
                            var keyRel = guard.GetRelativePathUnix(ws, guard.ResolvePath(key));
                            if (!map.TryAdd(keyRel, value.GetValue<string>()))
                                return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Duplicate hash key '{keyRel}'."));
                        }

                    var previews = new List<object>();
                    var usedPaths = new HashSet<string>(
                        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    foreach (var fp in fps)
                    {
                        var isCreate = fp.OldPath is null or "/dev/null";
                        var isDelete = fp.NewPath is null or "/dev/null";
                        if (!isCreate && !isDelete && !string.Equals(fp.OldPath, fp.NewPath, StringComparison.Ordinal))
                            return Task.FromResult(ToolResult.Fail("unsupported_patch", "Rename patches require rename_file."));
                        var target = isDelete ? fp.OldPath! : fp.NewPath!;
                        var full = guard.ResolvePath(target, forWrite: !isDelete, forDelete: isDelete);
                        var rel = guard.GetRelativePathUnix(ws, full);
                        if (!usedPaths.Add(rel))
                            return Task.FromResult(ToolResult.Fail("invalid_patch", $"Duplicate patch target '{rel}'."));
                        if (File.Exists(Path.Combine(ws, "a", target)) || File.Exists(Path.Combine(ws, "b", target)))
                            return Task.FromResult(ToolResult.Fail("ambiguous_patch_path", $"Patch path '{rel}' is ambiguous with a/ or b/ directories."));
                        if (Directory.Exists(full))
                            return Task.FromResult(ToolResult.Fail("invalid_patch", $"Patch target '{rel}' is a directory."));
                        if (isCreate && File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("precondition_failed", $"File '{rel}' already exists."));
                        if (!isCreate && !File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("not_found", $"File '{rel}' does not exist."));
                        if (isCreate && map.ContainsKey(rel))
                            return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Cannot require prior SHA for newly created '{rel}'."));
                        var before = isCreate ? null : LoadTextFile(full);
                        var requiredSha = map.TryGetValue(rel, out var mapped) ? mapped : fps.Count == 1 ? expectedSha : null;
                        if (requiredSha is not null &&
                            !string.Equals(before?.Sha256, requiredSha, StringComparison.OrdinalIgnoreCase))
                            return Task.FromResult(ToolResult.Fail("stale_file", $"File '{rel}' differs from expected SHA-256."));
                        var oldLines = before?.Lines ?? [];
                        var applied = ApplyHunksToLines(oldLines, fp.Hunks, rel, out var add, out var rem, out var err);
                        if (err is not null)
                            return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                        if (isDelete && applied!.Count != 0)
                            return Task.FromResult(ToolResult.Fail("invalid_patch", $"Deletion patch for '{rel}' did not remove all lines."));
                        previews.Add(new
                        {
                            path = rel,
                            wouldSucceed = true,
                            linesAdded = add,
                            linesRemoved = rem,
                            diff = BuildCompactUnifiedDiff(rel, oldLines, applied!)
                        });
                    }
                    foreach (var key in map.Keys)
                        if (!usedPaths.Contains(key))
                            return Task.FromResult(ToolResult.Fail("invalid_arguments", $"Unconsumed expectedSha256Map key '{key}'."));
                    return Task.FromResult(ToolResult.Ok($"Patch preview succeeded for {previews.Count} file(s).", new { previews }));
                }

                var path = args.RequireString("path");
                var fullFile = guard.ResolvePath(path);
                if (!File.Exists(fullFile))
                    throw new FileNotFoundException($"File not found: {path}");
                var loaded = LoadTextFile(fullFile);
                var expectedRangeSha = args.OptionalString("expectedSha256");
                if (expectedRangeSha is not null &&
                    !string.Equals(loaded.Sha256, expectedRangeSha, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail("stale_file", "Range preview does not match expected SHA-256."));
                if (args["expectedSha256Map"] is not null)
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "expectedSha256Map is only valid for patch preview."));
                var start = args.OptionalInt("startLine", 0);
                var end = args.OptionalInt("endLine", 0);
                if (start < 1 || end < start || end > loaded.Lines.Length)
                    return Task.FromResult(ToolResult.Fail("range_out_of_bounds", $"Range [{start}, {end}] outside 1..{loaded.Lines.Length}."));

                var rep = SplitLinesNoEnding(args.RequireRawString("newText"));
                var next = new List<string>();
                next.AddRange(loaded.Lines[..(start - 1)]);
                next.AddRange(rep);
                next.AddRange(loaded.Lines[end..]);
                var relPath = guard.GetRelativePathUnix(ws, fullFile);

                return Task.FromResult(ToolResult.Ok(
                    $"Preview ready for '{relPath}' (+{rep.Length}/-{end - start + 1}).",
                    new
                    {
                        path = relPath,
                        currentSha256 = loaded.Sha256,
                        linesAdded = rep.Length,
                        linesRemoved = end - start + 1,
                        diff = BuildCompactUnifiedDiff(relPath, loaded.Lines, next)
                    }));
            });

        yield return new ToolDefinition(
            Name: "rename_file",
            Category: "editing",
            Description: "Moves a file without overwriting an existing target. Keeps an undo backup and a crash-recovery manifest for both paths.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Atomically moves a file and journals reversible source and destination changes.",
            Parameters:
            [
                new("sourcePath", "string", "Existing relative file path.", Required: true),
                new("destinationPath", "string", "New relative file path inside workspace.", Required: true),
                new("overwrite", "boolean", "Overwriting an existing destination is not supported.", Default: false),
                new("expectedSha256", "string", "Optional SHA-256 of source to reject stale moves.")
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var src = guard.ResolvePath(args.RequireString("sourcePath"), forDelete: true);
                var dst = guard.ResolvePath(args.RequireString("destinationPath"), forWrite: true);
                var srcRel = guard.GetRelativePathUnix(ws, src);
                var dstRel = guard.GetRelativePathUnix(ws, dst);
                var overwrite = args.OptionalBool("overwrite", false);
                if (overwrite)
                    return Task.FromResult(ToolResult.Fail("unsafe_overwrite_blocked",
                        "RAMCA does not overwrite a file during rename. Choose a new destination."));
                if (string.Equals(srcRel, dstRel,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "Source and destination are the same."));
                if (!File.Exists(src))
                    throw new FileNotFoundException($"Source file not found: {srcRel}");
                if (File.Exists(dst) || Directory.Exists(dst))
                    return Task.FromResult(ToolResult.Fail("already_exists",
                        $"Destination '{dstRel}' exists. Choose an unused path."));

                var info = new FileInfo(src);
                if (info.Length > MaxReversibleDeleteBytes)
                    return Task.FromResult(ToolResult.Fail("backup_limit_exceeded",
                        $"Refusing to move {info.Length} bytes without a reversible snapshot. Limit: {MaxReversibleDeleteBytes}."));

                var beforeSha = ComputeFileSha256(src);
                var expected = args.OptionalString("expectedSha256");
                if (!string.IsNullOrWhiteSpace(expected) &&
                    !string.Equals(beforeSha, expected, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail("stale_file", "Source changed after the caller inspected it."));

                var sourceEditId = NewEditId();
                var destinationEditId = NewEditId();
                var backup = SaveBackupBytes(paths, ctx.SessionId, sourceEditId, src);
                var tx = new PatchTransaction
                {
                    Id = $"patch_{Guid.NewGuid():N}", Workspace = ws, SessionId = ctx.SessionId,
                    Items =
                    [
                        new PatchTransactionItem
                        {
                            RelativePath = srcRel, EditId = sourceEditId,
                            BackupPath = backup, BeforeHash = beforeSha, AfterHash = "deleted"
                        },
                        new PatchTransactionItem
                        {
                            RelativePath = dstRel, EditId = destinationEditId,
                            BeforeHash = "none", AfterHash = beforeSha
                        }
                    ]
                };
                var manifest = GetTransactionPath(paths, tx.Id);
                PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                try
                {
                    if (!string.Equals(ComputeFileSha256(src), beforeSha, StringComparison.OrdinalIgnoreCase) ||
                        File.Exists(dst) || Directory.Exists(dst))
                        throw new IOException("A file changed before rename; original must be preserved.");

                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Move(src, dst); // Both paths are inside one trusted workspace.
                    if (File.Exists(src) ||
                        !string.Equals(ComputeFileSha256(dst), beforeSha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Rename verification failed.");

                    var now = DateTimeOffset.UtcNow;
                    db.RecordEditBatch(
                    [
                        new EditJournalRecord(sourceEditId, ctx.SessionId, ws, srcRel,
                            "rename_source", beforeSha, "deleted", true, backup, 0, 0, false, now),
                        new EditJournalRecord(destinationEditId, ctx.SessionId, ws, dstRel,
                            "rename_destination", "none", beforeSha, false, null, 0, 0, false, now)
                    ]);

                    tx.Committed = true;
                    PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                    try { File.Delete(manifest); } catch (IOException) { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        RecoverTransaction(paths, guard, tx, db);
                        File.Delete(manifest);
                        return Task.FromResult(ToolResult.Fail("rename_rolled_back",
                            $"Move aborted; original bytes recovered: {ex.Message}"));
                    }
                    catch (Exception recovery)
                    {
                        return Task.FromResult(ToolResult.Fail("rename_recovery_required",
                            $"Move failed ({ex.Message}); recovery needs inspection ({recovery.Message}). Manifest: {manifest}"));
                    }
                }

                onFileMutated?.Invoke(src);
                onFileMutated?.Invoke(dst);
                return Task.FromResult(ToolResult.Ok(
                    $"Moved '{srcRel}' to '{dstRel}' with reversible source and destination edits.",
                    new
                    {
                        sourcePath = srcRel, destinationPath = dstRel,
                        transactionId = tx.Id, sourceEditId, destinationEditId,
                        undoOrder = "Use revert_edit with destinationEditId first, then sourceEditId.",
                        changedFiles = new[] { srcRel, dstRel }
                    }));
            });

        yield return new ToolDefinition(
            Name: "revert_rename",
            Category: "editing",
            Description: "Undo a verified rename as one crash-recoverable operation using the paired edit IDs returned by rename_file.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Restores the original path and removes the renamed copy, with linked reversible audit entries.",
            Parameters:
            [
                new("sourceEditId", "string", "Source edit ID returned by rename_file.", Required: true),
                new("destinationEditId", "string", "Destination edit ID returned by rename_file.", Required: true)
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var originalSourceId = args.RequireString("sourceEditId");
                var originalDestinationId = args.RequireString("destinationEditId");
                var srcEdit = db.GetEdit(originalSourceId);
                var dstEdit = db.GetEdit(originalDestinationId);
                if (srcEdit is null || dstEdit is null ||
                    srcEdit.Operation != "rename_source" ||
                    dstEdit.Operation != "rename_destination" ||
                    srcEdit.Reverted || dstEdit.Reverted ||
                    srcEdit.CreatedAtUtc != dstEdit.CreatedAtUtc ||
                    srcEdit.SessionId != dstEdit.SessionId ||
                    srcEdit.Workspace != ws || dstEdit.Workspace != ws ||
                    srcEdit.AfterSha256 != "deleted" ||
                    dstEdit.BeforeSha256 != "none" ||
                    !string.Equals(srcEdit.BeforeSha256, dstEdit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail("invalid_rename_pair",
                        "Rename edit pair is missing, unrelated, already reverted or from another workspace."));

                var src = guard.ResolvePath(srcEdit.RelativePath, forWrite: true);
                var dst = guard.ResolvePath(dstEdit.RelativePath, forDelete: true);
                if (File.Exists(src) || !File.Exists(dst))
                    return Task.FromResult(ToolResult.Fail("undo_conflict",
                        "Original path is already occupied or the renamed file no longer exists."));
                if (!string.Equals(ComputeFileSha256(dst), dstEdit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail("undo_conflict",
                        "Renamed file was modified after the move; refusing to overwrite newer work."));

                var sourceInverseId = NewEditId();
                var destinationInverseId = NewEditId();
                var destBackup = SaveBackupBytes(paths, ctx.SessionId, destinationInverseId, dst);
                var tx = new PatchTransaction
                {
                    Id = $"patch_{Guid.NewGuid():N}", Workspace = ws, SessionId = ctx.SessionId,
                    Items =
                    [
                        new PatchTransactionItem
                        {
                            RelativePath = srcEdit.RelativePath, EditId = sourceInverseId,
                            BeforeHash = "none", AfterHash = dstEdit.AfterSha256,
                            RevertsOriginalEditId = originalSourceId
                        },
                        new PatchTransactionItem
                        {
                            RelativePath = dstEdit.RelativePath, EditId = destinationInverseId,
                            BackupPath = destBackup, BeforeHash = dstEdit.AfterSha256,
                            AfterHash = "deleted", RevertsOriginalEditId = originalDestinationId
                        }
                    ]
                };
                var manifest = GetTransactionPath(paths, tx.Id);
                PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                try
                {
                    if (File.Exists(src) ||
                        !string.Equals(ComputeFileSha256(dst), dstEdit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Source or destination changed during rename undo.");

                    File.Move(dst, src);
                    if (File.Exists(dst) ||
                        !string.Equals(ComputeFileSha256(src), dstEdit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Rename undo checksum verification failed.");

                    var now = DateTimeOffset.UtcNow;
                    db.RecordRenameReversal(
                        new EditJournalRecord(sourceInverseId, ctx.SessionId, ws,
                            srcEdit.RelativePath, "revert_rename_source", "none",
                            dstEdit.AfterSha256, false, null, 0, 0, false, now),
                        new EditJournalRecord(destinationInverseId, ctx.SessionId, ws,
                            dstEdit.RelativePath, "revert_rename_destination",
                            dstEdit.AfterSha256, "deleted", true, destBackup, 0, 0, false, now),
                        originalSourceId, originalDestinationId);

                    tx.Committed = true;
                    PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
                    try { File.Delete(manifest); } catch (IOException) { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        RecoverTransaction(paths, guard, tx, db);
                        File.Delete(manifest);
                        return Task.FromResult(ToolResult.Fail("rename_undo_rolled_back",
                            $"Rename undo failed without altering the saved renamed file: {ex.Message}"));
                    }
                    catch (Exception recovery)
                    {
                        return Task.FromResult(ToolResult.Fail("rename_undo_recovery_required",
                            $"Rename undo failed ({ex.Message}); manual recovery check required ({recovery.Message}). Manifest: {manifest}"));
                    }
                }

                onFileMutated?.Invoke(src);
                onFileMutated?.Invoke(dst);
                return Task.FromResult(ToolResult.Ok(
                    $"Restored '{srcEdit.RelativePath}' from '{dstEdit.RelativePath}'.",
                    new
                    {
                        sourcePath = srcEdit.RelativePath, destinationPath = dstEdit.RelativePath,
                        sourceInverseId, destinationInverseId, changedFiles = new[]
                            { srcEdit.RelativePath, dstEdit.RelativePath }
                    }));
            });

        yield return new ToolDefinition(
            Name: "delete_file",
            Category: "editing",
            Description: "Deletes a workspace file only after preserving and verifying a reversible backup (up to 256 MiB); larger deletions are refused.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Deletes a workspace file only with a durable recovery manifest and verified undo backup.",
            Parameters:
            [
                new("path", "string", "Relative file path to delete.", Required: true),
                new("expectedSha256", "string", "Optional SHA-256 precondition check."),
                new("allowIrreversible", "boolean", "Deprecated; irrevocable remote deletion is forbidden.", Default: false)
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var full = guard.ResolvePath(args.RequireString("path"), forDelete: true);
                if (!File.Exists(full))
                    throw new FileNotFoundException($"File not found: {args.RequireString("path")}");

                var beforeSha = ComputeFileSha256(full);
                var expected = args.OptionalString("expectedSha256");
                if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(beforeSha, expected, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ToolResult.Fail("stale_file", $"File SHA-256 mismatch (expected {expected}, actual {beforeSha})."));

                var fi = new FileInfo(full);
                if (fi.Length > MaxReversibleDeleteBytes)
                {
                    return Task.FromResult(ToolResult.Fail("backup_limit_exceeded",
                        $"Refusing deletion of {fi.Length} bytes: reversible backup limit is {MaxReversibleDeleteBytes} bytes. This tool cannot perform irreversible deletions."));
                }

                var editId = NewEditId();
                string backup = SaveBackupBytes(paths, ctx.SessionId, editId, full);
                var rel = guard.GetRelativePathUnix(ws, full);

                // Detect changed or incomplete backup contents BEFORE deleting the original.
                // Fail closed: a recoverable deletion must have a valid snapshot on disk.
                if (backup is not null)
                {
                    var backupSha = ComputeFileSha256(backup);
                    var sourceSha = ComputeFileSha256(full);
                    if (!string.Equals(backupSha, beforeSha, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(sourceSha, beforeSha, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(backup);
                        return Task.FromResult(ToolResult.Fail("stale_file",
                            $"Refusing to delete '{rel}' because the file changed while its undo backup was created."));
                    }
                }

                var transactionResult = CommitSingleFileEdit(paths, guard, db, ctx.SessionId, ws, rel, full,
                    editId, "delete_file", beforeSha, "deleted", backup, true, 0, 0,
                    () => File.Delete(full));
                if (transactionResult is not null) return Task.FromResult(transactionResult);

                onFileMutated?.Invoke(full);
                return Task.FromResult(ToolResult.Ok(
                    $"Deleted '{rel}' (reversible via editId='{editId}').",
                    new { editId, path = rel, beforeSha256 = beforeSha, reversible = true, changedFiles = new[] { rel } }));
            });

        yield return new ToolDefinition(
            Name: "inspect_diff",
            Category: "editing",
            Description: "Lazy-loads a unified diff for a recorded editId (before vs current file) or lists recent session edits.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("editId", "string", "Optional specific editId to compute unified diff for."),
                new("limit", "integer", "Number of recent edit summaries to return when editId is omitted.", Default: 15)
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var editId = args.OptionalString("editId");
                if (string.IsNullOrWhiteSpace(editId))
                {
                    var recent = db.ListRecentEdits(ctx.SessionId, args.OptionalInt("limit", 15));
                    return Task.FromResult(ToolResult.Ok(
                        $"Retrieved {recent.Count} recent edit(s).",
                        new
                        {
                            edits = recent.Select(e => new
                            {
                                e.EditId, e.RelativePath, e.Operation, e.LinesAdded, e.LinesRemoved, e.Reverted, e.CreatedAtUtc
                            })
                        }));
                }

                var edit = db.GetEdit(editId) ?? throw new KeyNotFoundException($"Edit '{editId}' not found.");
                if (!string.Equals(WorkspaceGuard.NormalizeRoot(ws), WorkspaceGuard.NormalizeRoot(edit.Workspace),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return Task.FromResult(ToolResult.Fail("wrong_workspace",
                        "Select the edit's original workspace before inspecting its diff."));

                var full = guard.ResolvePath(edit.RelativePath);
                var backupPath = edit.BackupFilePath is null ? null : ResolveBackupPath(paths, edit);
                var beforeExists = backupPath is not null && File.Exists(backupPath);
                var afterExists = File.Exists(full);

                // A deleted file can be much larger than the text editor's 8 MiB cap.
                // Don't report an error or invent a text diff for binary snapshots.
                var canReadText = (!beforeExists || new FileInfo(backupPath!).Length <= MaxEditableFileBytes) &&
                                  (!afterExists || new FileInfo(full).Length <= MaxEditableFileBytes);
                string? diff = null;
                if (canReadText)
                {
                    try
                    {
                        var oldLines = beforeExists ? LoadTextFile(backupPath!).Lines : [];
                        var curLines = afterExists ? LoadTextFile(full).Lines : [];
                        diff = BuildCompactUnifiedDiff(edit.RelativePath, oldLines, curLines);
                    }
                    catch (InvalidOperationException) { }
                    catch (DecoderFallbackException) { }
                }

                return Task.FromResult(ToolResult.Ok(
                    diff is null ? $"Edit '{edit.EditId}' has binary or oversized content; showing checksums only."
                                 : $"Computed diff for edit '{edit.EditId}' on '{edit.RelativePath}'.",
                    new
                    {
                        edit.EditId,
                        path = edit.RelativePath,
                        edit.Operation,
                        edit.BeforeSha256,
                        edit.AfterSha256,
                        edit.LinesAdded,
                        edit.LinesRemoved,
                        edit.Reverted,
                        diffAvailable = diff is not null,
                        diff,
                        note = diff is null ? "Text diff unavailable for binary, non-text or oversized files. Use saved SHA-256 and edit journal instead." : null
                    }));
            });

        yield return new ToolDefinition(
            Name: "revert_edit",
            Category: "editing",
            Description: "Restores a recorded edit only when its current postimage matches SHA-256. The undo itself is crash-recoverable and receives its own edit ID.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Restores pre-edit file bytes (or removes a newly created file) and records a reversible undo transaction.",
            Parameters:
            [
                new("editId", "string", "Recorded edit to undo, within the currently selected workspace.", Required: true)
            ],
            Handler: (args, ctx, _) =>
            {
                var editId = args.RequireString("editId");
                var edit = db.GetEdit(editId) ?? throw new KeyNotFoundException($"Edit '{editId}' not found.");
                if (edit.Reverted)
                    return Task.FromResult(ToolResult.Fail("already_reverted", $"Edit '{editId}' has already been reverted."));

                var ws = guard.RequireActiveWorkspace();
                if (!string.Equals(WorkspaceGuard.NormalizeRoot(ws), WorkspaceGuard.NormalizeRoot(edit.Workspace),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return Task.FromResult(ToolResult.Fail("wrong_workspace", "Select the edit's original workspace before reverting it."));

                var full = guard.ResolvePath(edit.RelativePath, forWrite: true, explicitWorkspace: ws);
                var existsNow = File.Exists(full);
                if (edit.AfterSha256 == "deleted")
                {
                    if (existsNow)
                        return Task.FromResult(ToolResult.Fail("conflict",
                            $"Cannot restore '{edit.RelativePath}': another file already exists at the destination."));
                }
                else
                {
                    if (!existsNow)
                        return Task.FromResult(ToolResult.Fail("conflict", $"File '{edit.RelativePath}' no longer exists."));
                    var actualHash = ComputeFileSha256(full);
                    if (!string.Equals(actualHash, edit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(ToolResult.Fail("conflict",
                            $"Cannot undo '{editId}': current file differs from the recorded postimage ({actualHash})."));
                }

                string? originalSnapshot = null;
                if (edit.ExistedBefore)
                {
                    originalSnapshot = ResolveBackupPath(paths, edit);
                    if (!File.Exists(originalSnapshot))
                        return Task.FromResult(ToolResult.Fail("backup_unavailable", $"Verified backup for '{editId}' is unavailable."));
                    if (!string.Equals(ComputeFileSha256(originalSnapshot), edit.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(ToolResult.Fail("backup_corrupt", "Undo snapshot failed SHA-256 verification; no changes made."));
                }

                // Undo is another mutation. First save its own postimage and manifest;
                // never delete/rewrite directly and then attempt to mark it reverted.
                var undoId = NewEditId();
                var currentHash = existsNow ? edit.AfterSha256 : "none";
                var restoredHash = edit.ExistedBefore ? edit.BeforeSha256 : "deleted";
                var undoBackup = existsNow ? SaveBackupBytes(paths, ctx.SessionId, undoId, full) : null;
                var restoredSnapshot = originalSnapshot;
                var transactionResult = CommitSingleFileEdit(paths, guard, db, ctx.SessionId, ws,
                    edit.RelativePath, full, undoId, "revert_edit", currentHash, restoredHash,
                    undoBackup, existsNow, 0, 0,
                    () =>
                    {
                        if (restoredSnapshot is null)
                            File.Delete(full);
                        else
                            PortablePaths.AtomicRestoreBackupFile(restoredSnapshot, full);
                    }, revertOriginalEditId: editId);
                if (transactionResult is not null)
                    return Task.FromResult(transactionResult);

                onFileMutated?.Invoke(full);
                return Task.FromResult(ToolResult.Ok(
                    $"Restored '{edit.RelativePath}' from edit '{editId}'. Reversal is itself undoable.",
                    new { editId, undoEditId = undoId, path = edit.RelativePath, restoredSha256 = restoredHash,
                          changedFiles = new[] { edit.RelativePath } }));
            });
    }

    public static LoadedTextFile LoadTextFile(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        if (fi.Length > MaxEditableFileBytes)
            throw new InvalidOperationException($"File exceeds {MaxEditableFileBytes / (1024 * 1024)} MB edit limit; use read_range instead.");

        var bytes = File.ReadAllBytes(fullPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var (encoding, encName, bomLen) = DetectEncoding(bytes);

        // Reject obvious binary files (NUL byte outside UTF-16).
        if (!encName.StartsWith("utf-16", StringComparison.Ordinal))
        {
            for (var i = bomLen; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                    throw new InvalidOperationException("File appears to be binary (contains NUL bytes).");
            }
        }

        var text = encoding.GetString(bytes, bomLen, bytes.Length - bomLen);
        var crlfCount = 0;
        var bareLfCount = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r') crlfCount++;
                else bareLfCount++;
            }
        }
        var newLine = crlfCount > bareLfCount
            ? "\r\n"
            : (bareLfCount > 0 ? "\n" : (OperatingSystem.IsWindows() ? "\r\n" : "\n"));
        var endsWithNewLine = text.EndsWith('\n');
        var lines = SplitLinesNoEnding(text);

        return new LoadedTextFile(text, lines, encoding, encName, newLine, endsWithNewLine, sha, bytes.Length);
    }

    private static (Encoding Enc, string Name, int BomLen) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), "utf-8-bom", 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), "utf-16-le", 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), "utf-16-be", 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), "utf-8", 0);
    }

    public static string ComputeFileSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string SaveBackupBytes(PortablePaths paths, string sessionId, string editId, string sourceFile)
    {
        var dir = paths.GetSessionEditsDir(sessionId);
        var backup = Path.Combine(dir, $"{editId}.bak");
        try
        {
            using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(backup, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 8192, FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            if (!string.Equals(ComputeFileSha256(sourceFile), ComputeFileSha256(backup),
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("Backup SHA-256 verification failed; original file was not modified.");
            return backup;
        }
        catch
        {
            // Never strand a half-written snapshot after a disk-full or read failure.
            try { File.Delete(backup); } catch (IOException) { }
            throw;
        }
    }

    private static string NewEditId()
        => $"edit_{DateTimeOffset.UtcNow:HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    private static string NormalizeLineEndings(string text, string newLine)
        => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newLine);

    private static string[] SplitLinesNoEnding(string text)
    {
        if (text.Length == 0) return [];
        var norm = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (norm.EndsWith('\n'))
            norm = norm[..^1];
        return norm.Split('\n');
    }

    private static string JoinLinesPreservingStyle(IReadOnlyList<string> lines, string newLine, bool trailingNewLine)
    {
        if (lines.Count == 0) return string.Empty;
        var joined = string.Join(newLine, lines);
        return trailingNewLine ? joined + newLine : joined;
    }

    private static int CountLines(string text)
        => text.Length == 0 ? 0 : SplitLinesNoEnding(text).Length;

    private static List<FilePatch> ParseUnifiedDiff(string patchText)
    {
        var rawLines = patchText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new List<FilePatch>();
        var i = 0;

        while (i < rawLines.Length)
        {
            if (!rawLines[i].StartsWith("--- ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            if (i + 1 >= rawLines.Length || !rawLines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var oldPath = CleanDiffHeaderPath(rawLines[i][4..]);
            var newPath = CleanDiffHeaderPath(rawLines[i + 1][4..]);
            // Git prefixes must be a matching asymmetric a/old + b/new pair.
            // A plain '--- a/config.txt' / '+++ a/config.txt' refers to an
            // actual a/ directory; silently removing the prefix edits the wrong file.
            if (oldPath.StartsWith("a/", StringComparison.Ordinal) &&
                newPath.StartsWith("b/", StringComparison.Ordinal) &&
                string.Equals(oldPath[2..], newPath[2..], StringComparison.Ordinal))
            {
                oldPath = oldPath[2..];
                newPath = newPath[2..];
            }
            else if (oldPath == "/dev/null" && newPath.StartsWith("b/", StringComparison.Ordinal))
                newPath = newPath[2..];
            else if (newPath == "/dev/null" && oldPath.StartsWith("a/", StringComparison.Ordinal))
                oldPath = oldPath[2..];
            i += 2;

            var hunks = new List<DiffHunk>();
            while (i < rawLines.Length)
            {
                if (rawLines[i].StartsWith("--- ", StringComparison.Ordinal) ||
                    rawLines[i].StartsWith("diff --git ", StringComparison.Ordinal))
                    break;

                var m = HunkHeaderRegex().Match(rawLines[i]);
                if (!m.Success)
                {
                    i++;
                    continue;
                }

                var oldStart = int.Parse(m.Groups["oldStart"].Value);
                var oldCount = m.Groups["oldCount"].Success ? int.Parse(m.Groups["oldCount"].Value) : 1;
                var newStart = int.Parse(m.Groups["newStart"].Value);
                var newCount = m.Groups["newCount"].Success ? int.Parse(m.Groups["newCount"].Value) : 1;
                i++;

                var hunkLines = new List<string>();
                int seenOld = 0, seenNew = 0;
                while (i < rawLines.Length)
                {
                    var hl = rawLines[i];
                    if (hl.StartsWith("@@ ", StringComparison.Ordinal) ||
                        hl.StartsWith("--- ", StringComparison.Ordinal) ||
                        hl.StartsWith("diff --git ", StringComparison.Ordinal))
                        break;

                    if (hl.StartsWith("\\ No newline", StringComparison.Ordinal))
                        throw new ArgumentException("Unified patch with no-newline marker is not supported; use replace_range with an expected SHA-256 to preserve exact bytes.");

                    if (hl.Length == 0)
                    {
                        if (seenOld >= oldCount && seenNew >= newCount)
                        {
                            i++;
                            break;
                        }
                        // Treat empty line inside hunk as blank context line.
                        hunkLines.Add(" ");
                        seenOld++;
                        seenNew++;
                        i++;
                        continue;
                    }

                    var prefix = hl[0];
                    if (prefix == ' ') { hunkLines.Add(hl); seenOld++; seenNew++; i++; }
                    else if (prefix == '-') { hunkLines.Add(hl); seenOld++; i++; }
                    else if (prefix == '+') { hunkLines.Add(hl); seenNew++; i++; }
                    else break;
                }

                if (seenOld != oldCount || seenNew != newCount)
                    throw new ArgumentException($"Invalid unified patch: hunk counts expected {oldCount}/{newCount}, got {seenOld}/{seenNew}.");
                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, hunkLines));
            }

            if (hunks.Count > 0)
                result.Add(new FilePatch(oldPath, newPath, hunks));
        }

        return result;
    }

    private static string CleanDiffHeaderPath(string raw)
    {
        var tabIdx = raw.IndexOf('\t');
        var p = (tabIdx >= 0 ? raw[..tabIdx] : raw).Trim().Trim('"');
        if (p == "/dev/null") return p;
        // Do not strip a/ or b/ without inspecting the matching header pair.
        return p;
    }

    private static List<string>? ApplyHunksToLines(
        IReadOnlyList<string> source,
        IReadOnlyList<DiffHunk> hunks,
        string displayPath,
        out int added,
        out int removed,
        out string? error)
    {
        added = 0;
        removed = 0;
        error = null;
        var output = new List<string>(source.Count + 16);
        var cursor = 0; // 0-based index in source

        for (var hIdx = 0; hIdx < hunks.Count; hIdx++)
        {
            var h = hunks[hIdx];
            // Unified diff empty OLD ranges designate the line *before*
            // insertion: -N,0 inserts after the existing Nth line.
            var targetIdx = h.OldCount == 0 ? h.OldStart : h.OldStart == 0 ? 0 : h.OldStart - 1;

            // Pure additions have no source context to justify fuzzy relocation.
            // Never silently shift them to a different line.
            var matchedIdx = h.OldCount == 0
                ? (targetIdx >= cursor && targetIdx <= source.Count ? targetIdx : -1)
                : FindMatchingPosition(source, h, targetIdx, cursor);
            if (matchedIdx < 0)
            {
                error = $"Patch precondition failed in '{displayPath}' at hunk #{hIdx + 1} (around line {h.OldStart}): context or removed lines do not match current file.";
                return null;
            }

            while (cursor < matchedIdx)
            {
                output.Add(source[cursor]);
                cursor++;
            }

            foreach (var hl in h.Lines)
            {
                var kind = hl[0];
                var body = hl.Length > 1 ? hl[1..] : "";
                if (kind == ' ')
                {
                    output.Add(source[cursor]);
                    cursor++;
                }
                else if (kind == '-')
                {
                    removed++;
                    cursor++;
                }
                else if (kind == '+')
                {
                    output.Add(body);
                    added++;
                }
            }
        }

        while (cursor < source.Count)
        {
            output.Add(source[cursor]);
            cursor++;
        }

        return output;
    }

    private static int FindMatchingPosition(IReadOnlyList<string> source, DiffHunk hunk, int preferred, int minIdx)
    {
        if (MatchesAt(source, hunk, preferred, minIdx))
            return preferred;

        var candidate = -1;
        for (var delta = 1; delta <= 3; delta++)
        {
            foreach (var pos in new[] { preferred - delta, preferred + delta })
            {
                if (!MatchesAt(source, hunk, pos, minIdx)) continue;
                if (candidate >= 0) return -1; // ambiguous fuzzy match: don't guess
                candidate = pos;
            }
        }
        return candidate;
    }

    private static bool MatchesAt(IReadOnlyList<string> source, DiffHunk hunk, int startIdx, int minIdx)
    {
        if (startIdx < minIdx || startIdx > source.Count) return false;
        var pos = startIdx;
        foreach (var hl in hunk.Lines)
        {
            var kind = hl[0];
            if (kind == '+') continue;
            var expected = hl.Length > 1 ? hl[1..] : "";
            if (pos >= source.Count) return false;
            if (!string.Equals(source[pos], expected, StringComparison.Ordinal))
                return false;
            pos++;
        }
        return true;
    }

    public static string BuildCompactUnifiedDiff(string relPath, IReadOnlyList<string> before, IReadOnlyList<string> after, int maxChars = 4000)
    {
        var sb = new StringBuilder();
        sb.Append($"--- a/{relPath}\n+++ b/{relPath}\n");

        // Find common prefix and suffix for a fast, zero-bloat bounded diff.
        var prefix = 0;
        var minLen = Math.Min(before.Count, after.Count);
        while (prefix < minLen && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
            prefix++;

        var suffix = 0;
        while (suffix < (minLen - prefix) &&
               string.Equals(before[before.Count - 1 - suffix], after[after.Count - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        if (prefix == before.Count && prefix == after.Count)
            return sb.Append("(no changes)").ToString();

        var ctxStart = Math.Max(0, prefix - 2);
        var oldEnd = Math.Min(before.Count, before.Count - suffix + 2);
        var newEnd = Math.Min(after.Count, after.Count - suffix + 2);

        sb.Append($"@@ -{ctxStart + 1},{oldEnd - ctxStart} +{ctxStart + 1},{newEnd - ctxStart} @@\n");
        for (var i = ctxStart; i < prefix && sb.Length < maxChars; i++)
            sb.Append($" {before[i]}\n");
        for (var i = prefix; i < before.Count - suffix && sb.Length < maxChars; i++)
            sb.Append($"-{before[i]}\n");
        for (var i = prefix; i < after.Count - suffix && sb.Length < maxChars; i++)
            sb.Append($"+{after[i]}\n");
        for (var i = before.Count - suffix; i < oldEnd && sb.Length < maxChars; i++)
            sb.Append($" {before[i]}\n");

        if (sb.Length >= maxChars)
            sb.Append("... [diff preview truncated]\n");
        return sb.ToString().TrimEnd();
    }

    private static string ResolveBackupPath(PortablePaths paths, EditJournalRecord edit)
    {
        // Backups are always produced under session/edits; recovering a relocated
        // portable app must not depend on the original absolute installation path.
        var local = Path.Combine(paths.GetSessionEditsDir(edit.SessionId), edit.EditId + ".bak");
        if (File.Exists(local)) return local;
        return edit.BackupFilePath ?? local;
    }

    private sealed class PatchTransaction
    {
        public string Id { get; set; } = "";
        public string Workspace { get; set; } = "";
        public string SessionId { get; set; } = "";
        public bool Committed { get; set; }
        public List<PatchTransactionItem> Items { get; set; } = [];
    }

    private sealed class PatchTransactionItem
    {
        public string RelativePath { get; set; } = "";
        public string EditId { get; set; } = "";
        public string? BackupPath { get; set; }
        public string BeforeHash { get; set; } = "";
        public string AfterHash { get; set; } = "";
        public string? RevertsOriginalEditId { get; set; }
    }

    private static byte[] EncodeText(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>
    /// A regular edit is also a durable transaction. Its manifest is persisted before
    /// the first mutation; after a crash, startup either verifies the committed result
    /// or restores the exact preimage. If another editor changed it, startup blocks.
    /// </summary>
    private static ToolResult? CommitSingleFileEdit(
        PortablePaths paths, WorkspaceGuard guard, StateDatabase db,
        string sessionId, string workspace, string relative, string full,
        string editId, string operation, string beforeSha, string afterSha,
        string? backupPath, bool existedBefore, int added, int removed, Action mutation,
        string? revertOriginalEditId = null)
    {
        var tx = new PatchTransaction
        {
            Id = $"patch_{Guid.NewGuid():N}", Workspace = workspace, SessionId = sessionId,
            Items = [new PatchTransactionItem
            {
                RelativePath = relative, EditId = editId, BackupPath = backupPath,
                BeforeHash = beforeSha, AfterHash = afterSha,
                RevertsOriginalEditId = revertOriginalEditId
            }]
        };
        var manifest = GetTransactionPath(paths, tx.Id);
        PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
        try
        {
            var actual = File.Exists(full) ? ComputeFileSha256(full) : "none";
            if (!string.Equals(actual, beforeSha, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"File '{relative}' changed after it was inspected; refusing a stale write.");
            mutation();
            actual = File.Exists(full) ? ComputeFileSha256(full) : "deleted";
            if (!string.Equals(actual, afterSha, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Post-write verification failed for '{relative}'.");
            var journalEntry = new EditJournalRecord(editId, sessionId, workspace, relative,
                operation, beforeSha, afterSha, existedBefore, backupPath, added, removed,
                false, DateTimeOffset.UtcNow);
            if (revertOriginalEditId is null) db.RecordEdit(journalEntry);
            else db.RecordEditAndRevert(journalEntry, revertOriginalEditId);
            tx.Committed = true;
            PortablePaths.AtomicWriteAllText(manifest, JsonSerializer.Serialize(tx, AppConfig.JsonOptions));
            try { File.Delete(manifest); } catch (IOException) { }
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                RecoverTransaction(paths, guard, tx, db);
                File.Delete(manifest);
                return ToolResult.Fail("edit_rolled_back", $"Edit aborted and original verified: {ex.Message}");
            }
            catch (Exception recovery)
            {
                // Persist the manifest: refuse future operations until data integrity
                // can be determined, rather than guessing how to undo an interruption.
                return ToolResult.Fail("edit_recovery_required",
                    $"Edit failed ({ex.Message}) and recovery needs attention ({recovery.Message}). Manifest: {manifest}");
            }
        }
    }

    private static string GetTransactionPath(PortablePaths paths, string transactionId)
    {
        var dir = Path.Combine(paths.DataDir, "pending-patches");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, transactionId + ".json");
    }

    /// <summary>
    /// Recover only known pre/post-image hashes. A third-party edit is a conflict, never
    /// permission to overwrite the user's newer work. Startup calls this under host lock.
    /// </summary>
    public static void RecoverPendingPatchTransactions(PortablePaths paths, WorkspaceGuard guard, StateDatabase db)
    {
        var dir = Path.Combine(paths.DataDir, "pending-patches");
        if (!Directory.Exists(dir)) return;
        foreach (var manifest in Directory.EnumerateFiles(dir, "patch_*.json"))
        {
            var tx = JsonSerializer.Deserialize<PatchTransaction>(File.ReadAllText(manifest), AppConfig.JsonOptions)
                ?? throw new IOException($"Invalid recovery manifest: {manifest}");
            // A committed marker means the bytes were checked and the journal was
            // persisted. Never require the target to remain unchanged forever; later
            // authorized edits legitimately change its hash. Only incomplete edits roll back.
            if (!tx.Committed) RecoverTransaction(paths, guard, tx, db);
            File.Delete(manifest);
        }
    }

    private static string? ResolvePendingBackupPath(PortablePaths paths, PatchTransaction tx, PatchTransactionItem item)
    {
        // A portable RAMCA folder may move between a crash and the next launch.
        // Session/edit IDs are durable references; an absolute backup path is not.
        if (!string.IsNullOrWhiteSpace(tx.SessionId))
        {
            var local = Path.Combine(paths.GetSessionEditsDir(tx.SessionId), item.EditId + ".bak");
            if (WorkspaceGuard.IsEqualOrInside(local, paths.SessionsDir)) return local;
            return null;
        }
        // Only old manifests lacking SessionId may use their original stored path,
        // and never if it points outside the portable session-storage directory.
        if (item.BackupPath is not null && WorkspaceGuard.IsEqualOrInside(item.BackupPath, paths.SessionsDir))
            return item.BackupPath;
        return null;
    }

    private static string RecoveryDisplacementPath(string full)
        => Path.Combine(Path.GetDirectoryName(full)!, $".ramca-recovery-{Guid.NewGuid():N}.bak");

    /// <summary>
    /// Restores verified pre-image while first preserving the ACTUAL overwritten
    /// target bytes in a same-volume file. If a late external edit wins the race,
    /// restore the displaced bytes and fail closed; never delete the evidence.
    /// </summary>
    internal static void RestoreTrackedBackupSafely(string verifiedBackup, string target,
        string expectedBeforeHash, string expectedAfterHash)
    {
        var dir = Path.GetDirectoryName(target) ?? throw new IOException("Recovery target has no parent.");
        Directory.CreateDirectory(dir);
        var staged = Path.Combine(dir, $".ramca-restore-{Guid.NewGuid():N}.tmp");
        var displaced = RecoveryDisplacementPath(target);
        try
        {
            using (var source = new FileStream(verifiedBackup, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dest = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 81920, FileOptions.WriteThrough))
            {
                source.CopyTo(dest, 81920);
                dest.Flush(flushToDisk: true);
            }
            if (!string.Equals(ComputeFileSha256(staged), expectedBeforeHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recovery snapshot changed while being staged; original remains untouched.");

            if (!File.Exists(target))
            {
                // If an external editor creates the target here, this must fail,
                // not overwrite a file created after preflight.
                File.Move(staged, target, overwrite: false);
                return;
            }

            if (OperatingSystem.IsWindows())
                File.Replace(staged, target, displaced, ignoreMetadataErrors: false);
            else
            {
                // Unix compatibility does not offer an atomic multi-name compare/
                // replace primitive; preserve displaced content if the move fails.
                File.Move(target, displaced, overwrite: false);
                try { File.Move(staged, target, overwrite: false); }
                catch
                {
                    try { File.Move(displaced, target, overwrite: false); } catch { }
                    throw;
                }
            }

            var displacedHash = ComputeFileSha256(displaced);
            if (string.Equals(displacedHash, expectedAfterHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(displaced);
                return;
            }

            // A late save changed the bytes after the preflight. Restore the
            // displaced human file, retaining the RAMCA preimage as forensic
            // evidence if another writer races our corrective replacement.
            var savedPreimage = RecoveryDisplacementPath(target);
            try
            {
                if (OperatingSystem.IsWindows())
                    File.Replace(displaced, target, savedPreimage, ignoreMetadataErrors: false);
                else
                {
                    File.Move(target, savedPreimage, overwrite: false);
                    File.Move(displaced, target, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                throw new IOException($"Recovery conflict: late external content preserved at '{displaced}', but could not be moved back to '{target}': {ex.Message}", ex);
            }

            throw new IOException($"Recovery conflict: '{target}' changed during rollback; external content was restored. Verified RAMCA snapshot retained at '{verifiedBackup}' and '{savedPreimage}'.");
        }
        finally
        {
            if (File.Exists(staged)) try { File.Delete(staged); } catch (IOException) { }
        }
    }

    private static void RecoverTransaction(PortablePaths paths, WorkspaceGuard guard, PatchTransaction tx, StateDatabase db)
    {
        using var workspaceLease = guard.UseInvocationWorkspace(tx.Workspace);
        // Preflight EVERY target before restoring any; a conflict must never cause
        // partial rollback of other files or destroy a newer external edit.
        var targets = new List<(PatchTransactionItem Item, string Full)>();
        foreach (var item in tx.Items)
        {
            var full = guard.ResolvePath(item.RelativePath, forWrite: true, explicitWorkspace: tx.Workspace);
            var actual = File.Exists(full) ? ComputeFileSha256(full) : "deleted";
            if (item.BeforeHash == "none" && actual == "deleted")
            {
                targets.Add((item, full));
                continue;
            }
            if (!string.Equals(actual, item.BeforeHash, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(actual, item.AfterHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Recovery conflict at '{item.RelativePath}' (actual SHA-256 {actual}).");
            if (item.BeforeHash != "none")
            {
                var safeBackup = ResolvePendingBackupPath(paths, tx, item);
                if (safeBackup is null || !File.Exists(safeBackup) ||
                    !string.Equals(ComputeFileSha256(safeBackup), item.BeforeHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Recovery snapshot unavailable or corrupted for '{item.RelativePath}'.");
            }
            targets.Add((item, full));
        }
        foreach (var (item, full) in targets)
        {
            var actual = File.Exists(full) ? ComputeFileSha256(full) : "deleted";
            if (string.Equals(actual, item.BeforeHash, StringComparison.OrdinalIgnoreCase) ||
                item.BeforeHash == "none" && actual == "deleted")
                continue;
            if (item.BeforeHash == "none")
            {
                // Move first, verify displaced bytes, and only then delete.
                // A late external save must never be irreversibly removed.
                if (actual != "deleted")
                {
                    var displaced = RecoveryDisplacementPath(full);
                    File.Move(full, displaced, overwrite: false);
                    if (!string.Equals(ComputeFileSha256(displaced), item.AfterHash, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Move(displaced, full, overwrite: false); } catch { }
                        throw new IOException($"Recovery encountered a late external modification of '{item.RelativePath}'. Preserved displaced bytes at '{displaced}' if restoration failed.");
                    }
                    File.Delete(displaced);
                }
            }
            else
                RestoreTrackedBackupSafely(
                    ResolvePendingBackupPath(paths, tx, item) ?? throw new IOException("Recovery snapshot not found."),
                    full, item.BeforeHash, item.AfterHash);
        }
        foreach (var (item, _) in targets)
        {
            if (item.RevertsOriginalEditId is not null)
                db.ReconcileRolledBackInverse(item.EditId, item.RevertsOriginalEditId);
            else if (db.GetEdit(item.EditId) is not null)
                db.MarkEditReverted(item.EditId);
        }
    }
}
