using MilLib.Core.Documents;

// The thermal-printer instructions.
//
// This is the one part of the application that cannot be finished at a desk:
// whether the ink lands in the right place on the sticker can only be seen by
// printing on the sticker. What CAN be settled here is everything that used to
// make that a blocker —
//
//   that every dimension is worked out from the stock size in the settings and
//   the printer's resolution, rather than from dot counts compiled in;
//
//   that the same label on a 300 dpi printer comes out the same physical size
//   as on a 203, which is the mistake that makes labels two-thirds too small;
//
//   that nothing is drawn outside the label;
//
//   and that a book title carrying a ZPL control character cannot turn the
//   rest of the label into instructions.
//
// What is left for the printer is one label: Zpl.Calibration draws a 10 mm
// square from the same arithmetic every other label uses. Hold a ruler against
// it. If it measures 10 mm the settings match the stock, and everything below
// follows.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.ZebraProof

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-58}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

void Heading(string text)
{
    Console.WriteLine();
    Console.WriteLine(text);
}

/// <summary>Every ^FO x,y on a label, so nothing can be drawn off the edge unnoticed.</summary>
static IEnumerable<(int X, int Y)> Origins(string zpl)
{
    foreach (var part in zpl.Split("^FO").Skip(1))
    {
        var head = new string(part.TakeWhile(c => char.IsAsciiDigit(c) || c == ',').ToArray());
        var bits = head.Split(',');

        if (bits.Length >= 2
            && int.TryParse(bits[0], out var x)
            && int.TryParse(bits[1], out var y))
        {
            yield return (x, y);
        }
    }
}

static int Field(string zpl, string command)
{
    var at = zpl.IndexOf(command, StringComparison.Ordinal);

    if (at < 0)
    {
        return -1;
    }

    var digits = new string(zpl[(at + command.Length)..]
        .TakeWhile(char.IsAsciiDigit).ToArray());

    return digits.Length > 0 ? int.Parse(digits) : -1;
}

var book = new LabelFor("JAKLI/000001645", "000001645", "Regimental Signalling in the Field", "623.731 SIG");

// ------------------------------------------------ millimetres into dots --

Heading("Millimetres into dots");
{
    var ordinary = new Stock(51, 25, Zpl.CommonDpi);

    // 203 dpi is eight dots to the millimetre, which is the number every
    // Zebra manual quotes.
    Check("203 dpi is eight dots to the millimetre",
        Math.Abs(ordinary.PerMm - 7.992) < 0.01, $"{ordinary.PerMm:0.###} per mm");

    Check("so 51 x 25 mm stock is 408 x 200 dots",
        ordinary.Across == 408 && ordinary.Down == 200,
        $"{ordinary.Across} x {ordinary.Down}");

    var fine = new Stock(51, 25, Zpl.FineDpi);

    Check("and the same stock on a 300 dpi printer is 602 x 295",
        fine.Across == 602 && fine.Down == 295, $"{fine.Across} x {fine.Down}");

    // The whole point of carrying the resolution. A label laid out in dots for
    // a 203 and sent to a 300 comes out two-thirds the size.
    Check("which is the same physical label, not a smaller one",
        Math.Abs((fine.Across / fine.PerMm) - (ordinary.Across / ordinary.PerMm)) < 0.2,
        $"{fine.Across / fine.PerMm:0.#} mm both ways");

    Check("a millimetre measurement is the same distance on either printer",
        Math.Abs((ordinary.Dots(10) / ordinary.PerMm) - (fine.Dots(10) / fine.PerMm)) < 0.1,
        $"{ordinary.Dots(10)} dots and {fine.Dots(10)} dots, both 10 mm");
}

Heading("A pocket label");
{
    var stock = new Stock(51, 25, Zpl.CommonDpi);

    var zpl = Zpl.Pocket(book, stock);

    Check("it is one label", zpl.StartsWith("^XA") && zpl.TrimEnd().EndsWith("^XZ"), "^XA … ^XZ");

    Check("told how wide and how long the stock is",
        Field(zpl, "^PW") == stock.Across && Field(zpl, "^LL") == stock.Down,
        $"^PW{Field(zpl, "^PW")} ^LL{Field(zpl, "^LL")}");

    Check("it carries the barcode the scanner reads", zpl.Contains("^BCN") && zpl.Contains(book.Barcode),
        book.Barcode);

    Check("and the accession number a person reads", zpl.Contains(book.Accession), book.Accession);

    // Nothing off the edge. A field placed past the label prints on the next
    // one, or not at all, and neither is noticed until a roll has gone.
    var origins = Origins(zpl).ToList();

    Check("nothing is placed outside the label",
        origins.All(o => o.X >= 0 && o.X < stock.Across && o.Y >= 0 && o.Y < stock.Down),
        $"{origins.Count} fields, all inside {stock.Across} x {stock.Down}");

    // Bars below about 6 mm stop scanning reliably off thermal stock.
    var bars = Field(zpl[zpl.IndexOf("^BCN", StringComparison.Ordinal)..], "^BCN,");

    Check("the bars are tall enough to scan", bars >= stock.Dots(6),
        $"{bars} dots = {bars / stock.PerMm:0.#} mm");

    Check("and they fit above the number under them",
        bars + stock.Dots(3) + stock.Dots(3.6) <= stock.Down, "inside the label");
}

Heading("The same label on taller stock");
{
    // The behaviour that says the layout is worked out rather than fixed: more
    // room means taller bars, not the same bars with a gap under them.
    var small = new Stock(51, 25, Zpl.CommonDpi);
    var tall = new Stock(51, 38, Zpl.CommonDpi);

    var shortBars = Field(Zpl.Pocket(book, small)[Zpl.Pocket(book, small).IndexOf("^BCN", StringComparison.Ordinal)..], "^BCN,");
    var tallBars = Field(Zpl.Pocket(book, tall)[Zpl.Pocket(book, tall).IndexOf("^BCN", StringComparison.Ordinal)..], "^BCN,");

    Check("taller stock gets taller bars", tallBars > shortBars,
        $"{shortBars} dots on 25 mm, {tallBars} on 38 mm");

    // And more room across means more of the title, rather than the same
    // twenty-six characters chosen once for 51 mm stock.
    var narrow = Zpl.Pocket(book, new Stock(38, 25, Zpl.CommonDpi));
    var wide = Zpl.Pocket(book, new Stock(76, 25, Zpl.CommonDpi));

    var narrowTitle = Between(narrow, "^FD", "^FS");
    var wideTitle = Between(wide, "^FD", "^FS");

    Check("and wider stock shows more of the title",
        wideTitle.Length > narrowTitle.Length,
        $"{narrowTitle.Length} characters on 38 mm, {wideTitle.Length} on 76 mm");

    Check("but never more of it than there is", wideTitle.Length <= book.Title.Length,
        $"\"{wideTitle}\"");
}

Heading("A spine label");
{
    var stock = new Stock(25, 38, Zpl.CommonDpi);

    var zpl = Zpl.Spine(book, stock);

    Check("it is the call number and nothing else",
        zpl.Contains("623.731 SIG") && !zpl.Contains("^BCN"), "623.731 SIG");

    Check("wrapped inside the width of the spine",
        Field(zpl[zpl.IndexOf("^FB", StringComparison.Ordinal)..], "^FB") <= stock.Across,
        $"^FB{Field(zpl[zpl.IndexOf("^FB", StringComparison.Ordinal)..], "^FB")} inside {stock.Across}");

    var origins = Origins(zpl).ToList();

    Check("nothing outside the label",
        origins.All(o => o.X >= 0 && o.X < stock.Across && o.Y >= 0 && o.Y < stock.Down),
        $"{origins.Count} fields, all inside");

    // A book with no call number still gets a spine label — with its accession
    // number, which is the only thing every copy is guaranteed to have.
    var unclassified = new LabelFor("JAKLI/000001645", "000001645", "Something", "");

    Check("a book with no call number falls back to its accession number",
        Zpl.Spine(unclassified, stock).Contains("JAKLI/000001645"), "the accession number");
}

Heading("The calibration label");
{
    var stock = new Stock(51, 25, Zpl.CommonDpi);

    var zpl = Zpl.Calibration(stock);

    // This is what replaces having the printer here. It draws a 10 mm square
    // from the same arithmetic as every other label, so a ruler settles it.
    Check("it draws a box", zpl.Contains("^GB"), "^GB");

    var box = Field(zpl[zpl.IndexOf("^GB", StringComparison.Ordinal)..], "^GB");

    Check("and the box really is 10 mm at 203 dpi",
        Math.Abs((box / stock.PerMm) - 10) < 0.15, $"{box} dots = {box / stock.PerMm:0.##} mm");

    var fine = new Stock(51, 25, Zpl.FineDpi);
    var fineZpl = Zpl.Calibration(fine);
    var fineBox = Field(fineZpl[fineZpl.IndexOf("^GB", StringComparison.Ordinal)..], "^GB");

    Check("and 10 mm at 300 dpi too, which is a different dot count",
        Math.Abs((fineBox / fine.PerMm) - 10) < 0.15 && fineBox != box,
        $"{fineBox} dots = {fineBox / fine.PerMm:0.##} mm");

    // It says what it was printed from, so a label held against a ruler is
    // enough to work out which setting to change.
    Check("it prints the numbers it was drawn from",
        zpl.Contains("51 x 25 mm") && zpl.Contains("203 dpi"), "51 x 25 mm, 203 dpi");

    Check("and nothing on it is off the label",
        Origins(zpl).All(o => o.X >= 0 && o.X < stock.Across && o.Y >= 0 && o.Y < stock.Down),
        "inside");
}

Heading("What cannot break a label");
{
    var stock = new Stock(51, 25, Zpl.CommonDpi);

    // ^ and ~ are ZPL's two command characters. A title containing one would
    // end the field early and print the rest of the title as instructions.
    var awkward = new LabelFor("A/1", "1", "Rockets ^ Missiles ~ Guns", "^~");

    var zpl = Zpl.Pocket(awkward, stock);

    Check("a title with a caret in it cannot become an instruction",
        !zpl.Contains("^ M"), "escaped");

    Check("nor one with a tilde", !zpl.Contains("~ G"), "escaped");

    Check("and the label is still one whole label",
        zpl.StartsWith("^XA") && zpl.TrimEnd().EndsWith("^XZ"), "^XA … ^XZ");

    Check("a spine label made only of control characters survives it",
        Zpl.Spine(awkward, new Stock(25, 38)).TrimEnd().EndsWith("^XZ"), "^XA … ^XZ");
}

Heading("A batch");
{
    var stock = new Stock(51, 25, Zpl.CommonDpi);

    var books = Enumerable.Range(1, 5)
        .Select(n => new LabelFor($"JAKLI/{n:000000000}", $"{n:000000000}", $"Book {n}", $"1{n}"))
        .ToList();

    var zpl = Zpl.Batch(books, LabelKind.Pocket, stock);

    Check("five books make five labels",
        zpl.Split("^XA").Length - 1 == 5 && zpl.Split("^XZ").Length - 1 == 5, "5 labels");

    Check("and every one of them is closed",
        zpl.Split("^XA").Length == zpl.Split("^XZ").Length, "balanced");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("Every dimension comes from the stock size and the printer's resolution. "
        + "What is left is one label and a ruler.");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

return failures == 0 ? 0 : 1;

static string Between(string text, string from, string to)
{
    var start = text.IndexOf(from, StringComparison.Ordinal);

    if (start < 0)
    {
        return "";
    }

    start += from.Length;

    var end = text.IndexOf(to, start, StringComparison.Ordinal);

    return end < 0 ? "" : text[start..end];
}
