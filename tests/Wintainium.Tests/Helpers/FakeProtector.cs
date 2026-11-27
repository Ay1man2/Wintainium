using Wintainium.Core.Token;

namespace Wintainium.Tests.Helpers;

/// <summary>A reversible stand-in for DPAPI, so token files can be tested on any OS.</summary>
public sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => [0xD9, .. data.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length == 0 || data[0] != 0xD9)
        {
            throw new System.Security.Cryptography.CryptographicException("Key not valid for use in specified state.");
        }

        return [.. data.Skip(1).Select(b => (byte)(b ^ 0x5A))];
    }
}
