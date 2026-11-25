using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class EditingAndGitTests
{
    [Fact]
    public async Task GitStatusPagesChangesAndSkipsKnownStatus()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        Assert.True((await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject
        { ["action"] = "init", ["name"] = "main" })).Success);
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, $"file{i}.txt"), "new");
        var first = await sb.Host.Registry.InvokeAsync("git_status", new JsonObject
        { ["recentCommits"] = 0, ["maxFiles"] = 2 });
        Assert.True(first.Success, first.ErrorMessage);
        var data = System.Text.Json.JsonSerializer.SerializeToElement(first.Data);
        Assert.Equal(5, data.GetProperty("changedCount").GetInt32());
        Assert.Equal(2, data.GetProperty("files").GetArrayLength());
        var next = await sb.Host.Registry.InvokeAsync("git_status", new JsonObject
        { ["recentCommits"] = 0, ["offset"] = 2, ["maxFiles"] = 2 });
        Assert.True(next.Success, next.ErrorMessage);
        Assert.Equal(2, System.Text.Json.JsonSerializer.SerializeToElement(next.Data).GetProperty("files").GetArrayLength());
        var unchanged = await sb.Host.Registry.InvokeAsync("git_status", new JsonObject
        { ["recentCommits"] = 0, ["ifVersion"] = data.GetProperty("statusVersion").GetString() });
        var same = System.Text.Json.JsonSerializer.SerializeToElement(unchanged.Data);
        Assert.True(same.GetProperty("unchanged").GetBoolean());
        Assert.Equal(0, same.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Editing_PreservesCrlfAndUtf8Bom_DetectsConcurrentEdits_AndSafelyReverts()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var filePath = Path.Combine(sb.WorkspaceDir, "Service.cs");
        var initialContent = "namespace Demo;\r\npublic class Service\r\n{\r\n    public int Value() => 1;\r\n}\r\n";
        await File.WriteAllTextAsync(filePath, initialContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Read file and inspect encoding, CRLF, and SHA-256.
        var readRes = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "Service.cs" });
        Assert.True(readRes.Success);
        var readDoc = JsonSerializer.SerializeToElement(readRes.Data);
        var sha1 = readDoc.GetProperty("sha256").GetString()!;
        Assert.Equal("utf-8-bom", readDoc.GetProperty("encoding").GetString());
        Assert.Equal("CRLF", readDoc.GetProperty("lineEnding").GetString());

        // Preview range edit without mutating disk.
        var preview = await sb.Host.Registry.InvokeAsync("preview_edit", new JsonObject
        {
            ["path"] = "Service.cs",
            ["startLine"] = 4,
            ["endLine"] = 4,
            ["newText"] = "    public int Value() => 42;"
        });
        Assert.True(preview.Success);
        Assert.Equal(initialContent, await File.ReadAllTextAsync(filePath));

        // Apply range edit with matching SHA-256.
        var editRes = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "Service.cs",
            ["startLine"] = 4,
            ["endLine"] = 4,
            ["newText"] = "    public int Value() => 42;",
            ["expectedSha256"] = sha1
        });
        Assert.True(editRes.Success);
        var editId = JsonSerializer.SerializeToElement(editRes.Data).GetProperty("editId").GetString()!;

        // Verify UTF-8 BOM + CRLF preserved and no leftover temp files exist.
        var rawBytes = await File.ReadAllBytesAsync(filePath);
        Assert.Equal(0xEF, rawBytes[0]);
        Assert.Equal(0xBB, rawBytes[1]);
        Assert.Equal(0xBF, rawBytes[2]);
        var updatedText = await File.ReadAllTextAsync(filePath);
        Assert.Contains("Value() => 42;\r\n", updatedText);
        Assert.Empty(Directory.GetFiles(sb.WorkspaceDir, "*.tmp"));

        // Stale SHA-256 precondition must fail with stale_file.
        var stale = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "Service.cs",
            ["startLine"] = 4,
            ["endLine"] = 4,
            ["newText"] = "    public int Value() => 99;",
            ["expectedSha256"] = sha1
        });
        Assert.False(stale.Success);
        Assert.Equal("stale_file", stale.ErrorCode);

        // Inspect diff for editId.
        var diffRes = await sb.Host.Registry.InvokeAsync("inspect_diff", new JsonObject { ["editId"] = editId });
        Assert.True(diffRes.Success);
        var diffStr = JsonSerializer.SerializeToElement(diffRes.Data, LocalIdeBridge.Core.AppConfig.JsonOptions).GetProperty("diff").GetString()!;
        Assert.Contains("+    public int Value() => 42;", diffStr);

        // Safe revert restores exact original content including BOM & CRLF.
        var revRes = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject { ["editId"] = editId });
        Assert.True(revRes.Success);
        Assert.Equal(initialContent, await File.ReadAllTextAsync(filePath));

        // Verify unsafe revert rejection when file changed after an edit.
        var edit2 = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "Service.cs",
            ["startLine"] = 4,
            ["endLine"] = 4,
            ["newText"] = "    public int Value() => 7;"
        });
        var edit2Id = JsonSerializer.SerializeToElement(edit2.Data).GetProperty("editId").GetString()!;
        await File.AppendAllTextAsync(filePath, "// concurrent human edit\r\n");

        var unsafeRev = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject { ["editId"] = edit2Id });
        Assert.False(unsafeRev.Success);
        Assert.Equal("conflict", unsafeRev.ErrorCode);
    }

    [Fact]
    public async Task ApplyPatch_AtomicallyAppliesValidMultiFilePatch_AndRejectsFailedPreconditionsWithoutPartialWrites()
    {
        await using var sb = await TestSandbox.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt"), "alpha\nbeta\ngamma\n");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "b.txt"), "one\ntwo\nthree\n");

        // Multi-file patch where second file has mismatched context -> NEITHER file may be modified!
        var badMultiPatch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             alpha
            -beta
            +BETA
             gamma
            --- a/b.txt
            +++ b/b.txt
            @@ -1,3 +1,3 @@
             one
            -NON_EXISTENT_LINE
            +TWO
             three
            """;
        var failed = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = badMultiPatch });
        Assert.False(failed.Success);
        Assert.Equal("precondition_failed", failed.ErrorCode);
        Assert.Equal("alpha\nbeta\ngamma\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt")));
        Assert.Equal("one\ntwo\nthree\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "b.txt")));

        // Valid multi-file patch modifying a.txt and creating c.txt.
        var goodPatch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,4 @@
             alpha
            -beta
            +BETA
            +BETA_2
             gamma
            --- /dev/null
            +++ b/c.txt
            @@ -0,0 +1,2 @@
            +created line 1
            +created line 2
            """;
        var ok = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = goodPatch });
        Assert.True(ok.Success);
        Assert.Equal("alpha\nBETA\nBETA_2\ngamma\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt")));
        Assert.Equal(string.Join(Environment.NewLine, "created line 1", "created line 2", ""), await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "c.txt")));

        // Rename and reversible delete.
        var ren = await sb.Host.Registry.InvokeAsync("rename_file",
            new JsonObject { ["sourcePath"] = "c.txt", ["destinationPath"] = "sub/moved.txt" });
        Assert.True(ren.Success);
        Assert.True(File.Exists(Path.Combine(sb.WorkspaceDir, "sub", "moved.txt")));

        var del = await sb.Host.Registry.InvokeAsync("delete_file", new JsonObject { ["path"] = "sub/moved.txt" });
        Assert.True(del.Success);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "sub", "moved.txt")));
        var delEditId = JsonSerializer.SerializeToElement(del.Data).GetProperty("editId").GetString()!;

        var restore = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject { ["editId"] = delEditId });
        Assert.True(restore.Success);
        Assert.True(File.Exists(Path.Combine(sb.WorkspaceDir, "sub", "moved.txt")));
    }

    [Fact]
    public async Task RenameCreatesPairedUndoRecordsAndPreservesBinaryBytes()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var source = Path.Combine(sb.WorkspaceDir, "original.bin");
        var destination = Path.Combine(sb.WorkspaceDir, "sub", "renamed.bin");
        var bytes = new byte[1024 * 1024 + 111];
        new Random(19).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);

        var move = await sb.Host.Registry.InvokeAsync("rename_file", new JsonObject
        {
            ["sourcePath"] = "original.bin",
            ["destinationPath"] = "sub/renamed.bin"
        });
        Assert.True(move.Success, move.Summary);
        Assert.False(File.Exists(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));

        var response = JsonSerializer.SerializeToElement(move.Data);
        var destinationEditId = response.GetProperty("destinationEditId").GetString()!;
        var sourceEditId = response.GetProperty("sourceEditId").GetString()!;

        var undoDest = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject
        {
            ["editId"] = destinationEditId
        });
        Assert.True(undoDest.Success, undoDest.Summary);
        Assert.False(File.Exists(destination));

        var undoSource = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject
        {
            ["editId"] = sourceEditId
        });
        Assert.True(undoSource.Success, undoSource.Summary);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task RevertRenameRestoresBothPathsInOneRecordedAction()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var oldPath = Path.Combine(sb.WorkspaceDir, "before.txt");
        var newPath = Path.Combine(sb.WorkspaceDir, "after.txt");
        await File.WriteAllTextAsync(oldPath, "preserve the exact file");

        var moved = await sb.Host.Registry.InvokeAsync("rename_file", new JsonObject
        {
            ["sourcePath"] = "before.txt",
            ["destinationPath"] = "after.txt"
        });
        Assert.True(moved.Success, moved.Summary);
        var ids = JsonSerializer.SerializeToElement(moved.Data);
        var sourceEditId = ids.GetProperty("sourceEditId").GetString()!;
        var destinationEditId = ids.GetProperty("destinationEditId").GetString()!;

        var undone = await sb.Host.Registry.InvokeAsync("revert_rename", new JsonObject
        {
            ["sourceEditId"] = sourceEditId,
            ["destinationEditId"] = destinationEditId
        });
        Assert.True(undone.Success, undone.Summary);
        Assert.Equal("preserve the exact file", await File.ReadAllTextAsync(oldPath));
        Assert.False(File.Exists(newPath));
        Assert.True(sb.Host.Database.GetEdit(sourceEditId)!.Reverted);
        Assert.True(sb.Host.Database.GetEdit(destinationEditId)!.Reverted);

        var repeat = await sb.Host.Registry.InvokeAsync("revert_rename", new JsonObject
        {
            ["sourceEditId"] = sourceEditId,
            ["destinationEditId"] = destinationEditId
        });
        Assert.False(repeat.Success);
        Assert.Equal("invalid_rename_pair", repeat.ErrorCode);
    }

    [Fact]
    public async Task RenameNeverOverwritesAnExistingDestination()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var source = Path.Combine(sb.WorkspaceDir, "source.txt");
        var destination = Path.Combine(sb.WorkspaceDir, "destination.txt");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(destination, "human work");

        var result = await sb.Host.Registry.InvokeAsync("rename_file", new JsonObject
        {
            ["sourcePath"] = "source.txt",
            ["destinationPath"] = "destination.txt",
            ["overwrite"] = true
        });
        Assert.False(result.Success);
        Assert.Equal("unsafe_overwrite_blocked", result.ErrorCode);
        Assert.Equal("source", await File.ReadAllTextAsync(source));
        Assert.Equal("human work", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task DeletingFileLargerThanTextEditLimit_PreservesRestorableBackup()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var full = Path.Combine(sb.WorkspaceDir, "large-original.bin");
        var bytes = new byte[9 * 1024 * 1024];
        bytes[0] = 42;
        bytes[^1] = 99;
        await File.WriteAllBytesAsync(full, bytes);

        var deletion = await sb.Host.Registry.InvokeAsync("delete_file",
            new JsonObject { ["path"] = "large-original.bin" });
        Assert.True(deletion.Success);
        Assert.False(File.Exists(full));
        var deleteData = JsonSerializer.SerializeToElement(deletion.Data);
        Assert.True(deleteData.GetProperty("reversible").GetBoolean());

        var editId = deleteData.GetProperty("editId").GetString()!;
        var restored = await sb.Host.Registry.InvokeAsync("revert_edit",
            new JsonObject { ["editId"] = editId });
        Assert.True(restored.Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(full));
    }

    [Fact]
    public async Task RevertRejectsTamperedBackup_WithoutOverwritingCurrentFile()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var full = Path.Combine(sb.WorkspaceDir, "important.txt");
        await File.WriteAllTextAsync(full, "the original data\n");

        var editResult = await sb.Host.Registry.InvokeAsync("create_file", new JsonObject
        {
            ["path"] = "important.txt",
            ["content"] = "the newer data\n",
            ["overwrite"] = true
        });
        Assert.True(editResult.Success);
        var editId = JsonSerializer.SerializeToElement(editResult.Data).GetProperty("editId").GetString()!;
        var entry = sb.Host.Database.GetEdit(editId)!;
        Assert.NotNull(entry.BackupFilePath);
        await File.WriteAllTextAsync(entry.BackupFilePath!, "corrupt backup\n");

        var revertResult = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject
        {
            ["editId"] = editId
        });
        Assert.False(revertResult.Success);
        Assert.Equal("backup_corrupt", revertResult.ErrorCode);
        Assert.Equal("the newer data\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task GitOperations_InitBranchStageCommitDiffCheckpointWorktree_AndBlocksPushByDefault()
    {
        await using var sb = await TestSandbox.CreateAsync(cfg =>
        {
            // Explicit opt-in for this legacy branch/worktree functionality test.
            cfg.Autonomy.AllowGitBranchChanges = true;
            cfg.Autonomy.AllowGitWorktrees = true;
        });

        var init = await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject { ["action"] = "init", ["name"] = "main" });
        Assert.True(init.Success);

        await sb.Host.Registry.InvokeAsync("create_file", new JsonObject { ["path"] = "README.md", ["content"] = "# Demo\n" });
        var stage = await sb.Host.Registry.InvokeAsync("git_stage", new JsonObject { ["paths"] = new JsonArray("README.md") });
        Assert.True(stage.Success);

        var commit1 = await sb.Host.Registry.InvokeAsync("git_commit", new JsonObject
        {
            ["message"] = "Initial commit",
            ["authorName"] = "Test User",
            ["authorEmail"] = "test@example.com",
            ["allowPreviouslyStaged"] = true
        });
        Assert.True(commit1.Success);
        var sha1 = JsonSerializer.SerializeToElement(commit1.Data).GetProperty("commitSha").GetString()!;
        Assert.Equal(40, sha1.Length);

        // Create and switch feature branch.
        var sw = await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject { ["action"] = "create_and_switch", ["name"] = "feat/update" });
        Assert.True(sw.Success);

        await sb.Host.Registry.InvokeAsync("create_file",
            new JsonObject { ["path"] = "README.md", ["content"] = "# Demo\nUpdated line\n", ["overwrite"] = true });

        var gdiff = await sb.Host.Registry.InvokeAsync("git_diff", new JsonObject());
        Assert.True(gdiff.Success);
        var gdiffStr = JsonSerializer.SerializeToElement(gdiff.Data, LocalIdeBridge.Core.AppConfig.JsonOptions).GetProperty("diff").GetString()!;
        Assert.Contains("+Updated line", gdiffStr);

        var commit2 = await sb.Host.Registry.InvokeAsync("git_commit",
            new JsonObject { ["message"] = "Update readme", ["stageAll"] = true });
        Assert.True(commit2.Success);

        // Save named checkpoint.
        var cp = await sb.Host.Registry.InvokeAsync("git_checkpoint", new JsonObject { ["label"] = "after-readme" });
        Assert.True(cp.Success);

        // Create, list, and remove a sandboxed Git worktree.
        var wtAdd = await sb.Host.Registry.InvokeAsync("git_worktree",
            new JsonObject { ["action"] = "add", ["name"] = "wt_exp", ["branch"] = "exp/worktree" });
        Assert.True(wtAdd.Success);
        var wtRemove = await sb.Host.Registry.InvokeAsync("git_worktree",
            new JsonObject { ["action"] = "remove", ["name"] = "wt_exp" });
        Assert.True(wtRemove.Success);

        // External git push must be denied by default policy.
        var push = await sb.Host.Registry.InvokeAsync("git_push", new JsonObject());
        Assert.False(push.Success);
        Assert.Equal("authorization_required", push.ErrorCode);

        var status = await sb.Host.Registry.InvokeAsync("git_status", new JsonObject { ["recentCommits"] = 5 });
        Assert.True(status.Success);
        Assert.Contains("feat/update", JsonSerializer.Serialize(status.Data));
    }
}
