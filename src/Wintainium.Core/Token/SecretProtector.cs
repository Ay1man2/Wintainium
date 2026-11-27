using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Wintainium.Core.Token;

/// <summary>Encrypts secrets for the current Windows user (DPAPI); a fake in tests.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI, current user, no extra entropy: what PowerShell's ConvertFrom-SecureString uses.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

public static class SecretProtector
{
    /// <summary>DPAPI on Windows; elsewhere a protector that refuses (tokens are only stored on Windows).</summary>
    public static ISecretProtector ForCurrentUser() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new UnavailableProtector();

    private sealed class UnavailableProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => throw new PlatformNotSupportedException("Saving a token needs Windows.");

        public byte[] Unprotect(byte[] data) => throw new PlatformNotSupportedException("Reading a token needs Windows.");
    }
}
