using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>
/// What the unit's name looks like on paper.
///
/// Every document the library produces — the register, a no-dues chit, a
/// shortage report, a stock board's proceedings — is headed the same way and
/// footed the same way. That is not decoration: a page that turns up in a file
/// six months later has to say on its face which library it came from, when it
/// was produced, and that nobody has since added a page to the middle of it.
/// </summary>
public record Letterhead(
    string Organisation,
    string LibraryName,
    string Motto,
    string? CrestPath,
    string Accent);

public static class PrintStyle
{
    /// <summary>Everything on the page is one of these three sizes.</summary>
    public const float Body = 9.5f;

    public const float Small = 8f;

    public const float Heading = 14f;

    /// <summary>
    /// Black on white, and one accent.
    ///
    /// A register is photocopied, faxed and filed. Anything that depends on
    /// colour to be read stops being readable the first time somebody runs it
    /// through a machine, so colour is spent only on the rule under the
    /// letterhead and never on a value.
    /// </summary>
    public const string Ink = "#111111";

    public const string Muted = "#5A5A5A";

    public const string Rule = "#B8B0A2";

    public const string HeadFill = "#EDE9DF";

    public static void Page(PageDescriptor page, bool landscape = false)
    {
        page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
        page.Margin(14, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontSize(Body).FontColor(Ink).FontFamily(Fonts.Calibri));
    }

    /// <summary>
    /// The head of the page: the crest, the unit, and what this document is.
    ///
    /// Repeated on every page rather than only the first, because a register is
    /// read as loose sheets far more often than as a bound run.
    /// </summary>
    public static void Head(IContainer container, Letterhead unit, string document, string? range = null)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (unit.CrestPath is not null && File.Exists(unit.CrestPath))
                {
                    row.ConstantItem(46).Height(46).AlignMiddle().Image(unit.CrestPath).FitArea();
                    row.ConstantItem(12);
                }

                row.RelativeItem().AlignMiddle().Column(text =>
                {
                    text.Item().Text(unit.Organisation.ToUpperInvariant())
                        .FontSize(Heading).SemiBold().LetterSpacing(0.08f);

                    text.Item().Text(unit.LibraryName)
                        .FontSize(Small).FontColor(Muted).LetterSpacing(0.12f);

                    if (unit.Motto.Length > 0)
                    {
                        text.Item().Text(unit.Motto.ToUpperInvariant())
                            .FontSize(Small - 0.5f).FontColor(unit.Accent).LetterSpacing(0.16f);
                    }
                });

                row.ConstantItem(220).AlignMiddle().AlignRight().Column(text =>
                {
                    text.Item().AlignRight().Text(document.ToUpperInvariant())
                        .FontSize(11).SemiBold().LetterSpacing(0.14f);

                    if (!string.IsNullOrWhiteSpace(range))
                    {
                        text.Item().AlignRight().Text(range).FontSize(Small).FontColor(Muted);
                    }
                });
            });

            column.Item().PaddingTop(6).LineHorizontal(1.2f).LineColor(unit.Accent);
        });
    }

    /// <summary>
    /// The foot: when it was produced, and which page of how many.
    ///
    /// The page count is the part that matters. "Page 3" on its own says
    /// nothing about whether pages 4 and 5 are missing from the file.
    /// </summary>
    public static void Foot(IContainer container, string tally)
    {
        container.PaddingTop(4).Column(column =>
        {
            column.Item().LineHorizontal(0.5f).LineColor(Rule);

            column.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(tally).FontSize(Small).FontColor(Muted);

                row.RelativeItem().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(Small).FontColor(Muted));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });

                row.RelativeItem().AlignRight()
                    .Text($"Produced {DateTime.Now:dd MMM yyyy HH:mm}")
                    .FontSize(Small).FontColor(Muted);
            });
        });
    }

    /// <summary>A column heading in a ruled table.</summary>
    public static IContainer HeadCell(IContainer container) =>
        container
            .Background(HeadFill)
            .BorderBottom(1).BorderColor(Rule)
            .PaddingVertical(3).PaddingHorizontal(4);

    /// <summary>A body cell. Ruled on every side, the way a ledger is ruled.</summary>
    public static IContainer Cell(IContainer container) =>
        container
            .BorderBottom(0.5f).BorderColor(Rule)
            .PaddingVertical(2f).PaddingHorizontal(4);

    /// <summary>
    /// Where somebody has to sign.
    ///
    /// A line and a name under it, with room above to write. Any document that
    /// leaves the library and comes back needs one, and it should be the same
    /// shape on all of them.
    /// </summary>
    public static void SignatureBlock(IContainer container, params string[] roles)
    {
        container.PaddingTop(28).Row(row =>
        {
            foreach (var role in roles)
            {
                row.RelativeItem().PaddingRight(24).Column(column =>
                {
                    column.Item().PaddingTop(26).LineHorizontal(0.7f).LineColor(Ink);
                    column.Item().PaddingTop(3).Text(role).FontSize(Small).FontColor(Muted);
                });
            }
        });
    }
}
