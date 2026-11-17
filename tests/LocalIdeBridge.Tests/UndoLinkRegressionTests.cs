using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class UndoLinkRegressionTests
{
    [Fact]
    public async Task UndoAndUndoOfUndoKeepAtomicInverseHistory()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "item.txt");
        await File.WriteAllTextAsync(path, "initial\n");
        var edit = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "item.txt", ["startLine"] = 1, ["endLine"] = 1, ["newText"] = "first"
        });
        Assert.True(edit.Success, edit.ErrorMessage);
        var editId = JsonSerializer.SerializeToElement(edit.Data, AppConfig.JsonOptions).GetProperty("editId").GetString()!;
        var undo = await sb.Host.Registry.InvokeAsync("revert_edit",
            new JsonObject { ["editId"] = editId });
        Assert.True(undo.Success, undo.ErrorMessage);
        var inverseId = JsonSerializer.SerializeToElement(undo.Data, AppConfig.JsonOptions).GetProperty("undoEditId").GetString()!;
        Assert.True(sb.Host.Database.GetEdit(editId)!.Reverted);
        Assert.False(sb.Host.Database.GetEdit(inverseId)!.Reverted);
        Assert.Equal("initial\n", await File.ReadAllTextAsync(path));

        var undoUndo = await sb.Host.Registry.InvokeAsync("revert_edit",
            new JsonObject { ["editId"] = inverseId });
        Assert.True(undoUndo.Success, undoUndo.ErrorMessage);
        Assert.True(sb.Host.Database.GetEdit(inverseId)!.Reverted);
        Assert.Equal("first\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnknownOriginalRollsBackInverseRowAndDoesNotMutateJournal()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var rec = new EditJournalRecord("synthetic-inverse", sb.Host.Database.ActiveSessionId,
            sb.WorkspaceDir, "no-file.txt", "revert_edit", "before", "after",
            false, null, 0, 0, false, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() =>
            sb.Host.Database.RecordEditAndRevert(rec, "nonexistent-original"));
        Assert.Null(sb.Host.Database.GetEdit(rec.EditId));
    }
}
