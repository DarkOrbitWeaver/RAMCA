using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
                        if (sb.Length + line.Length + 1 > maxChars && i > 0)
                        {
                            truncated = true;
                            break;
                        }
                        if (sb.Length > 0) sb.Append('\n');
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
                new("endLine", "integer", "1-based inclusive ending line number.", Required: true)
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
                            lines.Add(new { line = totalLines, text = line });
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append($"{totalLines,5}: {line}");
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
                        formatted = sb.ToString(),
                        lines
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
                PortablePaths.AtomicWriteAllText(full, normalized, encoding);
                var afterSha = ComputeFileSha256(full);
                var newLineCount = CountLines(normalized);
                var rel = guard.GetRelativePathUnix(ws, full);

                db.RecordEdit(new EditJournalRecord(
                    editId, ctx.SessionId, ws, rel, existed ? "overwrite_file" : "create_file",
                    beforeSha, afterSha, existed, backupPath, newLineCount, oldLineCount, false, DateTimeOffset.UtcNow));

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
                PortablePaths.AtomicWriteAllText(full, joined, current.Encoding);

                var afterSha = ComputeFileSha256(full);
                var rel = guard.GetRelativePathUnix(ws, full);
                var removed = end - start + 1;
                var added = replacementLines.Length;

                db.RecordEdit(new EditJournalRecord(
                    editId, ctx.SessionId, ws, rel, "replace_range",
                    current.Sha256, afterSha, true, backupPath, added, removed, false, DateTimeOffset.UtcNow));

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
                new("expectedSha256", "string", "Optional SHA-256 precondition (for single-file patches).")
            ],
            Handler: (args, ctx, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var patchText = args.RequireRawString("patch");
                var expectedSha = args.OptionalString("expectedSha256");

                var filePatches = ParseUnifiedDiff(patchText);
                if (filePatches.Count == 0)
                    return Task.FromResult(ToolResult.Fail("invalid_patch", "No valid unified diff file hunks found. Include --- a/file, +++ b/file, and @@ -l,c +l,c @@ headers."));

                // Phase 1: Validate every file and compute full post-patch content in memory before touching disk.
                var staged = new List<(string RelPath, string FullPath, string Op, LoadedTextFile? Before, string? NewContent, Encoding Enc, int Added, int Removed)>();

                foreach (var fp in filePatches)
                {
                    var isCreate = fp.OldPath is null or "/dev/null";
                    var isDelete = fp.NewPath is null or "/dev/null";
                    var targetRel = isDelete ? fp.OldPath! : fp.NewPath!;
                    var full = guard.ResolvePath(targetRel, forWrite: !isDelete, forDelete: isDelete);
                    var cleanRel = guard.GetRelativePathUnix(ws, full);

                    if (isCreate)
                    {
                        if (File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("precondition_failed", $"Patch attempts to create '{cleanRel}', but file already exists."));
                        var applied = ApplyHunksToLines([], fp.Hunks, cleanRel, out var added, out var removed, out var err);
                        if (err is not null)
                            return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                        var nl = OperatingSystem.IsWindows() ? "\r\n" : "\n";
                        staged.Add((cleanRel, full, "patch_create", null, JoinLinesPreservingStyle(applied!, nl, true), new UTF8Encoding(false), added, removed));
                    }
                    else
                    {
                        if (!File.Exists(full))
                            return Task.FromResult(ToolResult.Fail("not_found", $"Patch target file '{cleanRel}' does not exist."));
                        var before = LoadTextFile(full);
                        if (filePatches.Count == 1 && !string.IsNullOrWhiteSpace(expectedSha) &&
                            !string.Equals(before.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult(ToolResult.Fail("stale_file",
                                $"File '{cleanRel}' changed since read (expected {expectedSha}, actual {before.Sha256})."));
                        }

                        if (isDelete)
                        {
                            var applied = ApplyHunksToLines(before.Lines, fp.Hunks, cleanRel, out var added, out var removed, out var err);
                            if (err is not null)
                                return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                            staged.Add((cleanRel, full, "patch_delete", before, null, before.Encoding, added, removed));
                        }
                        else
                        {
                            var applied = ApplyHunksToLines(before.Lines, fp.Hunks, cleanRel, out var added, out var removed, out var err);
                            if (err is not null)
                                return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                            var joined = JoinLinesPreservingStyle(applied!, before.NewLine, before.EndsWithNewLine || applied!.Count > 0);
                            staged.Add((cleanRel, full, "apply_patch", before, joined, before.Encoding, added, removed));
                        }
                    }
                }

                // Phase 2: Commit atomically and record reversible edits.
                var fileResults = new List<object>();
                int totalAdded = 0, totalRemoved = 0;

                foreach (var item in staged)
                {
                    var editId = NewEditId();
                    string? backup = item.Before is not null ? SaveBackupBytes(paths, ctx.SessionId, editId, item.FullPath) : null;
                    string afterSha;

                    if (item.Op == "patch_delete")
                    {
                        File.Delete(item.FullPath);
                        afterSha = "deleted";
                    }
                    else
                    {
                        PortablePaths.AtomicWriteAllText(item.FullPath, item.NewContent!, item.Enc);
                        afterSha = ComputeFileSha256(item.FullPath);
                    }

                    db.RecordEdit(new EditJournalRecord(
                        editId, ctx.SessionId, ws, item.RelPath, item.Op,
                        item.Before?.Sha256 ?? "none", afterSha, item.Before is not null,
                        backup, item.Added, item.Removed, false, DateTimeOffset.UtcNow));

                    onFileMutated?.Invoke(item.FullPath);
                    totalAdded += item.Added;
                    totalRemoved += item.Removed;
                    fileResults.Add(new
                    {
                        editId,
                        path = item.RelPath,
                        operation = item.Op,
                        beforeSha256 = item.Before?.Sha256 ?? "none",
                        afterSha256 = afterSha,
                        linesAdded = item.Added,
                        linesRemoved = item.Removed
                    });
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Applied patch across {fileResults.Count} file(s) (+{totalAdded}/-{totalRemoved} lines).",
                    new
                    {
                        totalLinesAdded = totalAdded,
                        totalLinesRemoved = totalRemoved,
                        changedFiles = staged.Select(s => s.RelPath).ToArray(),
                        edits = fileResults
                    }));
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
                new("newText", "string", "Proposed replacement text (for range preview).")
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

                    var previews = new List<object>();
                    foreach (var fp in fps)
                    {
                        var isCreate = fp.OldPath is null or "/dev/null";
                        var target = fp.NewPath is null or "/dev/null" ? fp.OldPath! : fp.NewPath!;
                        var full = guard.ResolvePath(target);
                        var rel = guard.GetRelativePathUnix(ws, full);
                        var oldLines = isCreate ? [] : LoadTextFile(full).Lines;
                        var applied = ApplyHunksToLines(oldLines, fp.Hunks, rel, out var add, out var rem, out var err);
                        if (err is not null)
                            return Task.FromResult(ToolResult.Fail("precondition_failed", err));
                        previews.Add(new
                        {
                            path = rel,
                            wouldSucceed = true,
                            linesAdded = add,
                            linesRemoved = rem,
                            diff = BuildCompactUnifiedDiff(rel, oldLines, applied!)
                        });
                    }
                    return Task.FromResult(ToolResult.Ok($"Patch preview succeeded for {previews.Count} file(s).", new { previews }));
                }

                var path = args.RequireString("path");
                var fullFile = guard.ResolvePath(path);
                if (!File.Exists(fullFile))
                    throw new FileNotFoundException($"File not found: {path}");
                var loaded = LoadTextFile(fullFile);
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
            Description: "Renames or moves a file inside the trusted workspace.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Moves file within trusted workspace.",
            Parameters:
            [
                new("sourcePath", "string", "Existing relative file path.", Required: true),
                new("destinationPath", "string", "New relative file path inside workspace.", Required: true),
                new("overwrite", "boolean", "Overwrite destination if it exists.", Default: false)
            ],
            Handler: (args, _, _) =>
            {
                var ws = guard.RequireActiveWorkspace();
                var src = guard.ResolvePath(args.RequireString("sourcePath"), forDelete: true);
                var dst = guard.ResolvePath(args.RequireString("destinationPath"), forWrite: true);
                var overwrite = args.OptionalBool("overwrite", false);

                if (!File.Exists(src))
                    throw new FileNotFoundException($"Source file not found: {args.RequireString("sourcePath")}");
                if (File.Exists(dst) && !overwrite)
                    return Task.FromResult(ToolResult.Fail("already_exists", $"Destination '{args.RequireString("destinationPath")}' already exists."));

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst, overwrite);
                onFileMutated?.Invoke(src);
                onFileMutated?.Invoke(dst);

                var srcRel = guard.GetRelativePathUnix(ws, src);
                var dstRel = guard.GetRelativePathUnix(ws, dst);
                return Task.FromResult(ToolResult.Ok(
                    $"Moved '{srcRel}' -> '{dstRel}'.",
                    new { sourcePath = srcRel, destinationPath = dstRel, changedFiles = new[] { srcRel, dstRel } }));
            });

        yield return new ToolDefinition(
            Name: "delete_file",
            Category: "editing",
            Description: "Deletes a workspace file after preserving an undo backup (up to 256 MiB). Larger deletions require explicit allowIrreversible=true, and cannot be undone.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Deletes target file; saves a reversible backup unless irreversible deletion was explicitly requested for an oversized file.",
            Parameters:
            [
                new("path", "string", "Relative file path to delete.", Required: true),
                new("expectedSha256", "string", "Optional SHA-256 precondition check."),
                new("allowIrreversible", "boolean", "Explicitly permit deletion without a backup when the file exceeds the 256 MiB undo limit.", Default: false)
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
                if (fi.Length > MaxReversibleDeleteBytes && !args.OptionalBool("allowIrreversible", false))
                {
                    return Task.FromResult(ToolResult.Fail("backup_limit_exceeded",
                        $"Refusing an irreversible deletion of {fi.Length} bytes. Files over {MaxReversibleDeleteBytes} bytes require explicit allowIrreversible=true."));
                }

                var editId = NewEditId();
                string? backup = fi.Length <= MaxReversibleDeleteBytes ? SaveBackupBytes(paths, ctx.SessionId, editId, full) : null;
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

                File.Delete(full);
                db.RecordEdit(new EditJournalRecord(
                    editId, ctx.SessionId, ws, rel, "delete_file",
                    beforeSha, "deleted", true, backup, 0, 0, false, DateTimeOffset.UtcNow));

                onFileMutated?.Invoke(full);
                return Task.FromResult(ToolResult.Ok(
                    backup is null ? $"Deleted '{rel}' irreversibly (explicitly requested)." : $"Deleted '{rel}' (reversible via editId='{editId}').",
                    new { editId, path = rel, beforeSha256 = beforeSha, reversible = backup is not null, changedFiles = new[] { rel } }));
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
                var full = guard.ResolvePath(edit.RelativePath, explicitWorkspace: edit.Workspace);
                var oldLines = edit.BackupFilePath is not null && File.Exists(edit.BackupFilePath)
                    ? LoadTextFile(edit.BackupFilePath).Lines
                    : [];
                var curLines = File.Exists(full) ? LoadTextFile(full).Lines : [];
                var diff = BuildCompactUnifiedDiff(edit.RelativePath, oldLines, curLines);

                return Task.FromResult(ToolResult.Ok(
                    $"Computed diff for edit '{edit.EditId}' on '{edit.RelativePath}'.",
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
                        diff
                    }));
            });

        yield return new ToolDefinition(
            Name: "revert_edit",
            Category: "editing",
            Description: "Safely reverts a specific recorded editId ONLY if the file has not been modified since that edit (verified by SHA-256).",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Restores pre-edit file bytes (or deletes newly created file) inside the workspace.",
            Parameters:
            [
                new("editId", "string", "Edit identifier returned by create_file, replace_range, apply_patch, or delete_file.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var editId = args.RequireString("editId");
                var edit = db.GetEdit(editId) ?? throw new KeyNotFoundException($"Edit '{editId}' not found.");
                if (edit.Reverted)
                    return Task.FromResult(ToolResult.Fail("already_reverted", $"Edit '{editId}' has already been reverted."));

                var full = guard.ResolvePath(edit.RelativePath, forWrite: true, explicitWorkspace: edit.Workspace);

                if (edit.AfterSha256 == "deleted")
                {
                    if (File.Exists(full))
                        return Task.FromResult(ToolResult.Fail("conflict",
                            $"Cannot safely revert deletion of '{edit.RelativePath}' because a new file now exists at that path."));
                }
                else
                {
                    if (!File.Exists(full))
                        return Task.FromResult(ToolResult.Fail("conflict",
                            $"Cannot safely revert edit '{editId}' because '{edit.RelativePath}' no longer exists."));
                    var currentSha = ComputeFileSha256(full);
                    if (!string.Equals(currentSha, edit.AfterSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(ToolResult.Fail("conflict",
                            $"Unsafe to revert edit '{editId}': '{edit.RelativePath}' has been modified since that edit (expected {edit.AfterSha256}, current {currentSha})."));
                    }
                }

                if (!edit.ExistedBefore)
                {
                    File.Delete(full);
                }
                else
                {
                    if (string.IsNullOrEmpty(edit.BackupFilePath) || !File.Exists(edit.BackupFilePath))
                        return Task.FromResult(ToolResult.Fail("backup_unavailable", $"Backup snapshot for edit '{editId}' is no longer available."));

                    // Never overwrite a current file with an incomplete or modified backup snapshot.
                    var backupSha = ComputeFileSha256(edit.BackupFilePath);
                    if (!string.Equals(backupSha, edit.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(ToolResult.Fail("backup_corrupt", $"Backup snapshot for edit '{editId}' fails the recorded SHA-256 check; nothing has been restored."));

                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    var tmp = full + $".revert.{Guid.NewGuid():N}.tmp";
                    File.Copy(edit.BackupFilePath, tmp, overwrite: true);
                    File.Move(tmp, full, overwrite: true);
                }

                db.MarkEditReverted(editId);
                onFileMutated?.Invoke(full);

                return Task.FromResult(ToolResult.Ok(
                    $"Safely reverted edit '{editId}' on '{edit.RelativePath}'.",
                    new
                    {
                        editId,
                        path = edit.RelativePath,
                        restoredSha256 = edit.ExistedBefore ? edit.BeforeSha256 : "deleted",
                        changedFiles = new[] { edit.RelativePath }
                    }));
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
            var checkLen = Math.Min(bytes.Length, 8192);
            for (var i = bomLen; i < checkLen; i++)
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
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), "utf-8-bom", 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), "utf-16-le", 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), "utf-16-be", 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "utf-8", 0);
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
        File.Copy(sourceFile, backup, overwrite: true);
        return backup;
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
                    {
                        i++;
                        continue;
                    }

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
        if (p.StartsWith("a/", StringComparison.Ordinal) || p.StartsWith("b/", StringComparison.Ordinal))
            p = p[2..];
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
            var targetIdx = h.OldStart == 0 ? 0 : h.OldStart - 1;

            // Support small line-offset fuzz (+/- 3 lines) when exact line shifted slightly but context matches uniquely.
            var matchedIdx = FindMatchingPosition(source, h, targetIdx, cursor);
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

        for (var delta = 1; delta <= 3; delta++)
        {
            if (MatchesAt(source, hunk, preferred - delta, minIdx)) return preferred - delta;
            if (MatchesAt(source, hunk, preferred + delta, minIdx)) return preferred + delta;
        }
        return -1;
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
}
