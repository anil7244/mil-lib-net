using MilLib.Core.Data;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>One line of the register — a copy, and the work it is a copy of.</summary>
public record RegisterEntry(
    string Accession,

    /// <summary>
    /// The raw number underneath the printed one. Not shown — the printed form
    /// is what the unit calls it — but it is what the register is ordered by,
    /// and the imported numbers are unpadded, so sorting on the printed string
    /// would put 1000 before 999.
    /// </summary>
    int? Seq,

    string? BookNo,
    DateOnly AccessionedOn,
    string Author,
    string Title,
    string? Ledger,
    string? Publisher,
    int? Year,
    string? Pages,
    CopySource Source,
    string? BillNo,
    decimal? Cost,
    string? Classification,
    CopyStatus Status);

/// <summary>
/// The accession register: the library's legal ledger of every physical book it
/// has ever taken on.
///
/// Landscape, ruled, and strictly in accession order — the order the numbers
/// were handed out, which is the only order this document is allowed to be in.
/// It is produced, never edited: a correction to an entry is an annotation on
/// the copy, and it appears in the remarks column rather than replacing what was
/// originally written.
/// </summary>
public class AccessionRegisterDocument(
    Letterhead unit,
    IReadOnlyList<RegisterEntry> entries,
    string range,
    string currency) : IDocument
{
    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            PrintStyle.Page(page, landscape: true);

            page.Header().Element(header => PrintStyle.Head(header, unit, "Accession Register", range));

            page.Content().PaddingTop(8).Element(Table);

            page.Footer().Element(footer => PrintStyle.Foot(footer, Tally()));
        });
    }

    private string Tally()
    {
        var value = entries.Where(e => e.Cost is not null).Sum(e => e.Cost!.Value);

        var counted = entries.Count == 1 ? "1 entry" : $"{entries.Count:N0} entries";

        // The total is what a board asks for first, so it goes in the foot of
        // every page rather than only at the end of the run.
        return value > 0
            ? $"{counted} · {currency}{value:N2} of stock"
            : counted;
    }

    private void Table(IContainer container)
    {
        container.Table(table =>
        {
            // Fourteen columns on one sheet, so every width is fought over.
            //
            // Two rules decided these. A column must be wide enough for its own
            // heading — "REMARKS" broke across two lines and read as "REMARK
            // S". And a column must fit the value it usually holds on one line:
            // the date wrapped mid-year to "31/07/20 / 26", and the ledger name
            // took three lines, which made every row in the register three
            // lines tall and turned forty-three entries into five pages.
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(76);   // accession no
                columns.ConstantColumn(44);   // book no
                columns.ConstantColumn(58);   // date — fits dd/MM/yyyy whole
                columns.RelativeColumn(1.5f); // author
                columns.RelativeColumn(2.9f); // title
                columns.RelativeColumn(2.4f); // ledger — long names, given room
                columns.RelativeColumn(1.5f); // publisher
                columns.ConstantColumn(28);   // year
                columns.ConstantColumn(32);   // pages
                columns.ConstantColumn(50);   // source
                columns.ConstantColumn(48);   // bill no
                columns.ConstantColumn(50);   // cost
                columns.ConstantColumn(46);   // classification
                columns.RelativeColumn(1.2f); // remarks
            });

            // Repeated at the top of every page. A ledger sheet without its
            // column headings is a grid of numbers nobody can read.
            table.Header(header =>
            {
                Head(header, "Accession No");
                Head(header, "Book No");
                Head(header, "Date");
                Head(header, "Author");
                Head(header, "Title");
                Head(header, "Ledger");
                Head(header, "Publisher");
                Head(header, "Year");
                Head(header, "Pages");
                Head(header, "Source");
                Head(header, "Bill No");
                Head(header, "Cost", right: true);
                Head(header, "Class No");
                Head(header, "Remarks");
            });

            foreach (var entry in entries)
            {
                Cell(table, entry.Accession, mono: true);
                Cell(table, entry.BookNo, mono: true);
                Cell(table, entry.AccessionedOn.ToString("dd/MM/yyyy"), mono: true);
                Cell(table, entry.Author);
                Cell(table, entry.Title);
                Cell(table, entry.Ledger, small: true);
                Cell(table, entry.Publisher);
                Cell(table, entry.Year?.ToString(), mono: true);
                Cell(table, entry.Pages);
                Cell(table, Words.Of(entry.Source));
                Cell(table, entry.BillNo, mono: true);
                Cell(table, entry.Cost?.ToString("N2"), mono: true, right: true);
                Cell(table, entry.Classification, mono: true);

                // A copy that is simply on the shelf has nothing to remark on.
                // Only a book that is out, missing, withdrawn or at the binder
                // earns a word here — which is what makes the column worth
                // reading down.
                Cell(table, entry.Status == CopyStatus.AVAILABLE ? null : Words.Of(entry.Status));
            }
        });
    }

    private static void Head(TableCellDescriptor header, string text, bool right = false)
    {
        var cell = header.Cell().Element(PrintStyle.HeadCell);

        (right ? cell.AlignRight() : cell)
            .Text(text.ToUpperInvariant())
            .FontSize(PrintStyle.Small - 0.7f).SemiBold().LetterSpacing(0.04f);
    }

    private static void Cell(
        TableDescriptor table, string? text, bool mono = false, bool right = false, bool small = false)
    {
        var cell = table.Cell().Element(PrintStyle.Cell);

        var span = (right ? cell.AlignRight() : cell).Text(text ?? "")
            .FontSize(small ? PrintStyle.Body - 2 : PrintStyle.Body - 1);

        if (mono)
        {
            span.FontFamily(QuestPDF.Helpers.Fonts.Consolas);
        }
    }
}
