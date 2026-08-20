using MilLib.Core.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>One book being taken off the books.</summary>
public record Condemned(string Accession, string Title, string Acquired, decimal? Cost, string Remarks);

/// <summary>
/// The condemnation certificate — what a board signs when books leave the
/// library for good.
///
/// It exists to be filed and produced years later, so it says on its face what
/// was withdrawn, why, on whose authority, at what value, and who signed. The
/// list is numbered because a minute refers to "items 3 and 4 of the annexure",
/// and it carries the total because the value written off is the figure an
/// auditor asks for first.
/// </summary>
public class CondemnationDocument(
    Letterhead unit,
    Withdrawal withdrawal,
    string preparedBy,
    IReadOnlyList<Condemned> books,
    string currency) : IDocument
{
    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            PrintStyle.Page(page);

            page.Header().Element(header =>
                PrintStyle.Head(header, unit, "Certificate of Condemnation", withdrawal.WithdrawalNo));

            page.Content().PaddingTop(10).Column(column =>
            {
                column.Item().Element(Particulars);

                column.Item().PaddingTop(14).Element(Books);

                if (!string.IsNullOrWhiteSpace(withdrawal.Remarks))
                {
                    column.Item().PaddingTop(12).Column(remarks =>
                    {
                        remarks.Item().Text("REMARKS")
                            .FontSize(PrintStyle.Small).SemiBold().LetterSpacing(0.1f);

                        remarks.Item().PaddingTop(2).Text(withdrawal.Remarks)
                            .FontSize(PrintStyle.Body);
                    });
                }

                column.Item().PaddingTop(10).Element(Declaration);

                column.Item().Element(c => PrintStyle.SignatureBlock(c,
                    "Prepared by", "Librarian", "Presiding Officer", "Countersigned"));
            });

            page.Footer().Element(footer => PrintStyle.Foot(footer,
                $"{books.Count} {(books.Count == 1 ? "copy" : "copies")} · "
                + $"{currency}{books.Sum(b => b.Cost ?? 0):N2} written off"));
        });
    }

    private void Particulars(IContainer container)
    {
        container.Border(0.6f).BorderColor(PrintStyle.Rule).Padding(10).Column(column =>
        {
            column.Item().Row(row =>
            {
                Fact(row, "Withdrawal number", withdrawal.WithdrawalNo);
                Fact(row, "Date", withdrawal.WithdrawalDate.ToString("dd MMM yyyy"));
                Fact(row, "Reason", Words.Of(withdrawal.Reason));
                Fact(row, "Copies", books.Count.ToString("N0"));
            });

            column.Item().PaddingTop(8).Row(row =>
            {
                Fact(row, "Board proceedings", Or(withdrawal.BoardProceedings));
                Fact(row, "Sanction authority", Or(withdrawal.SanctionAuthority));
                Fact(row, "Sanction date", withdrawal.SanctionDate?.ToString("dd MMM yyyy") ?? "—");
                Fact(row, "Value written off",
                    currency + books.Sum(b => b.Cost ?? 0).ToString("N2"));
            });

            // Who drew it up, printed as well as signed for. A signature line
            // with nothing above it does not say whose signature is expected.
            column.Item().PaddingTop(8).Row(row => Fact(row, "Prepared by", preparedBy));
        });
    }

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static void Fact(RowDescriptor row, string label, string value)
    {
        row.RelativeItem().PaddingRight(10).Column(column =>
        {
            column.Item().Text(label.ToUpperInvariant())
                .FontSize(PrintStyle.Small - 1).FontColor(PrintStyle.Muted).LetterSpacing(0.06f);

            column.Item().PaddingTop(1).Text(value).FontSize(PrintStyle.Body + 0.5f).SemiBold();
        });
    }

    private void Books(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4)
                .Text("COPIES WITHDRAWN")
                .FontSize(PrintStyle.Small).SemiBold().LetterSpacing(0.1f);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(28);   // serial
                    columns.ConstantColumn(96);   // accession
                    columns.RelativeColumn(3);    // title
                    columns.RelativeColumn(1.4f); // how it was acquired
                    columns.ConstantColumn(64);   // cost
                });

                table.Header(header =>
                {
                    Head(header, "S.No");
                    Head(header, "Accession No");
                    Head(header, "Title");
                    Head(header, "Acquired");
                    Head(header, "Cost", right: true);
                });

                for (var i = 0; i < books.Count; i++)
                {
                    var book = books[i];

                    Cell(table, (i + 1).ToString("N0"), mono: true);
                    Cell(table, book.Accession, mono: true);
                    Cell(table, book.Title);
                    Cell(table, book.Acquired);
                    Cell(table, book.Cost?.ToString("N2") ?? "", mono: true, right: true);
                }

                // The total, on the table rather than only in the footer: this
                // is the line an auditor puts a pencil against.
                Total(table, "");
                Total(table, "");
                Total(table, "Total");
                Total(table, "");
                Total(table, books.Sum(b => b.Cost ?? 0).ToString("N2"), right: true);
            });
        });
    }

    /// <summary>
    /// The sentence that makes it a certificate rather than a list.
    ///
    /// Whoever signs is certifying something specific, and it should be written
    /// out above their name rather than assumed.
    /// </summary>
    private void Declaration(IContainer container)
    {
        var what = withdrawal.Reason switch
        {
            WithdrawalReason.LOST => "are not traceable and are written off as lost",
            WithdrawalReason.DAMAGED => "are damaged beyond economical repair",
            WithdrawalReason.OBSOLETE => "are obsolete and no longer of use to this library",
            WithdrawalReason.SUPERSEDED => "have been superseded by later editions",
            WithdrawalReason.TRANSFERRED => "have been transferred out of this library",
            _ => "are withdrawn from the library",
        };

        container.PaddingTop(6).Text(
            $"Certified that the {books.Count} {(books.Count == 1 ? "copy" : "copies")} listed above {what}, "
            + "and have been struck off the accession register accordingly. "
            + "The accession numbers are retired and will not be reissued.")
            .FontSize(PrintStyle.Body).LineHeight(1.35f);
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

    private static void Total(TableDescriptor table, string text, bool right = false)
    {
        var cell = table.Cell().Element(PrintStyle.Cell).PaddingTop(4);

        var span = (right ? cell.AlignRight() : cell).Text(text)
            .FontSize(PrintStyle.Body - 0.5f).SemiBold();

        if (right)
        {
            span.FontFamily(Fonts.Consolas);
        }
    }
}
