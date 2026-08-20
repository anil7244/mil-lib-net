using MilLib.Core.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>
/// Any report, on paper.
///
/// One document for all of them, because a report is a title, some columns and
/// some rows — and six documents that each drew their own table would be six
/// chances for one of them to look like it came from a different application.
///
/// It turns itself sideways when there are enough columns to need it, so a wide
/// report is not squeezed into portrait and a narrow one does not sit in the
/// middle of a landscape sheet with half the paper empty.
/// </summary>
public class ReportDocument(Letterhead unit, Report report) : IDocument
{
    private bool Wide => report.Columns.Count >= 6;

    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            PrintStyle.Page(page, landscape: Wide);

            page.Header().Element(header =>
                PrintStyle.Head(header, unit, report.Title, report.Note is null ? null : ""));

            page.Content().PaddingTop(8).Column(column =>
            {
                if (report.Note is not null)
                {
                    column.Item().PaddingBottom(6)
                        .Text(report.Note)
                        .FontSize(PrintStyle.Small).FontColor(PrintStyle.Muted).Italic();
                }

                if (report.Rows.Count == 0)
                {
                    column.Item().PaddingTop(30).AlignCenter()
                        .Text("Nothing to report.")
                        .FontSize(11).FontColor(PrintStyle.Muted);

                    return;
                }

                column.Item().Element(Table);
            });

            page.Footer().Element(footer => PrintStyle.Foot(footer, report.Tally));
        });
    }

    private void Table(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < report.Columns.Count; i++)
                {
                    // A column of numbers needs only as much room as its
                    // heading; a column of titles needs whatever is left.
                    columns.RelativeColumn(Numeric(i) ? 1f : 2.2f);
                }
            });

            table.Header(header =>
            {
                for (var i = 0; i < report.Columns.Count; i++)
                {
                    var cell = header.Cell().Element(PrintStyle.HeadCell);

                    (Numeric(i) ? cell.AlignRight() : cell)
                        .Text(report.Columns[i].ToUpperInvariant())
                        .FontSize(PrintStyle.Small - 0.5f).SemiBold().LetterSpacing(0.04f);
                }
            });

            for (var r = 0; r < report.Rows.Count; r++)
            {
                var row = report.Rows[r];

                // A total row, where there is one, is the last row and says so
                // by being the only bold thing in the table.
                var isTotal = row.Count > 0
                    && r == report.Rows.Count - 1
                    && row[0].Equals("Total", StringComparison.Ordinal);

                for (var i = 0; i < report.Columns.Count; i++)
                {
                    var cell = table.Cell().Element(PrintStyle.Cell);

                    var text = (Numeric(i) ? cell.AlignRight() : cell)
                        .Text(i < row.Count ? row[i] : "")
                        .FontSize(PrintStyle.Body - 1);

                    if (isTotal)
                    {
                        text.SemiBold();
                    }

                    if (Numeric(i))
                    {
                        text.FontFamily(Fonts.Consolas);
                    }
                }
            }
        });
    }

    private bool Numeric(int column) =>
        report.RightAligned is not null
        && column < report.RightAligned.Count
        && report.RightAligned[column];
}
