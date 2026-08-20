using MilLib.Core.Data;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MilLib.Core.Documents;

/// <summary>One member's pass, as it is printed.</summary>
public record PassFor(
    string MembershipNo,
    string FullName,
    string? Rank,
    string? PersonnelNo,
    string? UnitCoy,
    DateOnly? ValidUpto,
    SecurityClass Clearance,
    string QrToken,
    string? PhotoPath)
{
    /// <summary>Two letters, for when there is no photograph — which is most of them.</summary>
    public string Initials
    {
        get
        {
            var words = FullName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(2)
                .Select(w => char.ToUpperInvariant(w[0]));

            var letters = string.Concat(words);

            return letters.Length > 0 ? letters : "—";
        }
    }
}

/// <summary>
/// The pass a member carries.
///
/// Printed at CR80 — 85.6 × 54 mm, the size of every bank card and every ID
/// card holder in existence — so a unit can put it in a laminating pouch
/// bought anywhere. Laid out to be cut with a guillotine, with a dashed line
/// round each one, because that is how it will actually be done.
///
/// What is on it is decided by one question: what does the counter need when
/// somebody is standing at it? A photograph and a name to check the person, a
/// membership number to type when the scanner will not read, the clearance so
/// nobody has to look it up, and the QR the scanner reads. Nothing else earns
/// its space on a card this size.
///
/// The QR encodes the member's <c>qr_token</c> — the same value the scan box
/// resolves on — and not the membership number. The token can be reissued when
/// a pass is lost, which makes every printed copy of the old one useless; a
/// number cannot.
/// </summary>
public class PassDocument(Letterhead unit, IReadOnlyList<PassFor> members) : IDocument
{
    /// <summary>CR80, in points. The size of a bank card.</summary>
    private const float CardWidth = 85.6f * 72f / 25.4f;

    private const float CardHeight = 54f * 72f / 25.4f;

    private static float Mm(float mm) => mm * 72f / 25.4f;

    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(12, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(7).FontColor(PrintStyle.Ink).FontFamily(Fonts.Calibri));

            // A quiet line saying what this sheet is. It is what somebody
            // reaches for when a page of passes turns up in a tray.
            page.Header().PaddingBottom(6).Row(row =>
            {
                row.RelativeItem()
                    .Text($"{unit.Organisation} — library passes")
                    .FontSize(7.5f).FontColor(PrintStyle.Muted);

                row.RelativeItem().AlignRight()
                    .Text($"{members.Count} pass{(members.Count == 1 ? "" : "es")} · "
                        + "85.6 × 54 mm · cut on the dashed line")
                    .FontSize(7.5f).FontColor(PrintStyle.Muted);
            });

            page.Content().Element(Sheet);
        });
    }

    private void Sheet(IContainer container)
    {
        container.Inlined(inlined =>
        {
            inlined.Spacing(Mm(6));
            inlined.VerticalSpacing(Mm(6));

            foreach (var member in members)
            {
                inlined.Item()
                    .Width(CardWidth)
                    .Height(CardHeight)
                    // The cutting guide, drawn round the card rather than on
                    // it, so the line disappears with the trim.
                    .Border(0.4f)
                    .BorderColor(PrintStyle.Rule)
                    .Element(card => Card(card, member));
            }
        });
    }

    // The card, in millimetres, adding to exactly 54.
    //
    // Fixed rather than flowed. Asking the body to take "whatever is left"
    // reads well and does not work here: it takes everything there is, the
    // classification band is pushed off the bottom edge and clipped, and every
    // pass on the sheet comes out with no classification on it and nothing
    // saying so. Three numbers that add up are worth more than a layout that
    // is supposed to work it out.
    private const float AccentMm = 1.4f;

    private const float HeadMm = 11.2f;

    private const float BandMm = 3.6f;

    private const float BodyMm = 54f - AccentMm - HeadMm - BandMm;

    private void Card(IContainer container, PassFor member)
    {
        container.Column(column =>
        {
            // A band of the unit's accent across the top. The one piece of
            // colour on the card, and what makes a genuine pass recognisable
            // from across a counter.
            column.Item().Height(Mm(AccentMm)).Background(unit.Accent);

            column.Item().Height(Mm(HeadMm)).Element(Head);

            column.Item().Height(Mm(BodyMm))
                .PaddingHorizontal(Mm(2.6f)).PaddingVertical(Mm(1.6f))
                .Element(body => Body(body, member));

            column.Item().Height(Mm(BandMm)).Element(foot => Clearance(foot, member));
        });
    }

    private void Head(IContainer container)
    {
        container
            .BorderBottom(0.25f).BorderColor(PrintStyle.Rule)
            .PaddingHorizontal(Mm(2.6f)).PaddingVertical(Mm(1.2f))
            .Row(row =>
            {
                if (unit.CrestPath is not null && File.Exists(unit.CrestPath))
                {
                    row.ConstantItem(Mm(8.5f)).Height(Mm(8.5f)).AlignMiddle()
                        .Image(unit.CrestPath).FitArea();

                    row.ConstantItem(Mm(2.2f));
                }

                row.RelativeItem().AlignMiddle().Column(text =>
                {
                    text.Item().Text(unit.Organisation.ToUpperInvariant())
                        .FontSize(6.4f).SemiBold().LetterSpacing(0.05f);

                    text.Item().Text("LIBRARY PASS")
                        .FontSize(5.6f).FontColor(PrintStyle.Muted).LetterSpacing(0.16f);
                });
            });
    }

    private void Body(IContainer container, PassFor member)
    {
        container.Row(row =>
        {
            // The photograph, or the initials in its place. A grey rectangle
            // saying "no image" would be worse than either.
            row.ConstantItem(Mm(17)).Height(Mm(26)).Element(photo => Photo(photo, member));

            row.ConstantItem(Mm(2.4f));

            row.RelativeItem().Column(fields =>
            {
                fields.Item().Text(member.FullName)
                    .FontSize(9).Bold().LineHeight(1.1f);

                if (!string.IsNullOrWhiteSpace(member.Rank))
                {
                    fields.Item().PaddingTop(Mm(0.4f)).Text(member.Rank)
                        .FontSize(6).FontColor(PrintStyle.Muted);
                }

                fields.Item().PaddingTop(Mm(1.6f)).Element(f =>
                    Fact(f, "Membership", member.MembershipNo));

                if (!string.IsNullOrWhiteSpace(member.PersonnelNo))
                {
                    fields.Item().Element(f => Fact(f, "Personnel", member.PersonnelNo));
                }

                if (!string.IsNullOrWhiteSpace(member.UnitCoy))
                {
                    fields.Item().Element(f => Fact(f, "Unit", member.UnitCoy));
                }

                fields.Item().Element(f => Fact(f, "Valid upto",
                    member.ValidUpto?.ToString("dd MMM yyyy") ?? "—"));
            });

            row.ConstantItem(Mm(2));

            // On white, always. The quiet zone round a QR is part of the code,
            // and a scanner reading it off a tinted card is a scanner that
            // sometimes does not.
            row.ConstantItem(Mm(21)).Height(Mm(21)).AlignTop()
                .Background("#FFFFFF").Padding(Mm(0.6f))
                .Image(QrFor(member.QrToken)).FitArea();
        });
    }

    private static void Photo(IContainer container, PassFor member)
    {
        var frame = container.Border(0.25f).BorderColor(PrintStyle.Rule);

        if (member.PhotoPath is not null && File.Exists(member.PhotoPath))
        {
            frame.Image(member.PhotoPath).FitArea();

            return;
        }

        frame.Background(PrintStyle.HeadFill).AlignCenter().AlignMiddle()
            .Text(member.Initials)
            .FontSize(15).SemiBold().FontColor(PrintStyle.Muted);
    }

    private static void Fact(IContainer container, string label, string? value)
    {
        container.PaddingTop(Mm(1.5f)).Row(row =>
        {
            row.RelativeItem().Text(label)
                .FontSize(6).FontColor(PrintStyle.Muted).LetterSpacing(0.04f);

            row.AutoItem().Text(value ?? "—").FontSize(6.8f).SemiBold();
        });
    }

    /// <summary>
    /// The clearance, across the foot, in words.
    ///
    /// It is on the card because the alternative is somebody at the counter
    /// looking it up, and the whole point of a pass is that the answer is in
    /// the hand already. An unclassified pass says so rather than saying
    /// nothing, so a blank band never has to be interpreted.
    /// </summary>
    private void Clearance(IContainer container, PassFor member)
    {
        var classified = member.Clearance != SecurityClass.UNCLASSIFIED;

        container
            .Background(classified ? unit.Accent : PrintStyle.HeadFill)
            .PaddingHorizontal(Mm(2.6f)).AlignMiddle()
            .Row(row =>
            {
                row.RelativeItem().Text(Words.Of(member.Clearance).ToUpperInvariant())
                    .FontSize(5.6f).Bold().LetterSpacing(0.18f)
                    .FontColor(classified ? "#FFFFFF" : PrintStyle.Muted);

                row.AutoItem().Text(unit.LibraryName)
                    .FontSize(5).FontColor(classified ? "#FFFFFF" : PrintStyle.Muted);
            });
    }

    /// <summary>
    /// The QR, drawn small but at a high module count, because it is printed at
    /// 17 mm and read off a laminated card under a strip light.
    /// </summary>
    private static byte[] QrFor(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);

        return new PngByteQRCode(data).GetGraphic(10, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
    }
}
