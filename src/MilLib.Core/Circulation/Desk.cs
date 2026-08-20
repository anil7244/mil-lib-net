using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>A copy and the work it is a copy of, loaded together.</summary>
public record ScannedCopy(Copy Copy, Title Title, Loan? OnLoan, Member? HeldBy)
{
    public bool IsOut => OnLoan is not null;
}

/// <summary>A member and the rules that apply to them.</summary>
public record ScannedMember(Member Member, MemberCategory Category);

/// <summary>
/// What the scan turned out to be.
///
/// One box at the counter takes everything — a member's pass, a book's barcode,
/// an accession number somebody typed, or part of a name. Asking the operator
/// to say which of those they are about to scan is asking them to do the
/// computer's job while somebody waits.
/// </summary>
public abstract record Scan
{
    public sealed record Book(ScannedCopy Copy) : Scan;

    public sealed record Person(ScannedMember Member) : Scan;

    /// <summary>Several people match what was typed; the operator picks.</summary>
    public sealed record Several(IReadOnlyList<Member> Matches, string Query) : Scan;

    public sealed record Unknown(string Value) : Scan;
}

/// <summary>
/// Working out what was just scanned.
///
/// A copy is looked for first. Barcodes and accession numbers are exact and
/// unique, so a hit is certain — whereas a member lookup falls back to
/// searching names, which can match almost anything. Getting that order wrong
/// makes a book whose accession number happens to appear in somebody's phone
/// number resolve to the wrong thing.
/// </summary>
public class Desk(MilLibDbContext db, Preferences preferences)
{
    public async Task<Scan> ResolveAsync(string value)
    {
        value = value.Trim();

        if (value.Length == 0)
        {
            return new Scan.Unknown("");
        }

        if (await FindCopyAsync(value) is { } copy)
        {
            return new Scan.Book(copy);
        }

        var exact = await db.Members
            .FirstOrDefaultAsync(m => m.QrToken == value || m.MembershipNo == value);

        if (exact is not null)
        {
            return new Scan.Person(await WithCategoryAsync(exact));
        }

        var matches = await SearchMembersAsync(value);

        return matches.Count switch
        {
            0 => new Scan.Unknown(value),
            1 => new Scan.Person(await WithCategoryAsync(matches[0])),
            _ => new Scan.Several(matches, value),
        };
    }

    public async Task<ScannedCopy?> FindCopyAsync(string value)
    {
        value = value.Trim();

        if (value.Length == 0)
        {
            return null;
        }

        var copy = await db.Copies
            .FirstOrDefaultAsync(c => c.Barcode == value || c.AccessionNo == value);

        // Somebody reading a number off a label reads the prefix with it. The
        // prefix is how the unit says the number out loud; it is not part of
        // what is stored, and typing it should still find the book.
        if (copy is null && preferences.AccessionPrefix.Length > 0
            && value.StartsWith(preferences.AccessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = value[preferences.AccessionPrefix.Length..];

            copy = await db.Copies
                .FirstOrDefaultAsync(c => c.AccessionNo == bare || c.Barcode == bare);
        }

        if (copy is null)
        {
            return null;
        }

        var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

        var loan = await db.Loans
            .Where(l => l.CopyId == copy.CopyId
                     && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .OrderByDescending(l => l.LoanId)
            .FirstOrDefaultAsync();

        var holder = loan is null
            ? null
            : await db.Members.FirstOrDefaultAsync(m => m.MemberId == loan.MemberId);

        return new ScannedCopy(copy, title, loan, holder);
    }

    public async Task<ScannedMember?> FindMemberAsync(long memberId)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.MemberId == memberId);

        return member is null ? null : await WithCategoryAsync(member);
    }

    /// <summary>
    /// What this member currently holds, oldest loan first — which is the order
    /// in which they become a problem.
    /// </summary>
    public async Task<List<(Loan Loan, Copy Copy, Title Title)>> OpenLoansAsync(long memberId)
    {
        var rows = await db.Loans
            .Where(l => l.MemberId == memberId
                     && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .OrderBy(l => l.DueOn)
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(db.Titles, x => x.c.TitleId, t => t.TitleId, (x, t) => new { x.l, x.c, t })
            .ToListAsync();

        return [.. rows.Select(r => (r.l, r.c, r.t))];
    }

    /// <summary>
    /// What this member still owes, in fines nobody has settled or written off.
    ///
    /// The counter is where this is worth knowing. A fine raised on Tuesday is
    /// discovered on the fines screen by whoever goes looking, and by nobody
    /// else — whereas the person who owes it stands at this desk holding out a
    /// pass. It does not stop an issue on its own; a library is not a debt
    /// collector, and the rules that do stop one are IssuePolicy's.
    /// </summary>
    public async Task<decimal> OwedAsync(long memberId) =>
        await db.Fines
            .Where(f => f.MemberId == memberId && f.Status == FineStatus.PENDING)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;

    private async Task<List<Member>> SearchMembersAsync(string term)
    {
        var like = $"%{term}%";

        return await db.Members
            .Where(m => EF.Functions.Like(m.FullName, like)
                     || EF.Functions.Like(m.MembershipNo, like)
                     || EF.Functions.Like(m.PersonnelNo!, like)
                     || EF.Functions.Like(m.Phone!, like))
            .OrderBy(m => m.FullName)
            .Take(10)
            .ToListAsync();
    }

    /// <summary>
    /// A member without their category is a member without any rules, so the two
    /// are never handed around separately. A missing category row would mean an
    /// unlendable member rather than an unlimited one, which is why it falls
    /// back to a locked-down default rather than to nothing.
    /// </summary>
    private async Task<ScannedMember> WithCategoryAsync(Member member)
    {
        var category = await db.MemberCategories
            .FirstOrDefaultAsync(c => c.CategoryId == member.CategoryId);

        return new ScannedMember(member, category ?? new MemberCategory
        {
            Name = "Unknown category",
            MaxBooks = 0,
            LoanDays = 0,
            MaxRenewals = 0,
            MaxClearance = SecurityClass.UNCLASSIFIED,
        });
    }
}
