using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalIdeBridge.Core;

/// <summary>
/// Manages the cryptographically strong access key stored in data/secret.key.
/// Supports constant-time verification, online rotation, masked formatting, and log redaction.
/// </summary>
public sealed partial class CredentialStore
{
    private const string KeyPrefix = "lib_";
    private readonly PortablePaths _paths;
    private readonly object _sync = new();
    private string _currentKey = "";
    private byte[] _currentHash = [];
    private string? _previousKeyForRedaction;
    public DateTimeOffset LastRotatedUtc { get; private set; } = DateTimeOffset.UtcNow;

    [GeneratedRegex(@"lib_[A-Za-z0-9\-_]{16,}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?i)(Authorization\s*:\s*Bearer\s+)[^\s""',;]+", RegexOptions.Compiled)]
    private static partial Regex BearerHeaderPattern();

    [GeneratedRegex(@"(?i)(X-LocalIdeBridge-Key\s*:\s*)[^\s""',;]+", RegexOptions.Compiled)]
    private static partial Regex KeyHeaderPattern();

    public CredentialStore(PortablePaths paths)
    {
        _paths = paths;
    }

    public bool EnsureInitialized()
    {
        lock (_sync)
        {
            if (File.Exists(_paths.SecretKeyFile))
            {
                var existing = File.ReadAllText(_paths.SecretKeyFile, Encoding.UTF8).Trim();
                if (existing.StartsWith(KeyPrefix, StringComparison.Ordinal) && existing.Length >= 36)
                {
                    _currentKey = existing;
                    _currentHash = SHA256.HashData(Encoding.UTF8.GetBytes(existing));
                    LastRotatedUtc = File.GetLastWriteTimeUtc(_paths.SecretKeyFile);
                    return false;
                }
            }

            GenerateAndPersistLocked();
            return true;
        }
    }

    public string Rotate()
    {
        lock (_sync)
        {
            _previousKeyForRedaction = string.IsNullOrEmpty(_currentKey) ? null : _currentKey;
            return GenerateAndPersistLocked();
        }
    }

    private string GenerateAndPersistLocked()
    {
        Span<byte> random = stackalloc byte[32]; // 256 bits of cryptographic entropy
        RandomNumberGenerator.Fill(random);
        var encoded = Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);

        var key = KeyPrefix + encoded;
        Directory.CreateDirectory(_paths.DataDir);
        PortablePaths.AtomicWriteAllText(_paths.SecretKeyFile, key + Environment.NewLine);
        RestrictFilePermissions(_paths.SecretKeyFile);

        _currentKey = key;
        _currentHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        LastRotatedUtc = DateTimeOffset.UtcNow;
        return key;
    }

    public bool Verify(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 256)
            return false;

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim()));
        byte[] expectedHash;
        lock (_sync)
        {
            expectedHash = _currentHash;
        }

        if (expectedHash.Length != candidateHash.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
    }

    public string GetSecretForExplicitCopy()
    {
        lock (_sync)
        {
            return _currentKey;
        }
    }

    public string GetMasked()
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(_currentKey) || _currentKey.Length < 12)
                return "***************";
            return $"{KeyPrefix}***************{_currentKey[^4..]}";
        }
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        string current;
        string? previous;
        lock (_sync)
        {
            current = _currentKey;
            previous = _previousKeyForRedaction;
        }

        var result = input;
        if (!string.IsNullOrEmpty(current) && result.Contains(current, StringComparison.Ordinal))
            result = result.Replace(current, "[REDACTED_KEY]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(previous) && result.Contains(previous, StringComparison.Ordinal))
            result = result.Replace(previous, "[REDACTED_KEY]", StringComparison.Ordinal);

        result = TokenPattern().Replace(result, "[REDACTED_KEY]");
        result = BearerHeaderPattern().Replace(result, "$1[REDACTED]");
        result = KeyHeaderPattern().Replace(result, "$1[REDACTED]");
        return result;
    }

    private static void RestrictFilePermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var user = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "icacls",
                        ArgumentList = { path, "/inheritance:r", "/grant:r", $"{user}:(R,W)" },
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    proc?.WaitForExit(3000);
                }
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Best-effort OS ACL tightening; never crash initialization.
        }
    }
}
