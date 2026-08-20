using MilLib.Core.Licensing;

namespace MilLib.Desktop.Services;

/// <summary>
/// This product's licensing, wired to this machine and this installation.
///
/// The salt is the shared secret of the whole scheme: a key is
/// <c>sha256(hardwareId + salt + expiryTag)</c>, the vendor's Licence Manager
/// signs with it and this re-derives against it. Anybody holding it can mint
/// keys for this product.
///
/// In the web application it lives in a .env file that is not committed. A
/// desktop application has nowhere like that to put it — it ships as one
/// executable, and a secret in a file beside that executable is a secret in a
/// file anybody can open. So it is compiled in, which is the same choice the
/// other desktop products in this house made, and it is honest about what that
/// buys: it stops casual copying, not a determined person with a disassembler.
/// The thing actually protecting the records is that they are on a unit's own
/// machine behind its own doors.
/// </summary>
public static class Licensing
{
    // Kept out of committed source, so a public copy of the repository does
    // not carry the licensing secret. See LicenceSecret.cs (the real value,
    // git-ignored) and LicenceSecret.Default.cs (the stand-in).
    private const string Salt = LicenceSecret.Salt;

    /// <summary>
    /// Where this installation stands, as of the last time anything asked.
    ///
    /// Read at sign-in and kept, so every screen can put a banner up without
    /// each of them going to the database and working out a hardware
    /// fingerprint for itself.
    /// </summary>
    public static LicenceStanding? Standing { get; private set; }

    public static Licence For(Core.Data.MilLibDbContext db) =>
        new(db, Machine.HardwareId, Salt);

    /// <summary>Ask again, and remember the answer.</summary>
    public static async Task<LicenceStanding> RefreshAsync()
    {
        await using var db = Workspace.Open();

        return Standing = await For(db).StandingAsync(DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>
    /// Whether the application may be used. Unknown counts as usable: a
    /// licence check that could not be made is not evidence of anything, and
    /// locking a unit out of its own library because a database read failed
    /// would be the worst possible way to be wrong.
    /// </summary>
    public static bool Usable => Standing?.Usable ?? true;
}
