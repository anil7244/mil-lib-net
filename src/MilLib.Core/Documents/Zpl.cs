namespace MilLib.Core.Documents;

/// <summary>
/// The stock loaded in a thermal printer: how big a label is, and how finely
/// the printer draws.
///
/// Both come from the settings rather than from this code, which is the point.
/// Label stock is bought locally and no two units have the same; a size
/// compiled in is a size somebody has to telephone about.
/// </summary>
public record Stock(float WidthMm, float HeightMm, int Dpi = Zpl.CommonDpi)
{
    /// <summary>
    /// Dots per millimetre. 203 dpi is eight to the millimetre and is what
    /// nearly every desktop Zebra is; 300 dpi is just under twelve and is the
    /// other one a unit is likely to have.
    /// </summary>
    public double PerMm => Dpi / 25.4;

    public int Across => (int)Math.Round(WidthMm * PerMm);

    public int Down => (int)Math.Round(HeightMm * PerMm);

    /// <summary>
    /// A distance in millimetres, in dots. Everything on a label is placed
    /// this way: a fixed dot count is a different physical distance on a
    /// different printer, and the print walks across the label.
    /// </summary>
    public int Dots(double mm) => (int)Math.Round(mm * PerMm);
}

/// <summary>
/// ZPL for a Zebra thermal printer.
///
/// The printer draws its own Code 128 from <c>^BC</c>, so what goes down the
/// wire is text. That is why this path produces a better label than a sheet
/// through an office printer: nothing is rasterised, so the bars land exactly
/// on dot boundaries and scan first time.
///
/// Every dimension is worked out from the stock size in the settings and the
/// printer's resolution, in millimetres. Nothing here is a dot count somebody
/// measured once on one printer: change the stock on the Settings screen, or
/// say the printer is a 300 dpi one, and the labels come out right without
/// anybody editing this file.
///
/// What still cannot be done without the printer in the room is confirming
/// that the stock named in the settings is the stock actually loaded. That is
/// what <see cref="Calibration"/> is for — one label with a measured square on
/// it, held against a ruler.
/// </summary>
public static class Zpl
{
    /// <summary>What a desktop Zebra almost always is.</summary>
    public const int CommonDpi = 203;

    /// <summary>The other resolution worth offering. Anything else is unusual.</summary>
    public const int FineDpi = 300;

    public static string Batch(IEnumerable<LabelFor> books, LabelKind kind, Stock stock)
    {
        var text = new System.Text.StringBuilder();

        foreach (var book in books)
        {
            text.Append(kind == LabelKind.Spine ? Spine(book, stock) : Pocket(book, stock));
        }

        return text.ToString();
    }

    /// <summary>The code, the number under it, and as much of the title as fits.</summary>
    public static string Pocket(LabelFor book, Stock stock)
    {
        var margin = stock.Dots(2);
        var usable = stock.Across - (margin * 2);

        var titleHeight = stock.Dots(3);
        var numberHeight = stock.Dots(3.6);

        // The bars take whatever is left between the two lines of text, so a
        // taller label gets taller bars rather than the same bars with a gap
        // under them. Floored, because bars below about 6 mm stop scanning.
        var bars = Math.Max(
            stock.Dots(6),
            stock.Down - (margin * 2) - titleHeight - numberHeight - stock.Dots(2));

        // How much of the title fits, from the width and the character cell
        // rather than from a count that happened to look right on 51 mm stock.
        // ZPL's scalable font is about 0.6 of its height wide.
        var title = Clean(book.Title);
        var fits = Math.Max(8, (int)(usable / (titleHeight * 0.6)));

        if (title.Length > fits)
        {
            title = title[..fits];
        }

        return string.Join("\n",
        [
            "^XA",
            "^CI28",
            "^PW" + stock.Across,
            "^LL" + stock.Down,
            $"^FO{margin},{margin}^A0N,{titleHeight},{titleHeight}^FD{title}^FS",

            // Module width: 0.25 mm is the narrowest bar that reliably scans
            // off thermal stock. At 203 dpi that is 2 dots and at 300 it is 3,
            // which is the same physical width — worked out rather than chosen.
            $"^BY{Math.Max(2, stock.Dots(0.25))},3,{bars}",
            $"^FO{margin},{margin + titleHeight + stock.Dots(1)}"
                + $"^BCN,{bars},N,N,N^FD{Clean(book.Barcode)}^FS",
            $"^FO{margin},{stock.Down - margin - numberHeight}"
                + $"^A0N,{numberHeight},{numberHeight}^FD{Clean(book.Accession)}^FS",
            "^XZ",
        ]) + "\n";
    }

    /// <summary>The call number, wrapped and centred. Nothing else.</summary>
    public static string Spine(LabelFor book, Stock stock)
    {
        var mark = Clean(book.CallNumber.Length > 0 ? book.CallNumber : book.Accession);

        var margin = stock.Dots(1.5);
        var usable = stock.Across - (margin * 2);
        var height = stock.Dots(4.2);

        return string.Join("\n",
        [
            "^XA",
            "^CI28",
            "^PW" + stock.Across,
            "^LL" + stock.Down,

            // Call numbers stack. ^FB wraps them inside the narrow spine width
            // and centres each line, which is how a spine label is read on a
            // shelf at an angle.
            $"^FO{margin},{stock.Dots(5)}^A0N,{height},{height}"
                + $"^FB{usable},4,{stock.Dots(0.8)},C^FD{mark}^FS",
            "^XZ",
        ]) + "\n";
    }

    /// <summary>
    /// One label with a square on it of a known size, and the numbers it was
    /// printed from.
    ///
    /// This is the whole of the calibration, and it is why the stock sizes no
    /// longer have to be guessed by whoever wrote the software. Print it and
    /// hold a ruler against the square: if it measures 10 mm, the settings
    /// match the stock and every label after it is right. If it does not, the
    /// difference says exactly which way to change the setting.
    ///
    /// It exists because the alternative — printing five hundred book labels to
    /// find out — is how a roll of stock gets wasted.
    /// </summary>
    public static string Calibration(Stock stock)
    {
        var margin = stock.Dots(2);
        var box = stock.Dots(10);
        var text = stock.Dots(2.6);
        var rule = Math.Max(1, stock.Dots(0.3));
        var beside = margin + box + stock.Dots(2);

        return string.Join("\n",
        [
            "^XA",
            "^CI28",
            "^PW" + stock.Across,
            "^LL" + stock.Down,

            // A 10 mm square, drawn from the same arithmetic every label uses.
            // If this measures 10 mm, they all will.
            $"^FO{margin},{margin}^GB{box},{box},{rule}^FS",
            $"^FO{beside},{margin}^A0N,{text},{text}^FD10 mm square^FS",
            $"^FO{beside},{margin + text + stock.Dots(1)}^A0N,{text},{text}"
                + $"^FD{stock.WidthMm:0.#} x {stock.HeightMm:0.#} mm^FS",
            $"^FO{beside},{margin + (text * 2) + stock.Dots(2)}^A0N,{text},{text}"
                + $"^FD{stock.Dpi} dpi = {stock.Across} x {stock.Down} dots^FS",

            // A rule along the bottom edge, the full width of the label. If the
            // stock is narrower than the setting says, this runs off the side —
            // which is visible at a glance without measuring anything.
            $"^FO0,{stock.Down - stock.Dots(1.5)}^GB{stock.Across},{rule},{rule}^FS",
            "^XZ",
        ]) + "\n";
    }

    /// <summary>
    /// The two characters ZPL reads as commands. A title containing one would
    /// otherwise end the field early and print the rest as instructions.
    /// </summary>
    private static string Clean(string value) =>
        value.Replace('^', ' ').Replace('~', ' ');
}
