using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// How this library is set up: what it is called, what it looks like, which
/// screens it has, and how it numbers its books.
///
/// All of it is rows in the settings table rather than anything in this code,
/// which is what lets one install call itself a unit library with fines turned
/// off and the next call itself something else with fines on — without either
/// of them being a different build.
///
/// Read once when the application opens and again when Settings is saved. The
/// whole table is thirty-odd rows; reading it per screen would be work done
/// hundreds of times to notice a change that happens twice a year.
/// </summary>
public class Preferences
{
    private readonly Dictionary<string, string?> _values;

    private Preferences(Dictionary<string, string?> values) => _values = values;

    public static Preferences Empty { get; } = new([]);

    public static async Task<Preferences> ReadAsync(MilLibDbContext db)
    {
        var rows = await db.Settings.ToListAsync();

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            values[row.Key] = row.Value;
        }

        return new Preferences(values);
    }

    // ------------------------------------------------------------ reading --

    public string Text(string key, string fallback = "") =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    /// <summary>
    /// Anything but "1", "true", "yes" or "on" is off — including a key that
    /// is not there at all. A feature nobody has turned on is off, which is the
    /// only safe way for a missing row to be read.
    /// </summary>
    public bool Flag(string key, bool fallback = false)
    {
        if (!_values.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    public int Number(string key, int fallback) =>
        _values.TryGetValue(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    // ----------------------------------------------------------- branding --

    public string LibraryName => Text("branding.library_name", "Library");
    public string OrganisationName => Text("branding.org_name");
    public string Motto => Text("branding.motto");
    public string AccentColour => Text("branding.accent_colour", Setup.DefaultAccent);
    public string CrestPath => Text("branding.logo_path");
    public bool CrestInCircle => Flag("branding.crest_circle");
    public string CrestCircleColour => Text("branding.crest_circle_colour", "#0D0D0D");
    public string DefaultTheme => Text("branding.default_theme", "light");

    // ---------------------------------------------------------- accession --

    public string AccessionPrefix => Text("accession.prefix");
    public bool ShowAccessionPrefix => Flag("accession.show_prefix", true);
    public int AccessionPadLength => Number("accession.pad_length", 6);
    public int AccessionStartFrom => Number("accession.start_from", 1);

    /// <summary>
    /// An accession number as it should appear on a screen or a label.
    ///
    /// The prefix is part of how the unit refers to its books but not part of
    /// the number itself, and an install that finds it noise can hide it
    /// without the numbers underneath changing.
    /// </summary>
    public string Accession(string raw) =>
        ShowAccessionPrefix && AccessionPrefix.Length > 0 ? AccessionPrefix + raw : raw;

    // ------------------------------------------------------- circulation ---

    public string BarcodeSymbology => Text("barcode.symbology", "CODE128");
    public string Currency => Text("fine.currency", "INR");

    /// <summary>The symbol to put in front of an amount. INR has one; not all do.</summary>
    public string CurrencySymbol => Currency.ToUpperInvariant() switch
    {
        "INR" => "₹",
        "USD" => "$",
        "GBP" => "£",
        "EUR" => "€",
        _ => "",
    };

    public string Money(decimal amount) =>
        CurrencySymbol + amount.ToString("N2", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ screens --

    /// <summary>
    /// Whether a screen exists on this install.
    ///
    /// A flag hides a screen. It never hides a table: every install carries the
    /// whole schema whichever flags are set, so turning one on later reveals a
    /// screen rather than requiring a migration.
    /// </summary>
    public bool Has(Feature feature) => feature switch
    {
        Feature.Barcode => Flag("feature.barcode", true),
        Feature.Renewals => Flag("feature.renewals", true),
        Feature.Reservations => Flag("feature.reservations"),
        Feature.Fines => Flag("feature.fines"),
        Feature.StockVerify => Flag("feature.stock_verify"),
        Feature.Withdrawal => Flag("feature.withdrawal"),
        Feature.Classified => Flag("feature.classified"),
        Feature.ReportsAdvanced => Flag("feature.reports_advanced"),
        Feature.MemberLogin => Flag("feature.member_login"),
        Feature.Branches => Flag("feature.branches"),
        Feature.Acquisitions => Flag("feature.acquisitions"),
        Feature.Serials => Flag("feature.serials"),
        _ => false,
    };
}

public enum Feature
{
    Barcode,
    Renewals,
    Reservations,
    Fines,
    StockVerify,
    Withdrawal,
    Classified,
    ReportsAdvanced,
    MemberLogin,
    Branches,
    Acquisitions,
    Serials,
}
