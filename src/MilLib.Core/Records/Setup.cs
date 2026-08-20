using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// Changing how this library is set up.
///
/// Everything here is a row in the settings table, and every write goes through
/// this class so that the few values with rules attached cannot be changed by
/// any other route.
///
/// The rule that matters is the accession starting number. It is presentation
/// right up until the first book is accessioned and statutory from that moment
/// on: moving it afterwards would hand out numbers that are already on a shelf,
/// or leave a gap in a register that is supposed not to have one.
/// </summary>
public class Setup(MilLibDbContext db)
{
    /// <summary>
    /// Write one setting.
    ///
    /// The row is created if this install has never had it. A library upgraded
    /// from an older version is missing the keys added since, and a setting
    /// that silently does not save is worse than one that is merely absent.
    /// </summary>
    public async Task SetAsync(string key, string? value, string group, string label,
        SettingType type = SettingType.STRING)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);

        if (row is null)
        {
            db.Settings.Add(new Setting
            {
                Key = key,
                Value = value,
                Type = type,
                Group = group,
                Label = label,
                IsEditable = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.Now;

            db.Settings.Update(row);
        }

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// A yes-or-no setting, written as "1" or "0".
    ///
    /// Those two are what the PHP application writes and what
    /// <see cref="Preferences.Flag"/> reads. Writing "true" here would be read
    /// correctly by this application and by the other one too — but the pair of
    /// them would then disagree about what the file looks like, and the first
    /// person to open it with a third tool would have to work out why.
    /// </summary>
    public Task SetFlagAsync(string key, bool on, string group, string label) =>
        SetAsync(key, on ? "1" : "0", group, label, SettingType.BOOL);

    // ----------------------------------------------------------- branding --

    public async Task SaveBrandingAsync(Branding branding, long byUserId)
    {
        await SetAsync("branding.org_name", branding.Organisation.Trim(), "branding", "Organisation");
        await SetAsync("branding.library_name", branding.LibraryName.Trim(), "branding", "Library name");
        await SetAsync("branding.motto", branding.Motto.Trim(), "branding", "Motto");
        await SetAsync("branding.accent_colour", await ColourAsync("branding.accent_colour", branding.Accent),
            "branding", "Accent colour");
        await SetAsync("branding.bar_colour", await ColourAsync("branding.bar_colour", branding.BarColour),
            "branding", "Top bar colour");
        await SetAsync("branding.default_theme", branding.Theme == "light" ? "light" : "dark",
            "branding", "Default theme");
        await SetFlagAsync("branding.crest_circle", branding.CrestInCircle, "branding", "Crest in a circle");
        await SetAsync("branding.crest_circle_colour",
            await ColourAsync("branding.crest_circle_colour", branding.CrestCircleColour),
            "branding", "Circle colour");

        // Only touched when the crest itself was changed. Null means "leave it
        // alone"; empty means "there is no crest now".
        if (branding.CrestPath is not null)
        {
            await SetAsync("branding.logo_path", branding.CrestPath, "branding", "Crest");
        }

        Journal.Note(db, byUserId, "SETTINGS_BRANDING", "settings", null, new
        {
            branding.Organisation,
            branding.LibraryName,
            branding.Motto,
            branding.Accent,
        });

        await db.SaveAndForgetAsync();
    }

    // ---------------------------------------------------------- accession --

    /// <summary>Whether the starting number is still open to being changed.</summary>
    public async Task<bool> RegisterIsEmptyAsync() => !await db.Copies.AnyAsync();

    /// <summary>
    /// The numbering. The prefix and the padding are presentation and may be
    /// changed whenever; the starting number may not, once anything at all is
    /// on the register. Returns what went wrong, or nothing.
    /// </summary>
    public async Task<string?> SaveAccessionAsync(
        string prefix, bool showPrefix, int padLength, int startFrom, long byUserId)
    {
        var current = await db.Settings
            .Where(s => s.Key == "accession.start_from")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var stored = int.TryParse(current, out var was) ? was : 1;

        // Asked before anything is written. A save that half-happens and then
        // reports a refusal leaves somebody unsure which half took.
        if (startFrom != stored && !await RegisterIsEmptyAsync())
        {
            return "The starting number is locked once books are on the register — "
                + "changing it would hand out numbers that are already on a shelf.";
        }

        await SetAsync("accession.prefix", prefix.Trim(), "accession", "Accession prefix");
        await SetFlagAsync("accession.show_prefix", showPrefix, "accession", "Show the prefix");
        await SetAsync("accession.pad_length", Math.Clamp(padLength, 1, 12).ToString(),
            "accession", "Digits in the number", SettingType.INT);

        if (startFrom != stored)
        {
            await SetAsync("accession.start_from", Math.Max(1, startFrom).ToString(),
                "accession", "Start numbering from", SettingType.INT);

            // The live counter moves with it, or the setting says one thing and
            // the next book is given another.
            await db.AccessionCounters
                .Where(c => c.Scope == "default")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextSeq, Math.Max(1, startFrom)));
        }

        Journal.Note(db, byUserId, "SETTINGS_ACCESSION", "settings", null,
            new { prefix, showPrefix, padLength, startFrom });

        await db.SaveAndForgetAsync();

        return null;
    }

    // ------------------------------------------------------------- labels --

    /// <summary>
    /// The label stock, in millimetres.
    ///
    /// Pure calibration: the numbers say how big the paper in the printer is,
    /// so a sheet of A4 stickers and a roll on a thermal printer both come out
    /// right without either being assumed.
    /// </summary>
    public async Task SaveLabelsAsync(
        double pocketWidth, double pocketHeight, double spineWidth, double spineHeight,
        string printMode, string code, int printerDpi, long byUserId)
    {
        await SetAsync("barcode.printer_dpi",
            (printerDpi == Documents.Zpl.FineDpi ? Documents.Zpl.FineDpi : Documents.Zpl.CommonDpi).ToString(),
            "labels", "Thermal printer resolution", SettingType.INT);

        await SetAsync("barcode.pocket_width_mm", Size(pocketWidth), "labels", "Pocket label width");
        await SetAsync("barcode.pocket_height_mm", Size(pocketHeight), "labels", "Pocket label height");
        await SetAsync("barcode.spine_width_mm", Size(spineWidth), "labels", "Spine label width");
        await SetAsync("barcode.spine_height_mm", Size(spineHeight), "labels", "Spine label height");
        await SetAsync("barcode.default_print_mode",
            printMode == "label" ? "label" : "sheet", "labels", "Default print layout");
        await SetAsync("barcode.label_code",
            code is "qr" or "both" ? code : "barcode", "labels", "What goes on a pocket label");

        Journal.Note(db, byUserId, "SETTINGS_LABELS", "settings", null,
            new { pocketWidth, pocketHeight, spineWidth, spineHeight, printMode, code, printerDpi });

        await db.SaveAndForgetAsync();
    }

    // ----------------------------------------------------------- features --

    /// <summary>
    /// Turning a screen on or off.
    ///
    /// A flag reveals or hides a screen and never touches a table — every
    /// install carries the whole schema — so turning one on months later shows
    /// work that was always possible rather than needing anything migrated.
    /// </summary>
    public async Task SetFeatureAsync(Feature feature, bool on, long byUserId)
    {
        await SetFlagAsync(KeyOf(feature), on, "features", NameOf(feature));

        Journal.Note(db, byUserId, on ? "FEATURE_ON" : "FEATURE_OFF", "settings", null,
            new { feature = feature.ToString(), on });

        await db.SaveAndForgetAsync();
    }

    public static string KeyOf(Feature feature) => feature switch
    {
        Feature.Barcode => "feature.barcode",
        Feature.Renewals => "feature.renewals",
        Feature.Reservations => "feature.reservations",
        Feature.Fines => "feature.fines",
        Feature.StockVerify => "feature.stock_verify",
        Feature.Withdrawal => "feature.withdrawal",
        Feature.Classified => "feature.classified",
        Feature.ReportsAdvanced => "feature.reports_advanced",
        Feature.MemberLogin => "feature.member_login",
        Feature.Branches => "feature.branches",
        Feature.Acquisitions => "feature.acquisitions",
        Feature.Serials => "feature.serials",
        _ => "feature." + feature.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// What a flag is called on the screen. Spelt out rather than derived,
    /// because these members are PascalCase and the general rule that turns
    /// UPPER_SNAKE into words would make "Stockverify" of this one.
    /// </summary>
    public static string NameOf(Feature feature) => feature switch
    {
        Feature.Barcode => "Barcodes and labels",
        Feature.Renewals => "Renewals",
        Feature.Reservations => "Reservations",
        Feature.Fines => "Fines",
        Feature.StockVerify => "Stock verification",
        Feature.Withdrawal => "Withdrawal register",
        Feature.Classified => "Classified holdings",
        Feature.ReportsAdvanced => "Advanced reports",
        Feature.MemberLogin => "Member catalogue",
        Feature.Branches => "Branches",
        Feature.Acquisitions => "Acquisitions",
        Feature.Serials => "Serials",
        _ => feature.ToString(),
    };

    /// <summary>What each flag actually does, said on the screen beside it.</summary>
    public static string WhatItDoes(Feature feature) => feature switch
    {
        Feature.Barcode => "Barcode and QR labels for the books.",
        Feature.Renewals => "Extending a loan at the counter.",
        Feature.Reservations => "Holds — a queue on a book that is out.",
        Feature.Fines => "Charges for late, damaged and lost books.",
        Feature.StockVerify => "Counting the shelves against the register.",
        Feature.Withdrawal => "The condemnation register — taking books off the books.",
        Feature.Classified => "Security classifications, custody at issue, and the classified report.",
        Feature.ReportsAdvanced => "The deeper reports, such as what is borrowed most.",
        Feature.MemberLogin => "Members searching the catalogue for themselves.",
        Feature.Branches => "More than one library sharing these records.",
        Feature.Acquisitions => "Ordering and receiving.",
        Feature.Serials => "Periodicals and their issues.",
        _ => "",
    };

    /// <summary>
    /// Flags whose screens do not exist in either application yet. They are
    /// shown, and refuse to be switched on, because a switch that turns nothing
    /// on is worse than one that says why.
    /// </summary>
    public static bool NotBuiltYet(Feature feature) =>
        feature is Feature.Acquisitions or Feature.Serials;

    /// <summary>The house colour, used only where a unit has never set one.</summary>
    public const string DefaultAccent = "#c0392b";

    /// <summary>The near-black the top bar starts on, until a unit paints it.</summary>
    public const string DefaultBar = "#0d0d0d";

    /// <summary>
    /// Six hex digits with a hash, in lower case — or nothing, if that is not
    /// what it was given.
    /// </summary>
    private static string? Colour(string value)
    {
        value = value.Trim();

        if (value.Length == 0)
        {
            return null;
        }

        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return value.Length == 7 && value[1..].All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// The colour to store: the one given, or the one already there.
    ///
    /// This is sold to units that put their own colours on it, and the colour
    /// is not a preference — it is what their headed paper looks like. A save
    /// carrying a colour this code cannot read used to write the house red
    /// over it, which turned a fumbled keystroke into a rebranding nobody
    /// asked for and nobody was told about. What is already there is kept
    /// instead, and only an install that has never chosen gets the house red.
    /// </summary>
    private async Task<string> ColourAsync(string key, string value)
    {
        if (Colour(value) is { } good)
        {
            return good;
        }

        var stored = (await db.Settings.FirstOrDefaultAsync(s => s.Key == key))?.Value;

        return Colour(stored ?? "") ?? DefaultAccent;
    }

    /// <summary>
    /// Millimetres, stored the way somebody would write them rather than as a
    /// double's idea of them: "51" and "50.8", not "51.000000".
    /// </summary>
    private static string Size(double mm) =>
        Math.Round(Math.Clamp(mm, 5, 300), 1).ToString("0.##");
}

/// <summary>
/// What the unit calls itself and what that looks like on a page.
///
/// Carried as one record because the branding form saves as one thing: a save
/// that set the name but not the crest would leave a page half-rebranded.
/// <see cref="CrestPath"/> is the exception — null means the crest was not
/// touched, "" means there is deliberately no longer one.
/// </summary>
public record Branding(
    string Organisation,
    string LibraryName,
    string Motto,
    string Accent,
    string BarColour,
    string Theme,
    bool CrestInCircle,
    string CrestCircleColour,
    string? CrestPath);
