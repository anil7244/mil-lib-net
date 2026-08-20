using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>One book as the public catalogue shows it.</summary>
public record OnTheShelf(
    long TitleId,
    string Title,
    string Subtitle,
    string Authors,
    string Publisher,
    string CallNumber,
    string Language,
    int Copies,
    int Available)
{
    public bool In => Available > 0;

    /// <summary>
    /// Said the way somebody standing at a terminal wants it: whether they can
    /// walk to the shelf and take it, not a pair of numbers to work out.
    /// </summary>
    public string Standing => Copies == 0
        ? "Not on the shelves"
        : Available == 0
            ? Copies == 1 ? "The only copy is out" : $"All {Copies} copies are out"
            : Copies == 1 ? "On the shelf" : $"{Available} of {Copies} on the shelf";

    public string Where => CallNumber.Length > 0 ? CallNumber : "—";
}

/// <summary>One book somebody has out, as their own screen shows it.</summary>
public record MyBook(string Title, string Accession, DateOnly Due, int DaysLate)
{
    public bool Late => DaysLate > 0;

    public string When => Late
        ? $"was due {Due:dd MMM} — {DaysLate} day{(DaysLate == 1 ? "" : "s")} ago"
        : DaysLate == 0 ? $"due back today" : $"due back {Due:dd MMM}";
}

/// <summary>What the catalogue knows about the person standing at it.</summary>
public record WhoIsThere(
    Member Member,
    MemberCategory Category,
    IReadOnlyList<MyBook> Out,
    IReadOnlyList<string> Held,
    decimal Owed)
{
    public string Name => Member.Display;

    public SecurityClass Cleared => Member.EffectiveClearance(Category);

    public int MayStillTake => Math.Max(0, Category.MaxBooks - Out.Count);

    public bool AnythingLate => Out.Any(b => b.Late);
}

/// <summary>
/// The public catalogue — what a member sees on the terminal in the reading
/// room, with nobody from the library standing over them.
///
/// Two things make this different from every other screen in the application,
/// and both are about what it must not do.
///
/// It shows nothing classified to somebody who has not identified themselves.
/// An anonymous search runs at UNCLASSIFIED and cannot be talked out of it:
/// there is no box to type a clearance into, because the clearance comes from
/// the pass in somebody's hand and from nowhere else.
///
/// And it never shows one member anything about another. Everything personal is
/// scoped to whoever scanned in, and scanning in requires physically holding
/// that person's pass — which is the identification a reading room actually
/// has.
/// </summary>
public class ReadingRoom(MilLibDbContext db)
{
    /// <summary>How many results a terminal shows. Enough to choose from, few enough to read.</summary>
    public const int Most = 60;

    /// <summary>
    /// Search the catalogue, at a clearance.
    ///
    /// <paramref name="cleared"/> is UNCLASSIFIED for anybody who has not
    /// scanned a pass. A classified book is not merely hidden from the list —
    /// it is not in the query, so nothing about it can leak through a count or
    /// a total.
    /// </summary>
    public async Task<IReadOnlyList<OnTheShelf>> SearchAsync(
        string what, SecurityClass cleared = SecurityClass.UNCLASSIFIED)
    {
        var allowed = cleared.UpTo();

        var query = db.Titles.AsNoTracking()
            .Where(t => allowed.Contains(t.SecurityClass));

        what = what.Trim();

        if (what.Length > 0)
        {
            // Matched on the things somebody at a terminal actually knows: the
            // title, whoever wrote it, and the number on the spine.
            query = query.Where(t =>
                EF.Functions.Like(t.Name, $"%{what}%")
                || (t.Subtitle != null && EF.Functions.Like(t.Subtitle, $"%{what}%"))
                || (t.CallNumber != null && EF.Functions.Like(t.CallNumber, $"%{what}%"))
                || t.Authors.Any(a => EF.Functions.Like(a.Author!.Name, $"%{what}%")));
        }

        var rows = await query
            .OrderBy(t => t.Name)
            .Take(Most)
            .Select(t => new
            {
                t.TitleId,
                t.Name,
                t.Subtitle,
                t.CallNumber,
                t.Language,
                Publisher = t.Publisher!.Name,
                Authors = t.Authors.OrderBy(a => a.SortOrder).Select(a => a.Author!.Name).ToList(),
                Copies = t.Copies.Count(),
                Available = t.Copies.Count(c => c.Status == CopyStatus.AVAILABLE),
            })
            .ToListAsync();

        return
        [
            .. rows.Select(r => new OnTheShelf(
                r.TitleId,
                r.Name,
                r.Subtitle ?? "",
                r.Authors.Count > 0 ? string.Join(", ", r.Authors) : "",
                r.Publisher ?? "",
                r.CallNumber ?? "",
                r.Language,
                r.Copies,
                r.Available))
        ];
    }

    /// <summary>
    /// Who this is, from a scanned pass or a membership number.
    ///
    /// The same two things the counter's scan box resolves on, so a pass that
    /// works at the desk works here. Null when it is neither — the terminal
    /// says so rather than guessing.
    /// </summary>
    public async Task<WhoIsThere?> WhoAsync(string scanned, DateOnly today)
    {
        var value = scanned.Trim();

        if (value.Length == 0)
        {
            return null;
        }

        var member = await db.Members.AsNoTracking()
            .FirstOrDefaultAsync(m => m.QrToken == value || m.MembershipNo == value);

        // Only somebody currently on the roll. A member who has been posted out
        // is not refused rudely — the screen says to ask at the counter — but
        // their loans are not put on a public terminal either.
        if (member is null || member.Status != MemberStatus.ACTIVE)
        {
            return null;
        }

        var category = await db.MemberCategories.AsNoTracking()
            .FirstAsync(c => c.CategoryId == member.CategoryId);

        var loans = await db.Loans.AsNoTracking()
            .Where(l => l.MemberId == member.MemberId
                && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(db.Titles, x => x.c.TitleId, t => t.TitleId, (x, t) => new
            {
                t.Name,
                x.c.AccessionNo,
                x.l.DueOn,
            })
            .OrderBy(x => x.DueOn)
            .ToListAsync();

        var held = await db.Reservations.AsNoTracking()
            .Where(r => r.MemberId == member.MemberId
                && (r.Status == ReservationStatus.WAITING || r.Status == ReservationStatus.READY))
            .Join(db.Titles, r => r.TitleId, t => t.TitleId, (r, t) => new { t.Name, r.Status })
            .ToListAsync();

        var owed = await db.Fines.AsNoTracking()
            .Where(f => f.MemberId == member.MemberId && f.Status == FineStatus.PENDING)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;

        return new WhoIsThere(
            member,
            category,
            [
                .. loans.Select(l => new MyBook(
                    l.Name,
                    l.AccessionNo,
                    l.DueOn,
                    Math.Max(0, today.DayNumber - l.DueOn.DayNumber)))
            ],
            [
                .. held.Select(h => h.Status == ReservationStatus.READY
                    ? $"{h.Name} — waiting for you at the counter"
                    : $"{h.Name} — you are in the queue")
            ],
            owed);
    }

    /// <summary>
    /// Why this person cannot put a hold on this book, or null.
    ///
    /// Asked before the button is offered rather than after it is pressed. A
    /// terminal that accepts a request and then refuses it is a terminal
    /// somebody complains about at the counter.
    /// </summary>
    public async Task<string?> WhyNotHoldAsync(long titleId, WhoIsThere who, DateOnly today)
    {
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.TitleId == titleId);

        if (title is null)
        {
            return "That book is no longer in the catalogue.";
        }

        // The clearance gate again, at the moment of acting. Search already
        // excluded it, but a stale screen must not become a way round it.
        if (!who.Cleared.Allows(title.SecurityClass))
        {
            return "That book is not one you are cleared for. Ask at the counter.";
        }

        return await new Holds(db).WhyNotAsync(title, who.Member, who.Category);
    }

    /// <summary>Join the queue for a book that is out.</summary>
    public async Task<string> HoldAsync(long titleId, WhoIsThere who, DateOnly today)
    {
        var why = await WhyNotHoldAsync(titleId, who, today);

        if (why is not null)
        {
            return why;
        }

        var reservation = await new Holds(db).PlaceAsync(titleId, who.Member.MemberId);

        return reservation.QueuePosition <= 1
            ? "You are next for that book. The counter will keep it for you when it comes back."
            : $"You are number {reservation.QueuePosition} in the queue for that book.";
    }
}
