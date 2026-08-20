using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>Which label, and what goes on it.</summary>
public enum LabelKind
{
    /// <summary>The one inside the front cover, carrying the code and the title.</summary>
    Pocket,

    /// <summary>The one on the spine, carrying the call number and nothing else.</summary>
    Spine,
}

/// <summary>What the unit has chosen to print on a pocket label.</summary>
public enum LabelCode
{
    Barcode,
    Qr,
    Both,
}

/// <summary>One label's worth of book.</summary>
public record LabelFor(string Accession, string Barcode, string Title, string CallNumber);

/// <summary>
/// A sheet of labels for a plain office printer.
///
/// The thermal path exists too (see <see cref="Zpl"/>) and produces a better
/// label, but it needs a Zebra and calibrated stock. This is the path that
/// works in a unit library with one laser printer and a sheet of blank sticky
/// labels, which is most of them — so it is not the fallback, it is the normal
/// case, and it is laid out to be cut with a guillotine.
///
/// Every dimension comes from the settings, because label stock is bought
/// locally and no two units have the same.
/// </summary>
public class LabelSheetDocument(
    Letterhead unit,
    IReadOnlyList<LabelFor> books,
    LabelKind kind,
    LabelCode code,
    float widthMm,
    float heightMm,
    bool roll = false) : IDocument
{
    public void Compose(IDocumentContainer document)
    {
        // One label to a page, at the exact stock size. This is the universal
        // path: any label printer — thermal or otherwise — prints it through
        // its own Windows driver with the roll it was set up for. No printer
        // language is assumed, so it is not tied to one make.
        if (roll)
        {
            var w = widthMm * 72f / 25.4f;
            var h = heightMm * 72f / 25.4f;

            foreach (var book in books)
            {
                document.Page(page =>
                {
                    page.Size(w, h, Unit.Point);
                    page.Margin(0);
                    page.DefaultTextStyle(t => t.FontSize(8).FontColor(PrintStyle.Ink).FontFamily(Fonts.Calibri));
                    page.Content().Element(cell => Label(cell, book, frame: false));
                });
            }

            return;
        }

        document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(8, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(8).FontColor(PrintStyle.Ink).FontFamily(Fonts.Calibri));

            // A quiet line at the top saying which library and which stock this
            // was set for. It is the first thing anybody checks when a sheet
            // comes out misaligned.
            page.Header().PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Text($"{unit.Organisation} — {Words()} labels")
                    .FontSize(7.5f).FontColor(PrintStyle.Muted);

                row.RelativeItem().AlignRight()
                    .Text($"{widthMm:0.#} × {heightMm:0.#} mm · {books.Count} label{(books.Count == 1 ? "" : "s")}")
                    .FontSize(7.5f).FontColor(PrintStyle.Muted);
            });

            page.Content().Element(Sheet);
        });
    }

    private string Words() => kind == LabelKind.Spine ? "Spine" : "Pocket";

    private void Sheet(IContainer container)
    {
        var width = widthMm * 72f / 25.4f;
        var height = heightMm * 72f / 25.4f;

        container.Column(column =>
        {
            column.Spacing(2);

            // Laid out as an ordinary wrapping row rather than a fixed grid, so
            // a change of stock size changes how many fit across without
            // anybody having to work out the number.
            column.Item().Inlined(inlined =>
            {
                inlined.Spacing(2);
                inlined.BaselineTop();

                foreach (var book in books)
                {
                    inlined.Item().Width(width).Height(height).Element(cell =>
                        Label(cell, book));
                }
            });
        });
    }

    private void Label(IContainer container, LabelFor book, bool frame = true)
    {
        // A hairline round the label on a sheet — the cutting line — but not on
        // a roll, where the printer already separates one label from the next.
        var framed = (frame
                ? container.Border(0.4f).BorderColor(PrintStyle.Rule)
                : container)
            .Padding(4);

        if (kind == LabelKind.Spine)
        {
            Spine(framed, book);
            return;
        }

        Pocket(framed, book);
    }

    /// <summary>
    /// The spine label: the call number, large, and nothing else.
    ///
    /// It is read from six feet away by somebody walking along a shelf. Anything
    /// else on it is something they have to look past.
    /// </summary>
    private static void Spine(IContainer container, LabelFor book)
    {
        var mark = book.CallNumber.Length > 0 ? book.CallNumber : book.Accession;

        container.AlignMiddle().AlignCenter()
            .Text(mark)
            .FontSize(13).SemiBold()
            .LineHeight(1.1f);
    }

    /// <summary>
    /// How tall the bars can be on this stock.
    ///
    /// Worked out from the label rather than fixed, because bar height is what
    /// a scanner has to aim at — a short code on a tall label is a code the
    /// operator has to line up carefully, over and over, all day. What is left
    /// after the padding, the title and the number underneath belongs to the
    /// bars.
    /// </summary>
    private float BarHeight
    {
        get
        {
            var labelHeight = heightMm * 72f / 25.4f;

            return Math.Clamp(labelHeight - 32f, 16f, 64f);
        }
    }

    private void Pocket(IContainer container, LabelFor book)
    {
        container.Column(column =>
        {
            column.Item().Text(book.Title)
                .FontSize(6.5f).LineHeight(0.95f).ClampLines(2);

            column.Item().PaddingTop(2).Row(row =>
            {
                if (code is LabelCode.Barcode or LabelCode.Both)
                {
                    row.RelativeItem().Column(bars =>
                    {
                        bars.Item().Element(e => Code128.Draw(e, book.Barcode, BarHeight));

                        // Printed underneath, always. A barcode nobody can read
                        // by eye is a barcode that cannot be checked when the
                        // scanner is out of order.
                        bars.Item().PaddingTop(1).AlignCenter()
                            .Text(book.Accession)
                            .FontSize(7).SemiBold().FontFamily(Fonts.Consolas);
                    });
                }

                if (code is LabelCode.Qr or LabelCode.Both)
                {
                    if (code == LabelCode.Both)
                    {
                        row.ConstantItem(3);
                    }

                    // Square, and as tall as the bars beside it would be, so
                    // the two codes sit on the same baseline rather than one
                    // floating above the other.
                    var side = code == LabelCode.Both ? BarHeight : BarHeight + 8;

                    row.ConstantItem(side).Column(qr =>
                    {
                        qr.Item().Width(side).Height(side).Image(QrFor(book.Barcode)).FitArea();

                        if (code == LabelCode.Qr)
                        {
                            qr.Item().PaddingTop(1).AlignCenter()
                                .Text(book.Accession)
                                .FontSize(7).SemiBold().FontFamily(Fonts.Consolas);
                        }
                    });
                }
            });
        });
    }

    /// <summary>
    /// The QR, as a picture.
    ///
    /// Error correction M and a quiet border of four modules: a label on a book
    /// gets scuffed, and a code that only scans while it is pristine is a code
    /// that stops working in a month.
    /// </summary>
    private static byte[] QrFor(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);

        return new PngByteQRCode(data).GetGraphic(6, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
    }
}
