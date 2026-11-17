using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class SecurityAndWorkspaceTests
{
    [Fact]
    public async Task Authentication_AcceptsValidBearerAndHeader_RejectsUnauthorized()
    {
        await using var sb = await TestSandbox.CreateAsync();

        // Health endpoint works without auth and exposes no secrets or paths.
        using var anon = sb.CreateClient(omitAuth: true);
        var health = await anon.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var healthBody = await health.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sb.WorkspaceDir, healthBody);

        // Missing auth rejected on both /api/v1 and /mcp and /openapi.json.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("api/v1/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("mcp", TestSandbox.JsonBody(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);

        // Wrong token rejected.
        using var wrong = sb.CreateClient(overrideKey: "lib_invalid_token_12345678901234567890");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("api/v1/info")).StatusCode);

        // Valid Bearer token succeeds.
        using var authed = sb.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("api/v1/info")).StatusCode);

        // Valid X-LocalIdeBridge-Key header also succeeds.
        using var headerClient = sb.CreateClient(omitAuth: true);
        headerClient.DefaultRequestHeaders.Add("X-LocalIdeBridge-Key", sb.Host.Credentials.GetSecretForExplicitCopy());
        Assert.Equal(HttpStatusCode.OK, (await headerClient.GetAsync("api/v1/info")).StatusCode);
    }

    [Fact]
    public async Task CredentialRotation_InvalidatesPriorKeyImmediately_AndNeverLeaksToLogsOrExports()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var oldKey = sb.Host.Credentials.GetSecretForExplicitCopy();

        using var oldClient = sb.CreateClient(overrideKey: oldKey);
        Assert.Equal(HttpStatusCode.OK, (await oldClient.GetAsync("api/v1/info")).StatusCode);

        // Log a string accidentally containing both keys to test redaction.
        var newKey = sb.Host.Credentials.Rotate();
        Assert.NotEqual(oldKey, newKey);
        sb.Host.Logger.Warn("test", $"Attempt with {oldKey} and {newKey}");

        // Old key must fail immediately.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("api/v1/info")).StatusCode);

        // New key must succeed immediately.
        using var newClient = sb.CreateClient(overrideKey: newKey);
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("api/v1/info")).StatusCode);

        // Verify neither key exists in config.json, exported config, connection instructions, or any log file.
        var configRaw = await File.ReadAllTextAsync(sb.Paths.ConfigFile);
        var exported = sb.Config.ExportShareableJson();
        var instructions = sb.Host.Instructions.BuildAgentInstructions();
        Assert.DoesNotContain(oldKey, configRaw);
        Assert.DoesNotContain(newKey, configRaw);
        Assert.DoesNotContain(newKey, exported);
        Assert.DoesNotContain(newKey, instructions);

        foreach (var logFile in Directory.EnumerateFiles(sb.Paths.LogsDir, "*.log"))
        {
            await using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var logText = await reader.ReadToEndAsync();
            Assert.DoesNotContain(oldKey, logText);
            Assert.DoesNotContain(newKey, logText);
        }
    }

    [Fact]
    public async Task FailedAuthThrottling_OversizedPayload_MalformedJson_AndDisallowedOrigin()
    {
        await using var sb = await TestSandbox.CreateAsync(cfg =>
        {
            cfg.Security.FailedAuthPerMinute = 4;
            cfg.Security.MaxRequestBodyBytes = 64 * 1024; // 64 KB
        });

        // Disallowed Origin blocked against DNS rebinding.
        using var authed = sb.CreateClient();
        var originReq = new HttpRequestMessage(HttpMethod.Get, "api/v1/info");
        originReq.Headers.Add("Origin", "https://evil-attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await authed.SendAsync(originReq)).StatusCode);

        // Malformed JSON rejected cleanly with 400.
        var badApi = await authed.PostAsync("api/v1/tools/read_file/invoke",
            new StringContent("{not valid json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badApi.StatusCode);

        var badMcp = await authed.PostAsync("mcp",
            new StringContent("{broken", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badMcp.StatusCode);

        // Oversized payload rejected with 413 Payload Too Large.
        var giant = new string('A', 90 * 1024);
        var oversized = await authed.PostAsync("api/v1/tools/create_file/invoke",
            TestSandbox.JsonBody(new { path = "big.txt", content = giant }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

        // Failed auth throttling triggers 429 after 4 bad attempts.
        using var bad = sb.CreateClient(overrideKey: "lib_wrong_key_0000000000000000000000");
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("api/v1/info")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await bad.GetAsync("api/v1/info")).StatusCode);
    }

    [Fact]
    public async Task OriginMatchingPublicEndpoint_RequiresExactSchemeAndPort()
    {
        await using var sb = await TestSandbox.CreateAsync();
        sb.Config.Connection.PublicEndpoint = "https://bridge.example:9443/mcp";
        using var authed = sb.CreateClient();

        async Task<HttpStatusCode> RequestWithOrigin(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/info");
            request.Headers.Add("Origin", origin);
            using var response = await authed.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await RequestWithOrigin("http://bridge.example:9443"));
        Assert.Equal(HttpStatusCode.Forbidden, await RequestWithOrigin("https://bridge.example:9444"));
        Assert.Equal(HttpStatusCode.OK, await RequestWithOrigin("https://bridge.example:9443"));
    }

    [Fact]
    public async Task WorkspaceConfinement_BlocksTraversal_ProtectedPaths_AndRepoPolicy()
    {
        await using var sb = await TestSandbox.CreateAsync();

        // Direct traversal via ../
        var r1 = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "../PrivateOutsideFolder/private.txt" });
        Assert.False(r1.Success);
        Assert.Equal("workspace_violation", r1.ErrorCode);

        // Absolute path outside workspace
        var outsideFile = Path.Combine(sb.OutsideDir, "private.txt");
        var r2 = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = outsideFile });
        Assert.False(r2.Success);
        Assert.Equal("workspace_violation", r2.ErrorCode);

        // Attempt to read LocalIdeBridge's own secret.key
        var r3 = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = sb.Paths.SecretKeyFile });
        Assert.False(r3.Success);
        Assert.Equal("workspace_violation", r3.ErrorCode);

        // Attempt to write malicious Git hook inside workspace
        var r4 = await sb.Host.Registry.InvokeAsync("create_file",
            new JsonObject { ["path"] = ".git/hooks/pre-commit", ["content"] = "echo pwned" });
        Assert.False(r4.Success);
        Assert.Equal("workspace_violation", r4.ErrorCode);

        // Refuse trusting LocalIdeBridge internal data/secrets directory
        Assert.Throws<LocalIdeBridge.Core.WorkspaceViolationException>(() =>
            sb.Host.Guard.ValidateTrustableDirectory(sb.Paths.DataDir));

        // Enforce per-repository .localidebridge.json readOnlyPaths and deniedPaths
        File.WriteAllText(Path.Combine(sb.WorkspaceDir, "locked.txt"), "immutable");
        File.WriteAllText(Path.Combine(sb.WorkspaceDir, ".localidebridge.json"),
            """{"readOnlyPaths":["locked.txt"],"deniedPaths":["secrets"]}""");
        Directory.CreateDirectory(Path.Combine(sb.WorkspaceDir, "secrets"));
        File.WriteAllText(Path.Combine(sb.WorkspaceDir, "secrets", "prod.env"), "NOPE");

        var roRead = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "locked.txt" });
        Assert.True(roRead.Success);

        var roWrite = await sb.Host.Registry.InvokeAsync("create_file",
            new JsonObject { ["path"] = "locked.txt", ["content"] = "mutated", ["overwrite"] = true });
        Assert.False(roWrite.Success);
        Assert.Equal("workspace_violation", roWrite.ErrorCode);

        var deniedRead = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "secrets/prod.env" });
        Assert.False(deniedRead.Success);
        Assert.Equal("workspace_violation", deniedRead.ErrorCode);
    }

    [Fact]
    public async Task WorkspaceConfinement_BlocksFileAndDirectorySymlinkAndReparseEscapes()
    {
        await using var sb = await TestSandbox.CreateAsync();

        var dirLink = Path.Combine(sb.WorkspaceDir, "escaped_dir");
        var fileLink = Path.Combine(sb.WorkspaceDir, "escaped_file.txt");
        var outsideFile = Path.Combine(sb.OutsideDir, "private.txt");

        Directory.CreateSymbolicLink(dirLink, sb.OutsideDir);
        File.CreateSymbolicLink(fileLink, outsideFile);

        // Reading file symlink pointing outside workspace is blocked.
        var readFileLink = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "escaped_file.txt" });
        Assert.False(readFileLink.Success);
        Assert.Equal("workspace_violation", readFileLink.ErrorCode);

        // Reading through directory symlink/reparse point is blocked.
        var readThroughDir = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "escaped_dir/private.txt" });
        Assert.False(readThroughDir.Success);
        Assert.Equal("workspace_violation", readThroughDir.ErrorCode);

        // Creating a brand-new file through directory symlink pointing outside is blocked!
        var writeThroughDir = await sb.Host.Registry.InvokeAsync("create_file",
            new JsonObject { ["path"] = "escaped_dir/injected.txt", ["content"] = "bad" });
        Assert.False(writeThroughDir.Success);
        Assert.Equal("workspace_violation", writeThroughDir.ErrorCode);
        Assert.False(File.Exists(Path.Combine(sb.OutsideDir, "injected.txt")));

        // Text search must never leak content from symlinked outside directories/files.
        var search = await sb.Host.Registry.InvokeAsync("text_search", new JsonObject { ["query"] = "SENSITIVE_OUTSIDE_DATA" });
        Assert.True(search.Success);
        var searchEl = System.Text.Json.JsonSerializer.SerializeToElement(search.Data, LocalIdeBridge.Core.AppConfig.JsonOptions);
        Assert.Equal(0, searchEl.GetProperty("count").GetInt32());
        Assert.Empty(searchEl.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task DirectHttpsMode_ServesAuthenticatedTlsAndRejectsInsecureRemoteBind()
    {
        // Non-loopback without HTTPS must throw at startup.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await TestSandbox.CreateAsync(cfg =>
            {
                cfg.Connection.ListenHost = "0.0.0.0";
                cfg.Connection.UseHttps = false;
                cfg.Security.RequireHttpsForRemote = true;
            });
        });

        // HTTPS with auto-generated portable certificate works cleanly over TLS.
        await using var sb = await TestSandbox.CreateAsync(cfg =>
        {
            cfg.Connection.Mode = "direct_https";
            cfg.Connection.ListenHost = "127.0.0.1";
            cfg.Connection.UseHttps = true;
            cfg.Connection.AutoGenerateSelfSignedTls = true;
        });

        Assert.StartsWith("https://", sb.Host.BoundLocalUrl);
        using var tlsClient = sb.CreateClient(allowSelfSignedTls: true);
        var resp = await tlsClient.GetAsync("api/v1/info");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
