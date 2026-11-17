using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalIdeBridge.Cli;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class SupervisorTuiPresentationTests
{
    private static object? Call(SupervisorTui? tui, string name, params object?[] args)
    {
        var flags = BindingFlags.NonPublic | (tui is null ? BindingFlags.Static : BindingFlags.Instance);
        var method = typeof(SupervisorTui).GetMethod(name, flags)
            ?? throw new MissingMethodException(name);
        return method.Invoke(tui, args);
    }

    private static string[] Lines(object? result)
    {
        Assert.NotNull(result);
        var lines = new List<string>();
        foreach (var row in (IEnumerable)result)
            lines.Add((string)row!.GetType().GetProperty("Text")!.GetValue(row)!);
        return lines.ToArray();
    }

    [Theory]
    [InlineData(2L, "0.002s")]
    [InlineData(261L, "0.261s")]
    [InlineData(3_416L, "3.4s")]
    [InlineData(65_000L, "1m 5s")]
    [InlineData(3_700_000L, "1h 1m 40s")]
    public void DurationsUseSecondsMinutesHours(long milliseconds, string expected)
        => Assert.Equal(expected, Call(null, "FormatDuration", milliseconds));

    [Fact]
    public void TerminalLineClipping_PreservesColorsButRejectsCursorCommandsAndControls()
    {
        var input = "\x1b[31mred\x1b[0m text\x1b[2Jhidden\u0007";
        var clipped = (string)Call(null, "ClipAnsi", input, 9)!;
        Assert.Contains("\x1b[31m", clipped);
        Assert.DoesNotContain("\x1b[2J", clipped);
        Assert.DoesNotContain('\u0007', clipped);
        var plain = Regex.Replace(clipped, "\x1b\\[[0-9;]*m", "");
        Assert.True(plain.Length <= 9, plain);
    }

    [Fact]
    public async Task ColoredDiffPreview_VerifiesCurrentFileAndRefusesStalePreview()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var result = await sb.Host.Registry.InvokeAsync("create_file", new JsonObject
        {
            ["path"] = "sample.cs", ["content"] = "first\n"
        });
        Assert.True(result.Success, result.ErrorMessage);
        var editId = JsonSerializer.SerializeToElement(result.Data)
            .GetProperty("editId").GetString()!;
        var edit = sb.Host.Database.GetEdit(editId)!;
        var tui = new SupervisorTui(sb.Host);
        var preview = Lines(Call(tui, "GetVerifiedDiff", edit));
        Assert.Contains(preview, line => line.Contains("+first", StringComparison.Ordinal));

        var file = Path.Combine(sb.WorkspaceDir, "sample.cs");
        await File.WriteAllTextAsync(file, "human changed\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
        var updated = Lines(Call(tui, "GetVerifiedDiff", edit));
        Assert.Contains(updated, line => line.Contains("File changed after this edit", StringComparison.Ordinal));
        Assert.DoesNotContain(updated, line => line.Contains("+first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveOperationPreview_AssociatesOnlyTheMatchingRecordedCallAndRedactsKeys()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "code.txt"), "old\n");
        var key = sb.Host.Credentials.GetSecretForExplicitCopy();
        var result = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "code.txt", ["startLine"] = 1, ["endLine"] = 1,
            ["newText"] = "new " + key
        });
        Assert.True(result.Success, result.ErrorMessage);
        var call = sb.Host.Database.ListToolCalls(sb.Host.Database.ActiveSessionId, 1).Single();
        var tui = new SupervisorTui(sb.Host);
        Call(tui, "CaptureToolResult", "replace_range", result, call.DurationMs);
        var captured = Call(tui, "CapturedFor", call.Id);
        Assert.NotNull(captured);
        var diff = captured!.GetType().GetProperty("Diff")!.GetValue(captured) as string;
        Assert.NotNull(diff);
        Assert.DoesNotContain(key, diff);
        Assert.Contains("[REDACTED_KEY]", diff);
    }

    [Fact]
    public async Task ViewportNavigation_StopsAtLastAvailableActivityRow()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var tui = new SupervisorTui(sb.Host);
        await sb.Host.Registry.InvokeAsync("workspace_list", new JsonObject());
        Call(tui, "MoveSelection", 10);
        var field = typeof(SupervisorTui).GetField("_scrollOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(0, (int)field.GetValue(tui)!);
        await sb.Host.Registry.InvokeAsync("workspace_list", new JsonObject());
        Call(tui, "MoveSelection", 10);
        Assert.Equal(1, (int)field.GetValue(tui)!);
    }
}
