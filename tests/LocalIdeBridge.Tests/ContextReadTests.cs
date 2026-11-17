using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class ContextReadTests
{
    private static JsonElement Data(object? value) =>
        JsonSerializer.SerializeToElement(value, AppConfig.JsonOptions);

    [Fact]
    public async Task BatchReadsMultipleFilesAndRangesInOneCall()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.cs"), "A\nB\nC\nD\n");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "b.cs"), "one\ntwo\n");
        var response = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["path"] = "a.cs",
                    ["ranges"] = new JsonArray(new JsonArray(2, 3), new JsonArray(4, 4)) },
                new JsonObject { ["path"] = "b.cs",
                    ["ranges"] = new JsonArray(new JsonArray(1, 2)) })
        });
        Assert.True(response.Success, response.ErrorMessage);
        var files = Data(response.Data).GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal("B\nC", files[0].GetProperty("ranges")[0].GetProperty("content").GetString());
        Assert.Equal("D", files[0].GetProperty("ranges")[1].GetProperty("content").GetString());
        Assert.Equal("one\ntwo", files[1].GetProperty("ranges")[0].GetProperty("content").GetString());
        Assert.Equal(64, files[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task BatchReadsReportIndependentErrorsAndEnforceSerializedByteBudget()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "large.txt"), new string('x', 150_000));
        var response = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["path"] = "missing.txt" },
                new JsonObject { ["path"] = "large.txt" }),
            ["maxBytes"] = 2048
        });
        Assert.True(response.Success, response.ErrorMessage);
        var data = Data(response.Data);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response.Data, AppConfig.JsonOptions).Length <= 2048);
        Assert.Equal("not_found", data.GetProperty("files")[0].GetProperty("status").GetString());
        Assert.False(data.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task NumberedSingleLineReadNeverExceedsRequestedCap()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "one.txt"), new string('x', 8192));
        var read = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject
        {
            ["path"] = "one.txt", ["maxChars"] = 256, ["includeLineNumbers"] = true
        });
        Assert.True(read.Success, read.ErrorMessage);
        var data = Data(read.Data);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("content").GetString()!.Length <= 256);
    }

    [Fact]
    public async Task RejectsTooManyRangesBeforeAnyRead()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var ranges = new JsonArray();
        for (var i = 0; i < 65; i++) ranges.Add(new JsonArray(1, 2));
        var result = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["path"] = "a.cs", ["ranges"] = ranges })
        });
        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task DeniedExternalPathIsNotDisclosed()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var r = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["path"] = sb.OutsideDir + "\\private.txt" })
        });
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("denied", Data(r.Data).GetProperty("files")[0].GetProperty("status").GetString());
        Assert.DoesNotContain(sb.OutsideDir, JsonSerializer.Serialize(r.Data));
    }
}
