using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class SessionChangeProjectionTests
{
    [Fact]
    public async Task RepeatedEditsProduceOneNetLineChangeAndUndoReturnsToZero()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var file = Path.Combine(sb.WorkspaceDir, "source.cs");
        await File.WriteAllTextAsync(file, "A\nB\nC\n");

        async Task<string> Replace(string content)
        {
            var r = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
            {
                ["path"] = "source.cs", ["startLine"] = 2,
                ["endLine"] = 2, ["newText"] = content
            });
            Assert.True(r.Success, r.ErrorMessage);
            return System.Text.Json.JsonSerializer.SerializeToElement(r.Data, AppConfig.JsonOptions)
                .GetProperty("editId").GetString()!;
        }
        var first = await Replace("X");
        var second = await Replace("Y");
        var snapshot = SessionChangeProjection.Compute(
            sb.Paths, sb.Host.Guard, sb.Host.Database, sb.Host.Database.ActiveSessionId);
        Assert.Equal(1, snapshot.ChangedFiles);
        Assert.Equal(1, snapshot.AddedLines);
        Assert.Equal(1, snapshot.RemovedLines);
        Assert.Equal(2, snapshot.RecordedOperations);
        Assert.Equal(0, snapshot.UnknownFiles);

        var undoSecond = await sb.Host.Registry.InvokeAsync("revert_edit",
            new JsonObject { ["editId"] = second });
        Assert.True(undoSecond.Success, undoSecond.ErrorMessage);
        var undoFirst = await sb.Host.Registry.InvokeAsync("revert_edit",
            new JsonObject { ["editId"] = first });
        Assert.True(undoFirst.Success, undoFirst.ErrorMessage);

        var reverted = SessionChangeProjection.Compute(
            sb.Paths, sb.Host.Guard, sb.Host.Database, sb.Host.Database.ActiveSessionId);
        Assert.Equal(0, reverted.ChangedFiles);
        Assert.Equal(0, reverted.AddedLines);
        Assert.Equal(0, reverted.RemovedLines);
        Assert.Equal(4, reverted.RecordedOperations);
    }

    [Fact]
    public async Task UnattributedExternalModificationDoesNotCreateFakeNetLineTotals()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var file = Path.Combine(sb.WorkspaceDir, "external.txt");
        await File.WriteAllTextAsync(file, "before\n");
        var r = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "external.txt", ["startLine"] = 1,
            ["endLine"] = 1, ["newText"] = "agent"
        });
        Assert.True(r.Success, r.ErrorMessage);
        await File.WriteAllTextAsync(file, "human saved afterward\n");
        var result = SessionChangeProjection.Compute(
            sb.Paths, sb.Host.Guard, sb.Host.Database, sb.Host.Database.ActiveSessionId);
        Assert.Equal(0, result.ChangedFiles);
        Assert.Equal(1, result.UnknownFiles);
        Assert.Equal("first_touch_recorded_edits_only", result.Scope);
    }

    [Fact]
    public async Task DurableSummarySurvivesDbReopenAndIsScopedToSession()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var session = sb.Host.Database.ActiveSessionId;
        var summary = SessionChangeProjection.Compute(sb.Paths, sb.Host.Guard,
            sb.Host.Database, session);
        sb.Host.Database.SaveSessionChangeSummary(summary);
        var fetched = sb.Host.Database.GetSessionChangeSummary(session);
        Assert.NotNull(fetched);
        Assert.Equal(session, fetched.SessionId);
        Assert.Null(sb.Host.Database.GetSessionChangeSummary("other-session"));
    }
}
