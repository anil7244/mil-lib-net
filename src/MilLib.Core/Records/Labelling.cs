using Microsoft.EntityFrameworkCore;
using MilLib.Core.Documents;

namespace MilLib.Core.Data;

/// <summary>
/// Finding the books that need labels, and reading the stock sizes the unit
/// bought.
/// </summary>
public class Labelling(MilLibDbContext db, Preferences preferences)
{
    /// <summary>
    /// Copies matching what was typed — an accession number, part of a title,
    /// or nothing at all, which gives the ones most recently taken on.
    ///
    /// Newest first when nothing is typed, because the books that need labels
    /// are almost always the ones that arrived this week.
    /// </summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title)>> FindAsync(string search, int limit = 300)
    {
        search = search.Trim();

        var copies = db.Copies.AsQueryable();

        if (search.Length > 0)
        {
            var like = $"%{search}%";

            // The prefix is how the number is said out loud, not how it is
            // stored, so a number read off an existing label still finds it.
            var bare = preferences.AccessionPrefix.Length > 0
                && search.StartsWith(preferences.AccessionPrefix, StringComparison.OrdinalIgnoreCase)
                    ? search[preferences.AccessionPrefix.Length..]
                    : search;

            var bareLike = $"%{bare}%";

            copies = copies.Where(c =>
                EF.Functions.Like(c.AccessionNo, bareLike)
                || EF.Functions.Like(c.Barcode, bareLike)
                || EF.Functions.Like(c.BookNo!, like)
                || db.Titles.Any(t => t.TitleId == c.TitleId && EF.Functions.Like(t.Name, like)));
        }

        var rows = await copies
            .OrderByDescending(c => c.CopyId)
            .Take(limit)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        // Shown in register order once found. Somebody labelling a batch works
        // along the numbers, not backwards from the newest.
        return [.. rows.OrderBy(r => r.c.AccessionSeq).ThenBy(r => r.c.AccessionNo)
            .Select(r => (r.c, r.t))];
    }

    /// <summary>Every copy in a stretch of the register — how a new intake gets labelled.</summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title)>> InRangeAsync(int from, int to)
    {
        var rows = await db.Copies
            .Where(c => c.AccessionSeq >= from && c.AccessionSeq <= to)
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        return [.. rows.Select(r => (r.c, r.t))];
    }

    /// <summary>What goes on the label, for one copy.</summary>
    public LabelFor Describe(Copy copy, Title title) => new(
        preferences.Accession(copy.AccessionNo),
        copy.Barcode,
        title.Name,
        title.CallNumber ?? "");

    // ------------------------------------------------------------ the stock --

    public float PocketWidthMm => Millimetres("barcode.pocket_width_mm", 51);

    public float PocketHeightMm => Millimetres("barcode.pocket_height_mm", 25);

    public float SpineWidthMm => Millimetres("barcode.spine_width_mm", 25);

    public float SpineHeightMm => Millimetres("barcode.spine_height_mm", 38);

    /// <summary>Whether the unit prints a barcode, a QR code, or both.</summary>
    /// <summary>
    /// How finely the thermal printer draws, in dots per inch.
    ///
    /// A setting rather than a constant because it is the one thing about a
    /// Zebra that changes what every dimension means: the same label at 300 dpi
    /// is half the physical size if the software assumes 203.
    /// </summary>
    public int Dpi => preferences.Number("barcode.printer_dpi", Zpl.CommonDpi) switch
    {
        Zpl.FineDpi => Zpl.FineDpi,
        _ => Zpl.CommonDpi,
    };

    /// <summary>The stock loaded, for whichever kind of label is being printed.</summary>
    public Stock StockFor(LabelKind kind) => kind == LabelKind.Spine
        ? new Stock(SpineWidthMm, SpineHeightMm, Dpi)
        : new Stock(PocketWidthMm, PocketHeightMm, Dpi);

    public LabelCode Code => preferences.Text("barcode.label_code", "barcode").ToLowerInvariant() switch
    {
        "qr" => LabelCode.Qr,
        "both" => LabelCode.Both,
        _ => LabelCode.Barcode,
    };

    /// <summary>
    /// A size in millimetres. Stored as text because the settings table stores
    /// everything as text, and a unit that typed "51mm" into the box should get
    /// 51 rather than a sheet of labels one point wide.
    /// </summary>
    private float Millimetres(string key, float fallback)
    {
        var raw = new string(preferences.Text(key).Where(c => char.IsDigit(c) || c == '.').ToArray());

        return float.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
