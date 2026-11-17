using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class ProjectContextTests
{
    [Fact]
    public async Task OneCallOrientationIncludesSolutionAndTruthfulGitState()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "App.sln"), "solution\n");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var init = await sb.Host.Registry.InvokeAsync("git_branch",
            new JsonObject { ["action"] = "init" });
        Assert.True(init.Success);
        var result = await sb.Host.Registry.InvokeAsync("project_context", new JsonObject
        {
            ["maxProjects"] = 10,
            ["maxTreeEntries"] = 25
        });
        Assert.True(result.Success, result.ErrorMessage);
        var data = JsonSerializer.SerializeToElement(result.Data, AppConfig.JsonOptions);
        Assert.Equal("main", data.GetProperty("gitBranch").GetString());
        Assert.True(data.GetProperty("dirtyFileCount").GetInt32() >= 2);
        Assert.Contains("App.sln", data.GetProperty("solutions").ToString());
        Assert.Contains("App.csproj", data.GetProperty("projects").ToString());
        Assert.Equal(0, data.GetProperty("errors").GetArrayLength());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result.Data, AppConfig.JsonOptions).Length <= 24000);
    }

    [Fact]
    public async Task OneCallContextIsAvailableWithoutGitRepository()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var r = await sb.Host.Registry.InvokeAsync("project_context", new JsonObject());
        Assert.True(r.Success, r.ErrorMessage);
        var data = JsonSerializer.SerializeToElement(r.Data, AppConfig.JsonOptions);
        Assert.False(data.GetProperty("isGitRepository").GetBoolean());
        Assert.Equal(0, data.GetProperty("errors").GetArrayLength());
    }
}
