using MilLib.Core.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>One book that was not on the shelf.</summary>
public record Shortage(string Accession, string Title, string State, decimal? Cost);

/// <summary>
/// The shortage statement — what a stock check produces for the board.
///
/// It is signed, so it carries a signature block, and it says on its face what
/// was counted, by whom, between which dates and against which authority. A
/// list of missing books with no provenance is a list somebody can argue with;
/// this is a document.
///
/// It does not write anything off. Deciding that a missing book is lost is a
/// board's decision and goes through the withdrawal register — this only says
/// what was and was not found.
/// </summary>
public class ShortageDocument(
    Letterhead unit,
    string checkName,
    string conductedBy,
    DateOnly startedOn,
    DateOnly? completedOn,
    string? boardReference,
    int expected,
    int found,
    IReadOnlyList<Shortage> missing,
    IReadOnlyList<string> notInRegister,
    string currency) : IDocument
{
    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            PrintStyle.Page(page);

            page.Header().Element(header =>
                PrintStyle.Head(header, unit, "Shortage Statement", checkName));

            page.Content().PaddingTop(10).Column(column =>
            {
                column.Item().Element(Particulars);

                column.Item().PaddingTop(14).Element(MissingTable);

                if (notInRegister.Count > 0)
                {
                    column.Item().PaddingTop(16).Element(Strangers);
                }

                column.Item().Element(c => PrintStyle.SignatureBlock(c,
                    "Conducted by", "Librarian", "Presiding Officer"));
            });

            page.Footer().Element(footer => PrintStyle.Foot(footer,
                $"{missing.Count} not found of {expected:N0} expected"));
        });
    }

    /// <summary>
    /// The facts of the count, in a block a board can read in one look: what,
    /// who, when, against what authority, and the four figures.
    /// </summary>
    private void Particulars(IContainer container)
    {
        container.Border(0.6f).BorderColor(PrintStyle.Rule).Padding(10).Column(column =>
        {
            column.Item().Row(row =>
            {
                Fact(row, "Conducted by", conductedBy);
                Fact(row, "Started", startedOn.ToString("dd MMM yyyy"));
                Fact(row, "Completed", completedOn?.ToString("dd MMM yyyy") ?? "not yet closed");
                Fact(row, "Board reference", string.IsNullOrWhiteSpace(boardReference) ? "—" : boardReference);
            });

            column.Item().PaddingTop(8).Row(row =>
            {
                Fact(row, "Expected on the shelf", expected.ToString("N0"));
                Fact(row, "Found", found.ToString("N0"));
                Fact(row, "Not found", missing.Count.ToString("N0"));

                var value = missing.Where(m => m.Cost is not null).Sum(m => m.Cost!.Value);

                Fact(row, "Value of shortage", value > 0 ? currency + value.ToString("N2") : "—");
            });
        });
    }

    private static void Fact(RowDescriptor row, string label, string value)
    {
        row.RelativeItem().Column(column =>
        {
            column.Item().Text(label.ToUpperInvariant())
                .FontSize(PrintStyle.Small - 1).FontColor(PrintStyle.Muted).LetterSpacing(0.06f);

            column.Item().PaddingTop(1).Text(value).FontSize(PrintStyle.Body + 0.5f).SemiBold();
        });
    }

    private void MissingTable(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4)
                .Text("NOT FOUND ON THE SHELF")
                .FontSize(PrintStyle.Small).SemiBold().LetterSpacing(0.1f);

            if (missing.Count == 0)
            {
                column.Item().PaddingVertical(10)
                    .Text("Every copy expected on the shelf was found.")
                    .FontSize(PrintStyle.Body).FontColor(PrintStyle.Muted);

                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(28);   // serial
                    columns.ConstantColumn(96);   // accession
                    columns.RelativeColumn();     // title
                    columns.ConstantColumn(76);   // state
                    columns.ConstantColumn(64);   // cost
                });

                table.Header(header =>
                {
                    Head(header, "S.No");
                    Head(header, "Accession No");
                    Head(header, "Title");
                    Head(header, "Register says");
                    Head(header, "Cost", right: true);
                });

                for (var i = 0; i < missing.Count; i++)
                {
                    var line = missing[i];

                    // Numbered, because a board minute refers to "items 4 to 7
                    // of the statement" and an unnumbered list cannot be
                    // referred to at all.
                    Cell(table, (i + 1).ToString("N0"), mono: true);
                    Cell(table, line.Accession, mono: true);
                    Cell(table, line.Title);
                    Cell(table, line.State);
                    Cell(table, line.Cost?.ToString("N2") ?? "", mono: true, right: true);
                }
            });
        });
    }

    /// <summary>
    /// Barcodes found on the shelf that the register has never heard of.
    ///
    /// Short, usually empty, and worth its own heading when it is not: a book
    /// on the shelf that the library has no record of is a different problem
    /// from a book the library has a record of and cannot find.
    /// </summary>
    private void Strangers(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4)
                .Text("ON THE SHELF BUT NOT IN THE REGISTER")
                .FontSize(PrintStyle.Small).SemiBold().LetterSpacing(0.1f);

            column.Item().Text(string.Join("    ", notInRegister))
                .FontSize(PrintStyle.Body - 0.5f).FontFamily(Fonts.Consolas);
        });
    }

    private static void Head(TableCellDescriptor header, string text, bool right = false)
    {
        var cell = header.Cell().Element(PrintStyle.HeadCell);

        (right ? cell.AlignRight() : cell)
            .Text(text.ToUpperInvariant())
            .FontSize(PrintStyle.Small - 0.5f).SemiBold().LetterSpacing(0.04f);
    }

    private static void Cell(TableDescriptor table, string text, bool mono = false, bool right = false)
    {
        var cell = table.Cell().Element(PrintStyle.Cell);

        var span = (right ? cell.AlignRight() : cell).Text(text).FontSize(PrintStyle.Body - 0.5f);

        if (mono)
        {
            span.FontFamily(Fonts.Consolas);
        }
    }
}
