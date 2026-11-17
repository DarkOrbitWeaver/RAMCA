using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Tools;
using Xunit;

namespace LocalIdeBridge.Tests;

/// <summary>
/// Non-destructive checks for audit regressions. Every test uses an isolated, disposable
/// workspace; never run destructive recovery experiments against a real project.
/// </summary>
public sealed class AuditRecoveryRegressionTests
{
    [Fact]
    public async Task PatchWithRepeatedFileTarget_IsRejectedBeforeAnyMutation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "a.txt");
        await File.WriteAllTextAsync(path, "old\n");
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            +new
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            +other
            """;
        var result = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = patch });
        Assert.False(result.Success);
        Assert.Equal("old\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PlainDiffInRealADirectory_OnlyModifiesADirectoryFile()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        Directory.CreateDirectory(Path.Combine(sb.WorkspaceDir, "a"));
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a", "config.txt"), "old\n");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "config.txt"), "root-precious\n");
        var patch = """
            --- a/config.txt
            +++ a/config.txt
            @@ -1 +1 @@
            -old
            +new
            """;
        var result = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = patch });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a", "config.txt")));
        Assert.Equal("root-precious\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "config.txt")));
    }

    [Fact]
    public async Task PatchWithIncorrectHunkLength_IsRejectedWithoutMutation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "a.txt");
        await File.WriteAllTextAsync(path, "old\n");
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
            -old
            +new
            """;
        var result = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = patch });
        Assert.False(result.Success);
        Assert.Equal("old\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SuccessfulMultiFilePatch_RecordsVerifiedReversibleSnapshots()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var original = Path.Combine(sb.WorkspaceDir, "first.txt");
        var second = Path.Combine(sb.WorkspaceDir, "second.txt");
        await File.WriteAllTextAsync(original, "old\n");
        var patch = """
            --- a/first.txt
            +++ b/first.txt
            @@ -1 +1 @@
            -old
            +new
            --- /dev/null
            +++ b/second.txt
            @@ -0,0 +1 @@
            +created
            """;
        var result = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = patch });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("new\n", await File.ReadAllTextAsync(original));
        Assert.Equal("created" + Environment.NewLine, await File.ReadAllTextAsync(second));
        Assert.Empty(Directory.GetFiles(Path.Combine(sb.Paths.DataDir, "pending-patches"), "*.json"));
        var edits = JsonSerializer.SerializeToElement(result.Data).GetProperty("edits");
        Assert.Equal(2, edits.GetArrayLength());
        foreach (var edit in edits.EnumerateArray())
        {
            var id = edit.GetProperty("editId").GetString()!;
            Assert.NotNull(sb.Host.Database.GetEdit(id));
        }
    }

    [Fact]
    public async Task InterruptedPatch_RestoresVerifiedOriginalWithoutOverwritingOtherFiles()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var full = Path.Combine(sb.WorkspaceDir, "recovery.txt");
        var before = "before\n";
        var after = "agent-patch\n";
        var shaBefore = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before)));
        var shaAfter = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(after)));
        var backup = Path.Combine(sb.Paths.GetSessionEditsDir(sb.Host.Database.ActiveSessionId), "edit_recovery.bak");
        await File.WriteAllTextAsync(backup, before);
        await File.WriteAllTextAsync(full, after);
        var pending = Path.Combine(sb.Paths.DataDir, "pending-patches");
        Directory.CreateDirectory(pending);
        var manifest = Path.Combine(pending, "patch_recovery.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            id = "patch_recovery", workspace = sb.WorkspaceDir, committed = false,
            items = new[] { new { relativePath = "recovery.txt", editId = "edit_recovery", backupPath = backup, beforeHash = shaBefore, afterHash = shaAfter } }
        }, AppConfig.JsonOptions));
        EditingTools.RecoverPendingPatchTransactions(sb.Paths, sb.Host.Guard, sb.Host.Database);
        Assert.Equal(before, await File.ReadAllTextAsync(full));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task InterruptedPatch_RefusesToOverwriteUnexpectedHumanEdit()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var full = Path.Combine(sb.WorkspaceDir, "human.txt");
        var shaBefore = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("before\n"u8));
        var shaAfter = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("agent-patch\n"u8));
        var backup = Path.Combine(sb.Paths.GetSessionEditsDir(sb.Host.Database.ActiveSessionId), "edit_conflict.bak");
        await File.WriteAllTextAsync(backup, "before\n");
        await File.WriteAllTextAsync(full, "human-work\n");
        var pending = Path.Combine(sb.Paths.DataDir, "pending-patches");
        Directory.CreateDirectory(pending);
        var manifest = Path.Combine(pending, "patch_conflict.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            id = "patch_conflict", workspace = sb.WorkspaceDir, committed = false,
            items = new[] { new { relativePath = "human.txt", editId = "edit_conflict", backupPath = backup, beforeHash = shaBefore, afterHash = shaAfter } }
        }, AppConfig.JsonOptions));
        Assert.Throws<IOException>(() => EditingTools.RecoverPendingPatchTransactions(sb.Paths, sb.Host.Guard, sb.Host.Database));
        Assert.Equal("human-work\n", await File.ReadAllTextAsync(full));
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public async Task CommittedPatchManifest_DoesNotRejectLaterAuthorizedHumanChanges()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var full = Path.Combine(sb.WorkspaceDir, "later.txt");
        await File.WriteAllTextAsync(full, "human-newer\n");
        var pending = Path.Combine(sb.Paths.DataDir, "pending-patches");
        Directory.CreateDirectory(pending);
        var manifest = Path.Combine(pending, "patch_committed.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            id = "patch_committed", workspace = sb.WorkspaceDir, committed = true,
            items = new[] { new { relativePath = "later.txt", editId = "edit_old", backupPath = (string?)null, beforeHash = "none", afterHash = "old-after-hash" } }
        }, AppConfig.JsonOptions));
        EditingTools.RecoverPendingPatchTransactions(sb.Paths, sb.Host.Guard, sb.Host.Database);
        Assert.Equal("human-newer\n", await File.ReadAllTextAsync(full));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task GitInternalMetadata_CannotBeReadOrModifiedThroughGenericFileTools()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var git = Path.Combine(sb.WorkspaceDir, ".git");
        Directory.CreateDirectory(git);
        await File.WriteAllTextAsync(Path.Combine(git, "config"), "precious-git-config");
        var read = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = ".git/config" });
        var write = await sb.Host.Registry.InvokeAsync("create_file", new JsonObject
        {
            ["path"] = ".git/config", ["content"] = "malicious", ["overwrite"] = true
        });
        Assert.False(read.Success);
        Assert.False(write.Success);
        Assert.Equal("precious-git-config", await File.ReadAllTextAsync(Path.Combine(git, "config")));
    }

    [Fact]
    public async Task ToolInvocationPinsItsOriginalWorkspaceEvenAfterSelectionChanges()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var other = Path.Combine(sb.BaseDir, "AnotherWorkspace");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "who.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(other, "who.txt"), "second");
        sb.Host.Config.AddTrustedWorkspace(other, "other");
        sb.Host.Guard.SelectWorkspace(other);

        var context = new ToolExecutionContext(sb.Host.Database.ActiveSessionId, WorkspaceRoot: sb.WorkspaceDir);
        var result = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "who.txt" }, context);
        Assert.True(result.Success, result.ErrorMessage);
        var doc = JsonSerializer.SerializeToElement(result.Data);
        Assert.Contains("first", doc.GetProperty("content").GetString());
        Assert.DoesNotContain("second", doc.GetProperty("content").GetString());
    }

    [Fact]
    public async Task LargeJobResult_SpillsCompleteValidJsonAndCanBePagedWithoutTruncation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var id = "job_large_result_for_test";
        sb.Host.Database.CreateJob(id, sb.Host.Database.ActiveSessionId, sb.WorkspaceDir, "read_file", "{}", true);
        var hugeJson = JsonSerializer.Serialize(new { data = new string('x', 100_000) });
        sb.Host.Database.UpdateJobStatus(id, "succeeded", resultJson: hugeJson);
        var record = sb.Host.Database.GetJob(id)!;
        using var reference = JsonDocument.Parse(record.ResultJson!);
        Assert.True(reference.RootElement.GetProperty("spilled").GetBoolean());
        var reconstructed = new System.Text.StringBuilder();
        for (var offset = 0; offset < hugeJson.Length; offset += 16_000)
            reconstructed.Append(sb.Host.Database.ReadJobResultSlice(id, offset, 16_000).Content);
        Assert.Equal(hugeJson, reconstructed.ToString());
    }

    [Fact]
    public async Task UnrevertedEditJournal_IsNotPrunedWhenRetentionCountIsReached()
    {
        await using var sb = await TestSandbox.CreateAsync(cfg => cfg.Retention.EditJournalMaxEntries = 20, startListener: false);
        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var response = await sb.Host.Registry.InvokeAsync("create_file", new JsonObject
            {
                ["path"] = $"item_{i}.txt", ["content"] = "keep me"
            });
            Assert.True(response.Success, response.ErrorMessage);
            ids.Add(JsonSerializer.SerializeToElement(response.Data).GetProperty("editId").GetString()!);
        }
        foreach (var id in ids)
            Assert.NotNull(sb.Host.Database.GetEdit(id));
    }

    [Fact]
    public void MultiProjectTestReports_AreAggregatedAndMissingCountersFailClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ramca_trx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="3" passed="2" failed="1" executed="3" /></ResultSummary></TestRun>""";
            var b = """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="2" passed="2" failed="0" executed="2" /></ResultSummary></TestRun>""";
            File.WriteAllText(Path.Combine(dir, "a.trx"), a);
            File.WriteAllText(Path.Combine(dir, "b.trx"), b);
            var report = ProcessRunner.ParseTrxDirectory(dir);
            Assert.Equal(2, report.ReportFiles);
            Assert.Equal(5, report.Summary.Total);
            Assert.Equal(4, report.Summary.Passed);
            Assert.Equal(1, report.Summary.Failed);

            File.WriteAllText(Path.Combine(dir, "b.trx"), "<TestRun />");
            Assert.Throws<InvalidDataException>(() => ProcessRunner.ParseTrxDirectory(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ExecuteOnce_DuplicateDoesNotRepeatFileMutation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var invocation = new JsonObject
        {
            ["operationId"] = "exactly_once_created_file",
            ["tool"] = "create_file",
            ["arguments"] = new JsonObject { ["path"] = "once.txt", ["content"] = "first\n" }
        };
        var first = await sb.Host.Registry.InvokeAsync("execute_once", (JsonObject)invocation.DeepClone());
        Assert.True(first.Success, first.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "once.txt"), "later-human-edit\n");
        var repeated = await sb.Host.Registry.InvokeAsync("execute_once", (JsonObject)invocation.DeepClone());
        Assert.True(repeated.Success, repeated.ErrorMessage);
        Assert.Equal("later-human-edit\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "once.txt")));
        var recorded = sb.Host.Database.GetOperation("exactly_once_created_file");
        Assert.NotNull(recorded);
        Assert.Equal("succeeded", recorded!.Status);
    }

    [Fact]
    public async Task ExecuteOnce_ReuseForDifferentPayloadFailsWithoutSideEffects()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var first = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = "one_key_different_args", ["tool"] = "create_file",
            ["arguments"] = new JsonObject { ["path"] = "first.txt", ["content"] = "one" }
        });
        Assert.True(first.Success, first.ErrorMessage);
        var second = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = "one_key_different_args", ["tool"] = "create_file",
            ["arguments"] = new JsonObject { ["path"] = "second.txt", ["content"] = "two" }
        });
        Assert.False(second.Success);
        Assert.Equal("operation_id_conflict", second.ErrorCode);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "second.txt")));
    }

    [Fact]
    public async Task ExecuteOnce_RunningOperationIsNeverReplayed()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var payload = new JsonObject { ["path"] = "must_not_exist.txt", ["content"] = "bad" };
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString())));
        sb.Host.Database.TryReserveOperation("crash_ambiguous", sb.Host.Database.ActiveSessionId,
            sb.WorkspaceDir, "create_file", hash);
        var response = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = "crash_ambiguous", ["tool"] = "create_file", ["arguments"] = payload
        });
        Assert.False(response.Success);
        Assert.Equal("operation_in_progress", response.ErrorCode);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "must_not_exist.txt")));
    }

    [Fact]
    public async Task IrreversibleDeleteFlag_DoesNotPermitUnbackedLargeDeletion()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var big = Path.Combine(sb.WorkspaceDir, "oversized.bin");
        await using (var stream = new FileStream(big, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(256L * 1024 * 1024 + 1);
        var response = await sb.Host.Registry.InvokeAsync("delete_file", new JsonObject
        {
            ["path"] = "oversized.bin", ["allowIrreversible"] = true
        });
        Assert.False(response.Success);
        Assert.True(File.Exists(big));
    }
}
