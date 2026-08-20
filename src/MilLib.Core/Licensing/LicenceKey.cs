using System.Security.Cryptography;
using System.Text;

namespace MilLib.Core.Licensing;

/// <summary>What a key turned out to say.</summary>
public record KeyDetails(string Main, string Tag, DateOnly? Expires, bool Perpetual);

/// <summary>
/// Licence keys, in the format this company has been issuing for years:
/// <c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-EEEEEE</c>.
///
/// An exact port of the PHP, and deliberately not an improvement on it. Two
/// programs have to agree about this: the vendor's Licence Manager mints the
/// key, and this re-derives it. A unit may already hold a key issued for the
/// web application on the same machine, and that key has to go on working
/// here — so the algorithm is reproduced down to the alphabet and the modulo.
///
/// What makes it a licence rather than a password is that the expiry is inside
/// the hash as well as on the end of the key. Editing the date on a key
/// changes what the key should have been, so it stops matching.
/// </summary>
public static class LicenceKey
{
    /// <summary>
    /// Thirty-two characters, with 0, 1, I and O left out, so nobody reading a
    /// key off a printed certificate or over a telephone can confuse a zero
    /// for a letter.
    /// </summary>
    public const string Charset = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string Generate(string hardwareId, string salt, DateOnly? expires)
    {
        var tag = EncodeTag(expires);

        var hash = Sha256Hex(hardwareId + salt + tag);

        var key = new StringBuilder(25);

        for (var i = 0; i < 25; i++)
        {
            var b = Convert.ToInt32(hash.Substring(i * 2, 2), 16);

            key.Append(Charset[b % 32]);
        }

        var groups = new string[5];

        for (var i = 0; i < 5; i++)
        {
            groups[i] = key.ToString(i * 5, 5);
        }

        return string.Join('-', groups) + '-' + tag;
    }

    /// <summary>
    /// The original format, with no expiry either in the hash or on the end.
    ///
    /// Nothing new is issued this way. It exists because keys in that format
    /// are still in customers' hands, and a program that only knows today's
    /// format would tell one of those customers their genuine key is invalid.
    /// </summary>
    public static string GenerateLegacy(string hardwareId, string salt)
    {
        var hash = Sha256Hex(hardwareId + salt);

        var key = new StringBuilder(25);

        for (var i = 0; i < 25; i++)
        {
            var b = Convert.ToInt32(hash.Substring(i * 2, 2), 16);

            key.Append(Charset[b % 32]);
        }

        var groups = new string[5];

        for (var i = 0; i < 5; i++)
        {
            groups[i] = key.ToString(i * 5, 5);
        }

        return string.Join('-', groups);
    }

    /// <summary>
    /// Whether this key belongs to this machine, in either format.
    ///
    /// Compared as fixed-length text with no early exit. A comparison that
    /// stops at the first wrong character tells anybody timing it how much of
    /// their guess was right, which over enough attempts is the whole key.
    /// </summary>
    public static bool Verify(string entered, string hardwareId, string salt)
    {
        var normalised = Normalise(entered);

        if (Parse(entered) is { } parsed)
        {
            var expected = Normalise(Generate(hardwareId, salt, parsed.Expires));

            if (FixedTimeEquals(normalised, expected))
            {
                return true;
            }
        }

        return FixedTimeEquals(normalised, Normalise(GenerateLegacy(hardwareId, salt)));
    }

    /// <summary>
    /// What a key says about itself, or null if it is not the right shape.
    ///
    /// Nothing here proves the key is genuine — that is <see cref="Verify"/>.
    /// This only reads the expiry off it, which is needed before the key can
    /// be re-derived at all.
    /// </summary>
    public static KeyDetails? Parse(string key)
    {
        var clean = Normalise(key);

        if (clean.Length != 31)
        {
            return null;
        }

        var tag = clean[25..];

        return new KeyDetails(clean[..25], tag, DecodeTag(tag), tag == "999999");
    }

    /// <summary>Six digits: the expiry as yymmdd, or 999999 for one that never expires.</summary>
    public static string EncodeTag(DateOnly? expires)
    {
        if (expires is null)
        {
            return "999999";
        }

        // From 2099 onward is treated as perpetual, which is what the original
        // chose and what keys already issued depend on.
        return expires.Value.Year >= 2099 ? "999999" : expires.Value.ToString("yyMMdd");
    }

    public static DateOnly? DecodeTag(string tag)
    {
        if (tag == "999999" || tag.Length != 6 || !tag.All(char.IsAsciiDigit))
        {
            return null;
        }

        var yy = int.Parse(tag[..2]);
        var mm = int.Parse(tag[2..4]);
        var dd = int.Parse(tag[4..]);

        // Two digits, so a century has to be assumed. Under 80 is this one,
        // which is the rule the PHP used and the one existing keys were minted
        // against.
        var year = yy < 80 ? 2000 + yy : 1900 + yy;

        try
        {
            return new DateOnly(year, mm, dd);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// A key as it should be written down: five groups and the tag, in capitals.
    /// Whatever spacing or case somebody typed.
    /// </summary>
    public static string Tidy(string key)
    {
        var clean = Normalise(key);

        if (clean.Length != 31)
        {
            return clean;
        }

        var groups = new string[5];

        for (var i = 0; i < 5; i++)
        {
            groups[i] = clean.Substring(i * 5, 5);
        }

        return string.Join('-', groups) + '-' + clean[25..];
    }

    /// <summary>
    /// Everything a person might type around the key itself, taken off.
    ///
    /// Keys arrive read off a printed certificate, pasted out of a message, or
    /// typed with the dashes in different places. All of those are the same
    /// key and all of them should work.
    /// </summary>
    public static string Normalise(string key) =>
        new([.. key.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static bool FixedTimeEquals(string a, string b) =>
        a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
