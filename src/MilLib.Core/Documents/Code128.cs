using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>
/// Code 128, drawn.
///
/// A barcode is a row of bars and gaps of four possible widths, and the whole
/// of the standard is the table below saying which widths spell which
/// character. Drawing it directly is a few dozen lines and produces vector bars
/// that stay sharp at any size — which matters, because a barcode rendered as a
/// picture and then scaled by a printer is a barcode a scanner refuses.
///
/// Subset B throughout. It covers every printable character, which means an
/// accession number reads the same whether the unit numbers its books 000002965
/// or JAKLI/1645 — and a symbology that silently changes shape depending on the
/// data is one that works in testing and fails on the one odd number.
/// </summary>
public static class Code128
{
    /// <summary>
    /// The standard. Each entry is six widths — bar, gap, bar, gap, bar, gap —
    /// except the stop, which has a seventh bar.
    /// </summary>
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104;

    private const int Stop = 106;

    /// <summary>
    /// The bar and gap widths for this text, in modules, starting with a bar
    /// and alternating.
    ///
    /// Anything outside the printable range is dropped rather than encoded
    /// wrongly. A barcode that scans back as something other than what is
    /// printed underneath it is worse than no barcode.
    /// </summary>
    public static IReadOnlyList<int> Widths(string text)
    {
        var values = new List<int> { StartB };

        foreach (var c in text)
        {
            if (c is >= (char)32 and <= (char)126)
            {
                values.Add(c - 32);
            }
        }

        // The check character: a weighted sum of everything before it. The
        // scanner recomputes it and refuses the read if it disagrees.
        var sum = StartB;

        for (var i = 1; i < values.Count; i++)
        {
            sum += values[i] * i;
        }

        values.Add(sum % 103);
        values.Add(Stop);

        var widths = new List<int>(values.Count * 6 + 1);

        foreach (var value in values)
        {
            foreach (var width in Patterns[value])
            {
                widths.Add(width - '0');
            }
        }

        return widths;
    }

    /// <summary>How many modules wide the whole symbol is.</summary>
    public static int Modules(string text) => Widths(text).Sum();

    /// <summary>
    /// Draw it into the space given, as tall as the space allows.
    ///
    /// The module width is worked out from the space rather than fixed, so the
    /// same call produces a readable code on a 51 mm pocket label and on a
    /// quarter of one — and the quiet zone either side, which scanners need and
    /// which people cut off, is part of the drawing rather than something the
    /// caller has to remember.
    /// </summary>
    public static void Draw(IContainer container, string text, float height)
    {
        var widths = Widths(text);

        container.Height(height).Row(row =>
        {
            // Ten modules of quiet zone, five each side. Less than that and a
            // scanner cannot find where the symbol starts — and it is the first
            // thing somebody trims off when a label looks too wide.
            row.RelativeItem(5);

            var bar = true;

            foreach (var width in widths)
            {
                var item = row.RelativeItem(width);

                if (bar)
                {
                    item.Background(QuestPDF.Helpers.Colors.Black);
                }

                bar = !bar;
            }

            row.RelativeItem(5);
        });
    }
}
