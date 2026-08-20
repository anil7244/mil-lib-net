using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// A finished report: what it is called, its columns, and its rows as text.
///
/// Deliberately flat. A report is a thing that gets printed, filed and handed
/// to somebody — it has no behaviour, and giving each one its own shape would
/// mean six screens and six documents instead of one of each.
/// </summary>
public record Report(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? Note = null,
    IReadOnlyList<bool>? RightAligned = null)
{
    public static Report Empty(string title, string note) =>
        new(title, ["—"], [], note);

    public string Tally => Rows.Count == 1 ? "1 row" : $"{Rows.Count:N0} rows";
}

/// <summary>Which report, in the order the menu offers them.</summary>
public enum ReportKind
{
    Overdue,
    MemberActivity,
    Holdings,
    CopyStatus,
    Popular,
    Classified,
}

/// <summary>How a holdings report is grouped.</summary>
public enum HoldingsBy
{
    MaterialType,
    Classification,
    Subject,
}

/// <summary>Everything a report might need to be asked for.</summary>
public record ReportAsk(
    ReportKind Kind,
    string Member = "",
    HoldingsBy By = HoldingsBy.MaterialType,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>
/// The reports.
///
/// One rule runs through all of them and is the reason they live together: no
/// report may show material above the clearance of the person asking for it.
/// That gate is applied here, once, at the point the rows are read — not in six
/// separate places where five of them would be right and the sixth would be the
/// one somebody notices in a board of enquiry.
/// </summary>
public class Reports(MilLibDbContext db, Preferences preferences, SecurityClass clearance)
{
    public async Task<Report> RunAsync(ReportAsk ask, long byUserId, DateOnly today) => ask.Kind switch
    {
        ReportKind.Overdue => await OverdueAsync(today),
        ReportKind.MemberActivity => await MemberActivityAsync(ask.Member),
        ReportKind.Holdings => await HoldingsAsync(ask.By),
        ReportKind.CopyStatus => await CopyStatusAsync(),
        ReportKind.Popular => await PopularAsync(
            ask.From ?? today.AddMonths(-6), ask.To ?? today),
        ReportKind.Classified => await ClassifiedAsync(byUserId),
        _ => Report.Empty("Report", "That report does not exist."),
    };

    public static string Name(ReportKind kind) => kind switch
    {
        ReportKind.Overdue => "Overdue",
        ReportKind.MemberActivity => "Member activity",
        ReportKind.Holdings => "Holdings",
        ReportKind.CopyStatus => "Copies by state",
        ReportKind.Popular => "Most borrowed",
        ReportKind.Classified => "Classified holdings",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Which heading a report sits under — the same two the web application
    /// groups them beneath, so somebody moving between the two finds a report
    /// where they left it. Circulation is about the loans; catalogue is about
    /// the stock.
    /// </summary>
    public static string Section(ReportKind kind) => kind switch
    {
        ReportKind.Overdue or ReportKind.MemberActivity or ReportKind.Popular => "Circulation",
        _ => "Catalogue",
    };

    public static string Describe(ReportKind kind) => kind switch
    {
        ReportKind.Overdue => "Books past their due date and still out, oldest first.",
        ReportKind.MemberActivity => "One member's whole borrowing history.",
        ReportKind.Holdings => "What the library holds, counted by kind, classification or subject.",
        ReportKind.CopyStatus => "Where every copy is: on the shelf, out, at the binder, missing.",
        ReportKind.Popular => "The titles borrowed most often between two dates.",
        ReportKind.Classified => "Every classified copy and who is holding it. Looking at this is written down.",
        _ => "",
    };

    // ---------------------------------------------------------- the gate ----

    /// <summary>
    /// Titles this person may see. Everything else is built on top of it.
    /// </summary>
    private IQueryable<Title> Titles()
    {
        var allowed = clearance.UpTo();

        return db.Titles.Where(t => allowed.Contains(t.SecurityClass));
    }

    // -------------------------------------------------------- the reports ---

    private async Task<Report> OverdueAsync(DateOnly today)
    {
        var rows = await db.Loans
            .Where(l => (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE)
                     && l.DueOn < today)
            .OrderBy(l => l.DueOn)
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(Titles(), x => x.c.TitleId, t => t.TitleId, (x, t) => new { x.l, x.c, t })
            .Join(db.Members, x => x.l.MemberId, m => m.MemberId, (x, m) => new { x.l, x.c, x.t, m })
            .Join(db.MemberCategories, x => x.m.CategoryId, g => g.CategoryId,
                (x, g) => new { x.l, x.c, x.t, x.m, g })
            .ToListAsync();

        var lines = rows.Select(r =>
        {
            // The fine is worked out here rather than read from a table: it is
            // still accruing, and a report that quotes yesterday's figure is a
            // report somebody will be argued with about.
            var owed = FineCalculator.For(r.l, r.g, today);

            return (IReadOnlyList<string>)
            [
                r.m.Display,
                r.m.MembershipNo,
                r.t.Name,
                preferences.Accession(r.c.AccessionNo),
                r.l.DueOn.ToString("dd MMM yyyy"),
                owed.Days.ToString("N0"),
                owed.Amount > 0 ? preferences.Money(owed.Amount) : "",
            ];
        }).ToList();

        return new Report(
            "Overdue",
            ["Member", "Membership", "Title", "Accession", "Due", "Days late", "Fine"],
            lines,
            "The fine is worked out as this was printed, from the member's own category rates.",
            [false, false, false, false, false, true, true]);
    }

    private async Task<Report> MemberActivityAsync(string who)
    {
        who = who.Trim();

        if (who.Length == 0)
        {
            return Report.Empty("Member activity",
                "Give a membership number or a name to see that member's borrowing history.");
        }

        var like = $"%{who}%";

        var member = await db.Members
            .FirstOrDefaultAsync(m => m.MembershipNo == who || m.QrToken == who)
            ?? await db.Members
                .Where(m => EF.Functions.Like(m.FullName, like)
                         || EF.Functions.Like(m.PersonnelNo!, like))
                .OrderBy(m => m.FullName)
                .FirstOrDefaultAsync();

        if (member is null)
        {
            return Report.Empty("Member activity", $"Nobody matches “{who}”.");
        }

        var rows = await db.Loans
            .Where(l => l.MemberId == member.MemberId)
            .OrderByDescending(l => l.IssuedOn)
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(Titles(), x => x.c.TitleId, t => t.TitleId, (x, t) => new { x.l, x.c, t })
            .ToListAsync();

        return new Report(
            $"Member activity — {member.Display} ({member.MembershipNo})",
            ["Title", "Accession", "Issued", "Due", "Returned", "State"],
            [.. rows.Select(r => (IReadOnlyList<string>)
            [
                r.t.Name,
                preferences.Accession(r.c.AccessionNo),
                r.l.IssuedOn.ToString("dd MMM yyyy"),
                r.l.DueOn.ToString("dd MMM yyyy"),
                r.l.ReturnedOn?.ToString("dd MMM yyyy") ?? "—",
                Words.Of(r.l.Status),
            ])],
            rows.Count == 0 ? "This member has never borrowed anything." : null);
    }

    private async Task<Report> HoldingsAsync(HoldingsBy by)
    {
        var titles = Titles();

        List<(string Dimension, int Titles, int Copies)> counted;

        if (by == HoldingsBy.Subject)
        {
            // The copy counts are fetched separately and matched up here rather
            // than joined in. A title can carry several subjects, and joining
            // through the subject table would count its copies once per
            // subject — so a book filed under three headings would appear to be
            // three books.
            var copiesByTitle = await db.Copies
                .GroupBy(c => c.TitleId)
                .Select(g => new { TitleId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TitleId, x => x.Count);

            var filed = await db.TitleCategories
                .Join(titles, tc => tc.TitleId, t => t.TitleId, (tc, t) => new { tc, t })
                .Join(db.Categories, x => x.tc.CategoryId, c => c.CategoryId,
                    (x, c) => new { c.Name, x.t.TitleId })
                .ToListAsync();

            counted =
            [
                .. filed
                    .GroupBy(x => x.Name)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var ids = g.Select(x => x.TitleId).Distinct().ToList();

                        return (g.Key, ids.Count, ids.Sum(id => copiesByTitle.GetValueOrDefault(id)));
                    })
            ];
        }
        else
        {
            var rows = await titles
                .Select(t => new
                {
                    Dimension = by == HoldingsBy.Classification
                        ? t.SecurityClass.ToString()
                        : t.MaterialType.ToString(),
                    t.TitleId,
                    Copies = db.Copies.Count(c => c.TitleId == t.TitleId),
                })
                .ToListAsync();

            counted =
            [
                .. rows.GroupBy(r => r.Dimension)
                    .OrderBy(g => g.Key)
                    .Select(g => (g.Key, g.Count(), g.Sum(r => r.Copies)))
            ];
        }

        var heading = by switch
        {
            HoldingsBy.Classification => "Classification",
            HoldingsBy.Subject => "Subject",
            _ => "Kind",
        };

        var lines = counted
            .Select(c => (IReadOnlyList<string>)
                [Pretty(c.Dimension), c.Titles.ToString("N0"), c.Copies.ToString("N0")])
            .ToList();

        // The totals belong on the report. A table of counts whose total the
        // reader has to add up is a table that gets added up wrongly.
        if (lines.Count > 0)
        {
            lines.Add(["Total", counted.Sum(c => c.Titles).ToString("N0"),
                counted.Sum(c => c.Copies).ToString("N0")]);
        }

        return new Report(
            $"Holdings by {heading.ToLowerInvariant()}",
            [heading, "Titles", "Copies"],
            lines,
            null,
            [false, true, true]);
    }

    private async Task<Report> CopyStatusAsync()
    {
        var allowed = clearance.UpTo();

        var rows = await db.Copies
            .Where(c => db.Titles.Any(t => t.TitleId == c.TitleId && allowed.Contains(t.SecurityClass)))
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Copies = g.Count() })
            .ToListAsync();

        var lines = rows
            .OrderByDescending(r => r.Copies)
            .Select(r => (IReadOnlyList<string>)[Words.Of(r.Status), r.Copies.ToString("N0")])
            .ToList();

        if (lines.Count > 0)
        {
            lines.Add(["Total", rows.Sum(r => r.Copies).ToString("N0")]);
        }

        return new Report("Copies by state", ["State", "Copies"], lines, null, [false, true]);
    }

    private async Task<Report> PopularAsync(DateOnly from, DateOnly to)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MaxValue);

        var rows = await db.Loans
            .Where(l => l.IssuedOn >= start && l.IssuedOn <= end)
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(Titles(), x => x.c.TitleId, t => t.TitleId, (x, t) => new { x.l, t })
            .GroupBy(x => new { x.t.TitleId, x.t.Name })
            .Select(g => new { g.Key.Name, Issues = g.Count() })
            .OrderByDescending(x => x.Issues)
            .Take(100)
            .ToListAsync();

        return new Report(
            "Most borrowed",
            ["Title", "Times issued"],
            [.. rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Issues.ToString("N0")])],
            $"Between {from:dd MMM yyyy} and {to:dd MMM yyyy}. The hundred most borrowed.",
            [false, true]);
    }

    private async Task<Report> ClassifiedAsync(long byUserId)
    {
        // Written down before the rows are read.
        //
        // Who looked at the classified holdings, and when, is exactly the
        // question asked afterwards — and a note written only on success would
        // miss the attempt that failed halfway.
        await Journal.NoteAloneAsync(db, byUserId, "VIEW_CLASSIFIED", "report", null,
            new { report = "classified_holdings", cleared_to = clearance.ToString() });

        var allowed = clearance.UpTo();

        var rows = await db.Copies
            .Join(db.Titles.Where(t => t.SecurityClass != SecurityClass.UNCLASSIFIED
                                    && allowed.Contains(t.SecurityClass)),
                c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .OrderBy(x => x.c.AccessionSeq)
            .ToListAsync();

        var copyIds = rows.Select(r => r.c.CopyId).ToList();

        var holders = await db.Loans
            .Where(l => copyIds.Contains(l.CopyId)
                     && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .Join(db.Members, l => l.MemberId, m => m.MemberId, (l, m) => new { l.CopyId, m })
            .ToDictionaryAsync(x => x.CopyId, x => x.m.Display);

        return new Report(
            "Classified holdings",
            ["Accession", "Title", "Classification", "State", "Held by"],
            [.. rows.Select(r => (IReadOnlyList<string>)
            [
                preferences.Accession(r.c.AccessionNo),
                r.t.Name,
                Words.Of(r.t.SecurityClass),
                Words.Of(r.c.Status),
                holders.GetValueOrDefault(r.c.CopyId, "—"),
            ])],
            $"Limited to material at or below {Words.Of(clearance)} — your clearance. "
            + "Producing this report is written to the activity log.");
    }

    /// <summary>A stored word, said the way a person would say it.</summary>
    private static string Pretty(string stored) =>
        Enum.TryParse<SecurityClass>(stored, out var security) ? Words.Of(security)
        : Enum.TryParse<MaterialType>(stored, out var material) ? Words.Of(material)
        : stored;
}
