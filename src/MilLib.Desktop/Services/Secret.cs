using System.Security.Cryptography;
using System.Text;

namespace MilLib.Desktop.Services;

/// <summary>
/// Keeping a small secret — the password to a database server — unreadable in
/// the settings file beside the program, on whatever operating system this is.
///
/// On Windows it is the platform's own account-bound protection (DPAPI), so a
/// file already written by an earlier Windows build still reads. On macOS and
/// Linux, where there is no DPAPI, it is AES-GCM under a key derived from the
/// machine — weaker than DPAPI, but the threat is the same modest one either
/// way: a person who opens the settings file should not find a plain password
/// in it. A value carried between two different machines will not decrypt on
/// the second, which is treated as no password rather than as a crash — the
/// screen simply asks again.
/// </summary>
internal static class Secret
{
    private const string Tag = "AES1:";

    public static string Protect(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        if (OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }

        return Tag + AesProtect(value);
    }

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0)
        {
            return "";
        }

        try
        {
            if (stored.StartsWith(Tag, StringComparison.Ordinal))
            {
                return AesUnprotect(stored[Tag.Length..]);
            }

            if (OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
        }
        catch (Exception)
        {
            // Written by a different account or on a different machine. No
            // password rather than a crash; the Database screen asks again.
        }

        return "";
    }

    /// <summary>A 32-byte key that is the same each time on this machine.</summary>
    private static byte[] Key() =>
        SHA256.HashData(Encoding.UTF8.GetBytes("mil-lib::secret::v1::" + Environment.MachineName));

    private static string AesProtect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var gcm = new AesGcm(Key(), tag.Length);
        gcm.Encrypt(nonce, plain, cipher, tag);

        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private static string AesUnprotect(string stored)
    {
        var all = Convert.FromBase64String(stored);

        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;

        var nonce = all[..nonceLen];
        var tag = all[nonceLen..(nonceLen + tagLen)];
        var cipher = all[(nonceLen + tagLen)..];
        var plain = new byte[cipher.Length];

        using var gcm = new AesGcm(Key(), tagLen);
        gcm.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
