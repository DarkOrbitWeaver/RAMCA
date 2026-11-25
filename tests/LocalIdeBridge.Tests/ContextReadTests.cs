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
    public async Task DuplicatePathsCombineRangesAndKnownVersionSkipsSource()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "source.txt");
        await File.WriteAllTextAsync(path, "A\nB\nC\nD\nE\n");
        var first = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["path"] = "source.txt", ["ranges"] = new JsonArray(new JsonArray(1, 3)) },
                new JsonObject { ["path"] = "source.txt", ["ranges"] = new JsonArray(new JsonArray(2, 4)) })
        });
        Assert.True(first.Success, first.ErrorMessage);
        var file = Data(first.Data).GetProperty("files")[0];
        Assert.Equal("A\nB\nC\nD", file.GetProperty("ranges")[0].GetProperty("content").GetString());
        Assert.Equal(1, Data(first.Data).GetProperty("files").GetArrayLength());
        var args = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["path"] = "source.txt", ["ifSha256"] = file.GetProperty("sha256").GetString()
            })
        };
        var unchanged = await sb.Host.Registry.InvokeAsync("read_context", args);
        Assert.True(unchanged.Success, unchanged.ErrorMessage);
        var unchangedFile = Data(unchanged.Data).GetProperty("files")[0];
        Assert.Equal("unchanged", unchangedFile.GetProperty("status").GetString());
        Assert.False(unchangedFile.TryGetProperty("ranges", out _));
        await File.AppendAllTextAsync(path, "F\n");
        var changed = await sb.Host.Registry.InvokeAsync("read_context", args);
        Assert.True(changed.Success, changed.ErrorMessage);
        Assert.Equal("A\nB\nC\nD\nE\nF", Data(changed.Data).GetProperty("files")[0]
            .GetProperty("ranges")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ContextContinuationReconstructsSourceWithoutDuplicatingCharacters()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var source = "header\n" + new string('x', 7000) + "\nfooter";
        var path = Path.Combine(sb.WorkspaceDir, "long.txt");
        await File.WriteAllTextAsync(path, source);
        var assembled = new System.Text.StringBuilder();
        JsonArray? next = null;
        string? version = null;
        for (var page = 0; page < 20; page++)
        {
            var item = new JsonObject { ["path"] = "long.txt" };
            if (next is not null) item["ranges"] = new JsonArray(next);
            if (version is not null) item["expectedSha256"] = version;
            var result = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
            {
                ["items"] = new JsonArray(item), ["maxBytes"] = 2048
            });
            Assert.True(result.Success, result.ErrorMessage);
            var data = Data(result.Data);
            var file = data.GetProperty("files")[0];
            version ??= file.GetProperty("sha256").GetString();
            var range = file.GetProperty("ranges")[0];
            var content = range.GetProperty("content").GetString()!;
            Assert.NotEmpty(content);
            assembled.Append(content);
            if (!range.GetProperty("truncated").GetBoolean()) break;
            next = JsonNode.Parse(range.GetProperty("nextRange").GetRawText())!.AsArray();
        }
        Assert.Equal(source, assembled.ToString());
        await File.AppendAllTextAsync(path, "changed");
        var stale = await sb.Host.Registry.InvokeAsync("read_context", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["path"] = "long.txt", ["expectedSha256"] = version
            })
        });
        Assert.Equal("stale_file", Data(stale.Data).GetProperty("files")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task RangeReadRejectsOversizedLinesAndRetainsExactLineNumbers()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "range.txt"),
            "first\r\n" + new string('x', 100_000) + "\rthird\n");
        var tooLarge = await sb.Host.Registry.InvokeAsync("read_range", new JsonObject
        {
            ["path"] = "range.txt", ["startLine"] = 2, ["endLine"] = 2, ["maxChars"] = 1024
        });
        Assert.Equal("range_too_large", tooLarge.ErrorCode);
        var small = await sb.Host.Registry.InvokeAsync("read_range", new JsonObject
        {
            ["path"] = "range.txt", ["startLine"] = 3, ["endLine"] = 3, ["format"] = "formatted"
        });
        Assert.True(small.Success, small.ErrorMessage);
        var data = Data(small.Data);
        Assert.Equal(3, data.GetProperty("totalLines").GetInt32());
        Assert.Equal("    3: third", data.GetProperty("formatted").GetString());
        Assert.False(data.TryGetProperty("lines", out _));
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
