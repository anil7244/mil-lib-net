using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

namespace MilLib.Core.Licensing;

/// <summary>Which of the four states this installation is in.</summary>
public enum LicenceState
{
    /// <summary>Inside the free period. Everything works.</summary>
    Trial,

    /// <summary>The free period is over and no key has been entered.</summary>
    TrialOver,

    /// <summary>A genuine key for this machine. Everything works.</summary>
    Licensed,

    /// <summary>A genuine key whose date has passed.</summary>
    Expired,
}

/// <summary>
/// Where this installation stands, and what to say about it.
/// </summary>
public record LicenceStanding(
    LicenceState State,
    string HardwareId,
    string? Key,
    DateOnly? Expires,
    bool Perpetual,
    int DaysLeft)
{
    /// <summary>Whether the application may be used at all.</summary>
    public bool Usable => State is LicenceState.Trial or LicenceState.Licensed;

    /// <summary>
    /// Whether it is worth saying something at the top of every screen.
    ///
    /// A trial always is — somebody has to know it will stop. A licence only
    /// once it is close enough that renewing it is this month's problem, so
    /// the banner means something when it appears rather than becoming part of
    /// the furniture.
    /// </summary>
    public bool WorthSaying =>
        State != LicenceState.Licensed || (!Perpetual && DaysLeft <= 30);

    public string Headline => State switch
    {
        LicenceState.Trial => DaysLeft <= 1
            ? "The trial ends today."
            : $"Trial — {DaysLeft} days left.",

        LicenceState.TrialOver => "The trial has ended.",

        LicenceState.Expired => "The licence has expired.",

        _ when Perpetual => "Licensed.",

        _ when DaysLeft <= 0 => "The licence expires today.",

        _ when DaysLeft <= 30 => $"Licensed — {DaysLeft} days left.",

        _ => $"Licensed until {Expires:dd MMM yyyy}.",
    };

    /// <summary>What to do about it, or nothing when there is nothing to do.</summary>
    public string What => State switch
    {
        LicenceState.Trial =>
            "Everything works. Enter a licence key before it ends and nothing stops.",

        LicenceState.TrialOver =>
            "The records are all still here and nothing has been lost. "
            + "A licence key is needed before the library can be used again.",

        LicenceState.Expired =>
            "The records are all still here. Ask for a renewal key — it is issued "
            + "against this same machine, and entering it puts everything back.",

        _ when Perpetual => "This licence does not expire.",

        _ when DaysLeft <= 30 => "Ask for a renewal key before the date, and nothing stops.",

        _ => "",
    };

    /// <summary>How serious this is, for the screen to colour by.</summary>
    public bool Grave => State is LicenceState.TrialOver or LicenceState.Expired
        || (State == LicenceState.Trial && DaysLeft <= 3)
        || (State == LicenceState.Licensed && !Perpetual && DaysLeft <= 7);
}

/// <summary>
/// Whether this copy may be used, and for how much longer.
///
/// The licence is bound to the machine, not to the folder: the hardware
/// fingerprint is worked out fresh every time and never written down. An
/// earlier version of the web application cached it to a file, which meant
/// copying the folder to another machine carried the first machine's identity
/// along with it and the copy ran licensed. That mistake is not repeated here,
/// and the fingerprint is passed in rather than found here for the same reason
/// — how a machine is identified is the application's business, not the
/// library's.
///
/// The state lives in the same <c>license_info</c> table the web application
/// uses, so a unit that has already activated on this machine is already
/// activated here.
/// </summary>
public class Licence(MilLibDbContext db, string hardwareId, string salt)
{
    /// <summary>How long a new installation may be used before a key is needed.</summary>
    public const int TrialDays = 14;

    public string HardwareId => hardwareId;

    /// <summary>
    /// Where this installation stands today.
    ///
    /// Starts the trial the first time it is asked, which is the only moment
    /// the application can know it is a first run. Nothing else has to
    /// remember to do it.
    /// </summary>
    public async Task<LicenceStanding> StandingAsync(DateOnly today)
    {
        var row = await db.LicenseInfo
            .FirstOrDefaultAsync(l => l.HardwareId == hardwareId);

        if (row is null)
        {
            row = new LicenseInfo
            {
                HardwareId = hardwareId,
                TrialStartedAt = DateTime.Now,
                IsActive = false,
                AppName = Vendor.Product,
                AppVersion = Vendor.Version,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };

            db.LicenseInfo.Add(row);

            await db.SaveAndForgetAsync();
        }
        else if (row.TrialStartedAt is null)
        {
            row.TrialStartedAt = DateTime.Now;

            db.LicenseInfo.Update(row);

            await db.SaveAndForgetAsync();
        }

        // A key on the row is not taken at its word. It is re-derived against
        // this machine every time, so a row copied from another installation —
        // or edited by hand — does not licence anything.
        if (row.IsActive
            && !string.IsNullOrWhiteSpace(row.LicenseKey)
            && LicenceKey.Verify(row.LicenseKey, hardwareId, salt))
        {
            var parsed = LicenceKey.Parse(row.LicenseKey);

            var expires = parsed?.Expires;
            var perpetual = parsed?.Perpetual ?? true;

            if (perpetual || expires is null)
            {
                return new LicenceStanding(
                    LicenceState.Licensed, hardwareId, LicenceKey.Tidy(row.LicenseKey),
                    null, true, int.MaxValue);
            }

            // Valid through the whole of the expiry day. A licence that dies at
            // midnight on the morning of the date printed on it is a licence
            // that expires a day early as far as anybody reading it is
            // concerned.
            var left = expires.Value.DayNumber - today.DayNumber;

            return new LicenceStanding(
                left >= 0 ? LicenceState.Licensed : LicenceState.Expired,
                hardwareId, LicenceKey.Tidy(row.LicenseKey), expires, false, left);
        }

        var started = DateOnly.FromDateTime(row.TrialStartedAt ?? DateTime.Now);
        var ends = started.AddDays(TrialDays);
        var daysLeft = ends.DayNumber - today.DayNumber;

        return new LicenceStanding(
            daysLeft >= 0 ? LicenceState.Trial : LicenceState.TrialOver,
            hardwareId, null, ends, false, Math.Max(0, daysLeft));
    }

    /// <summary>
    /// Enter a key. Returns what to say, and whether it worked.
    ///
    /// The refusals are deliberately specific about the shape of the key and
    /// deliberately vague about why a well-formed one did not match: telling
    /// somebody their key is right but for the wrong machine is a useful thing
    /// to say, and telling them which half of it was right is not.
    /// </summary>
    public async Task<(bool Ok, string Said)> ActivateAsync(string entered, DateOnly today)
    {
        var clean = LicenceKey.Normalise(entered);

        if (clean.Length == 0)
        {
            return (false, "Type the licence key.");
        }

        if (clean.Length is not (25 or 31))
        {
            return (false,
                $"That is {clean.Length} characters. A licence key is 25 or 31 — "
                + "five groups of five, and on newer keys six digits after them.");
        }

        if (!clean.All(c => LicenceKey.Charset.Contains(c) || char.IsAsciiDigit(c)))
        {
            return (false, "There is a character in that key which never appears in one. "
                + "Licence keys have no O, I, L, zero or one in them — check for a "
                + "letter O typed where a zero was printed, or the other way round.");
        }

        if (!LicenceKey.Verify(entered, hardwareId, salt))
        {
            return (false, "That key is not for this machine. It is a genuine-looking key, "
                + "but licence keys are issued against one machine's hardware — check that "
                + "the hardware ID on this screen is the one the key was asked for.");
        }

        var parsed = LicenceKey.Parse(entered);
        var expires = parsed?.Expires;

        if (parsed is { Perpetual: false } && expires is { } date && date < today)
        {
            return (false, $"That key was issued to expire on {date:dd MMM yyyy}, which has passed. "
                + "It is genuine — ask for a renewal against the same hardware ID.");
        }

        var row = await db.LicenseInfo.FirstOrDefaultAsync(l => l.HardwareId == hardwareId)
            ?? new LicenseInfo { HardwareId = hardwareId, CreatedAt = DateTime.Now };

        row.LicenseKey = LicenceKey.Tidy(entered);
        row.IsActive = true;
        row.ActivatedAt = DateTime.Now;
        row.DeactivatedAt = null;
        row.ExpiresAt = expires?.ToDateTime(new TimeOnly(23, 59, 59));
        row.LastCheckedAt = DateTime.Now;
        row.AppName = Vendor.Product;
        row.AppVersion = Vendor.Version;
        row.UpdatedAt = DateTime.Now;

        if (row.Id == 0)
        {
            db.LicenseInfo.Add(row);
        }
        else
        {
            db.LicenseInfo.Update(row);
        }

        await db.SaveAndForgetAsync();

        return (true, parsed is { Perpetual: true } || expires is null
            ? "Activated. This licence does not expire."
            : $"Activated. Licensed until {expires:dd MMM yyyy}.");
    }
}

/// <summary>
/// Who made this and what it is called.
///
/// A product-level constant, deliberately not one of the branding settings: a
/// unit configures its own name, crest and motto, and cannot edit the mark of
/// whoever wrote the software off its own screens.
/// </summary>
public static class Vendor
{
    public const string Company = "Tactical Code";

    public const string Product = "Tactical Library Mgmt Sys";

    public const string Version = "1.0";

    public const string Website = "www.tacticalcode.in";

    public const string Phone = "+91 96433 25206";

    public const string Email = "anil7244@gmail.com";

    public const string Address = "Samba, J&K, India";
}
