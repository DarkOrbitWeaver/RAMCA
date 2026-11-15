using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Tests;

/// <summary>
/// Disposable isolated test environment creating both a fresh portable LocalIdeBridge root
/// and a disposable trusted workspace under the system temp directory.
/// </summary>
public sealed class TestSandbox : IAsyncDisposable
{
    public string BaseDir { get; }
    public string PortableDir { get; }
    public string WorkspaceDir { get; }
    public string OutsideDir { get; }
    public PortablePaths Paths { get; }
    public AppConfig Config { get; }
    public WorkbenchHost Host { get; private set; }

    private TestSandbox(string baseDir, PortablePaths paths, AppConfig config, string wsDir, string outsideDir, WorkbenchHost host)
    {
        BaseDir = baseDir;
        PortableDir = paths.RootDir;
        WorkspaceDir = wsDir;
        OutsideDir = outsideDir;
        Paths = paths;
        Config = config;
        Host = host;
    }

    public static async Task<TestSandbox> CreateAsync(Action<AppConfig>? configure = null, bool startListener = true)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        var portableDir = Path.Combine(baseDir, "LocalIdeBridge");
        var wsDir = Path.Combine(baseDir, "DisposableProject");
        var outsideDir = Path.Combine(baseDir, "PrivateOutsideFolder");

        Directory.CreateDirectory(portableDir);
        Directory.CreateDirectory(wsDir);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "private.txt"), "SENSITIVE_OUTSIDE_DATA");

        var paths = new PortablePaths(portableDir);
        var cfg = new AppConfig { SetupCompleted = true };
        cfg.AddTrustedWorkspace(wsDir, "disposable", setAsDefault: true);
        cfg.Connection.Port = 0; // Bind random free port
        configure?.Invoke(cfg);
        cfg.Save(paths);

        var host = new WorkbenchHost(paths, cfg);
        if (startListener)
            await host.StartNetworkListenerAsync();

        return new TestSandbox(baseDir, paths, cfg, wsDir, outsideDir, host);
    }

    public HttpClient CreateClient(string? overrideKey = null, bool omitAuth = false, bool allowSelfSignedTls = false)
    {
        var handler = new HttpClientHandler();
        if (allowSelfSignedTls)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(Host.BoundLocalUrl + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        if (!omitAuth)
        {
            var key = overrideKey ?? Host.Credentials.GetSecretForExplicitCopy();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        return client;
    }

    public static StringContent JsonBody(object obj)
    {
        var json = obj is JsonNode n
            ? n.ToJsonString()
            : System.Text.Json.JsonSerializer.Serialize(obj, AppConfig.JsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        try { Directory.Delete(BaseDir, recursive: true); } catch { }
    }
}
