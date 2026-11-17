using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LocalIdeBridge.Tools;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class RecoveryDisplacementTests
{
    private static void Restore(string backup, string target, string before, string after)
    {
        var method = typeof(EditingTools).GetMethod("RestoreTrackedBackupSafely",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [backup, target, Sha(before), Sha(after)]);
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task KnownAgentPostImageRestoresOriginalAndCleansDisplacedBytes()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "target.txt");
        var backup = Path.Combine(sb.WorkspaceDir, "backup.txt");
        await File.WriteAllTextAsync(path, "agent\n");
        await File.WriteAllTextAsync(backup, "before\n");
        Restore(backup, path, "before\n", "agent\n");
        Assert.Equal("before\n", await File.ReadAllTextAsync(path));
        Assert.Equal("before\n", await File.ReadAllTextAsync(backup));
        Assert.Empty(Directory.GetFiles(sb.WorkspaceDir, ".ramca-recovery-*.bak"));
    }

    [Fact]
    public async Task LateExternalSaveIsPreservedAndNeverSilentlyOverwritten()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "target.txt");
        var backup = Path.Combine(sb.WorkspaceDir, "backup.txt");
        await File.WriteAllTextAsync(path, "external-save\n");
        await File.WriteAllTextAsync(backup, "original\n");
        var error = Assert.Throws<TargetInvocationException>(() =>
            Restore(backup, path, "original\n", "agent\n"));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("Recovery conflict", error.InnerException!.Message);
        Assert.Equal("external-save\n", await File.ReadAllTextAsync(path));
        Assert.Equal("original\n", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task MissingTargetCanBeRestoredWithoutOverwritingUnrelatedFile()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.WorkspaceDir, "missing.txt");
        var backup = Path.Combine(sb.WorkspaceDir, "backup.txt");
        await File.WriteAllTextAsync(backup, "original\n");
        Restore(backup, path, "original\n", "deleted");
        Assert.Equal("original\n", await File.ReadAllTextAsync(path));
    }
}
