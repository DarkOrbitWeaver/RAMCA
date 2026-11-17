using System.Text.Json;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class ChatGptTunnelSetupTests
{
    [Theory]
    [InlineData("tunnel_6af24313da748191a6c0dc129be7fcc7", true)]
    [InlineData("tunnel_", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("tunnel_00000000000000000000000000000000; rm -rf", false)]
    public void TunnelIdValidation_RejectsMalformedValues(string id, bool valid)
        => Assert.Equal(valid, ChatGptTunnelSetup.IsValidTunnelId(id));

    [Fact]
    public void GeneratedProfile_SendsLocalHeaderWithoutEmbeddingSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ramca-connection-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PortablePaths(dir);
            paths.EnsureDirectories();
            File.WriteAllText(ChatGptTunnelSetup.SettingsFile(paths),
                JsonSerializer.Serialize(new ChatGptTunnelSetup.Settings("tunnel_6af24313da748191a6c0dc129be7fcc7")));
            File.WriteAllText(ChatGptTunnelSetup.KeyFile(paths), "sk-FAKE-DO-NOT-USE");
            File.WriteAllText(paths.SecretKeyFile, "lib_LOCAL_ONLY_FAKE_SECRET");
            var path = ChatGptTunnelSetup.CreateProfile(paths, "http://127.0.0.1:43170/mcp");
            var yaml = File.ReadAllText(path);
            Assert.Contains("api_key: env:CONTROL_PLANE_API_KEY", yaml);
            Assert.Contains("X-LocalIdeBridge-Key:", yaml);
            Assert.Contains("file:", yaml);
            Assert.DoesNotContain("sk-FAKE-DO-NOT-USE", yaml);
            Assert.DoesNotContain("lib_LOCAL_ONLY_FAKE_SECRET", yaml);
            Assert.Throws<InvalidOperationException>(() =>
                ChatGptTunnelSetup.CreateProfile(paths, "https://arbitrary.example/mcp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
