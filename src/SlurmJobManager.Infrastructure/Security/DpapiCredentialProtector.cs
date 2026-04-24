using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.Infrastructure.Security;

/// <summary>
/// Protects credentials using Windows Data Protection API (DPAPI).
/// Data is encrypted with the current user's key and is only readable
/// by the same user on the same machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    // Additional entropy makes the cipher unique to this application.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("SlurmJobManager_v4_DPAPI_Entropy");

    /// <inheritdoc/>
    public string Protect(string plainText)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainText);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    /// <inheritdoc/>
    public string Unprotect(string cipherText)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipherText);
        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Failed to decrypt credential. The data may have been encrypted by a different user or on a different machine.",
                ex);
        }
    }

    /// <summary>
    /// Returns <c>true</c> on the current platform so callers can guard gracefully.
    /// </summary>
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
