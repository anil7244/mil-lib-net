using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Documents;

namespace MilLib.Core.Data;

/// <summary>What is owed before somebody can be signed off the roll.</summary>
public record Outstanding(
    IReadOnlyList<(Loan Loan, Copy Copy, Title Title, decimal Accruing)> OpenLoans,
    IReadOnlyList<Fine> PendingFines,
    decimal Accrued,
    decimal Total)
{
    /// <summary>
    /// Clear only with nothing out and nothing owed. Both, not either: a book
    /// still on somebody's shelf is as much an open item as an unpaid fine, and
    /// a posting-out chit that ignores one of them is worth nothing.
    /// </summary>
    public bool Eligible => OpenLoans.Count == 0 && PendingFines.Count == 0;
}

/// <summary>
/// The roll of members: enrolling somebody, keeping their details right, and
/// signing them off when they are posted out.
///
/// The rules here are the PHP application's rules. The one worth naming is the
/// clearance ceiling — a member cannot be granted a clearance higher than their
/// category allows, which is checked when the record is saved rather than
/// discovered at the counter with somebody standing there.
/// </summary>
public class Roll(MilLibDbContext db)
{
    /// <summary>
    /// What is wrong with this record, in words, or nothing.
    ///
    /// Returned as a list rather than thrown one at a time so a form can show
    /// everything that needs fixing at once instead of one thing per attempt.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProblemsWithAsync(Member member, MemberCategory category)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(member.MembershipNo))
        {
            problems.Add("A membership number is needed.");
        }
        else if (await db.Members.AnyAsync(m =>
            m.MembershipNo == member.MembershipNo && m.MemberId != member.MemberId))
        {
            problems.Add($"Membership number {member.MembershipNo} already belongs to somebody else.");
        }

        if (string.IsNullOrWhiteSpace(member.FullName))
        {
            problems.Add("A name is needed.");
        }

        if (member.CategoryId == 0)
        {
            problems.Add("Choose a category — it decides how much they may borrow and for how long.");
        }

        // The ceiling, checked here rather than at the counter.
        if (member.ClearanceLevel.Level() > category.MaxClearance.Level())
        {
            problems.Add($"The {category.Name} category allows at most "
                + $"{Words.Of(category.MaxClearance)} clearance.");
        }

        if (member.ValidUpto is not null && member.ValidUpto < member.EnrolledOn)
        {
            problems.Add("Membership cannot expire before it starts.");
        }

        return problems;
    }

    public async Task<Member> EnrolAsync(Member member, long byUserId)
    {
        var now = DateTime.Now;

        member.QrToken = await UniqueTokenAsync();
        member.CreatedBy = byUserId;
        member.UpdatedBy = byUserId;
        member.CreatedAt = now;
        member.UpdatedAt = now;

        db.Members.Add(member);

        Journal.Note(db, byUserId, "MEMBER_ENROLLED", "member", null,
            new { member.MembershipNo, member.FullName });

        await db.SaveAndForgetAsync();

        return member;
    }

    public async Task ReviseAsync(Member member, long byUserId)
    {
        member.UpdatedBy = byUserId;
        member.UpdatedAt = DateTime.Now;

        db.Members.Update(member);

        Journal.Note(db, byUserId, "MEMBER_UPDATED", "member", member.MemberId,
            new { member.MembershipNo, member.FullName });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Removing somebody who was entered by mistake.
    ///
    /// Anybody who has ever borrowed a book stays: their loans are the library's
    /// record of where its books have been, and a loan whose member has vanished
    /// is a book nobody can account for.
    /// </summary>
    public async Task<string?> WhyNotRemovableAsync(Member member) =>
        await db.Loans.AnyAsync(l => l.MemberId == member.MemberId)
            ? "This member has borrowed before. That history is the library's record of where its books have been, so the record stays. Close the membership instead."
            : null;

    public async Task RemoveAsync(Member member, long byUserId)
    {
        Journal.Note(db, byUserId, "MEMBER_REMOVED", "member", member.MemberId,
            new { member.MembershipNo, member.FullName });

        db.Members.Remove(member);

        await db.SaveAndForgetAsync();
    }

    // ------------------------------------------------------------ clearance --

    /// <summary>
    /// Everything standing between this member and a no-dues chit: what they
    /// still hold, what they still owe, and what is quietly accruing on a book
    /// that is already late.
    /// </summary>
    public async Task<Outstanding> OutstandingForAsync(Member member, MemberCategory category, DateOnly today)
    {
        var loans = await db.Loans
            .Where(l => l.MemberId == member.MemberId
                     && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .OrderBy(l => l.DueOn)
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l, c })
            .Join(db.Titles, x => x.c.TitleId, t => t.TitleId, (x, t) => new { x.l, x.c, t })
            .ToListAsync();

        var open = loans
            .Select(r => (r.l, r.c, r.t, FineCalculator.For(r.l, category, today).Amount))
            .ToList();

        var pending = await db.Fines
            .Where(f => f.MemberId == member.MemberId && f.Status == FineStatus.PENDING)
            .OrderBy(f => f.CalculatedOn)
            .ToListAsync();

        var accrued = open.Sum(o => o.Amount);

        return new Outstanding(open, pending, accrued, pending.Sum(f => f.Amount) + accrued);
    }

    /// <summary>Posted out and signed off. Both dates stamped today.</summary>
    public async Task ClearAsync(Member member, long byUserId, DateOnly today)
    {
        member.Status = MemberStatus.POSTED_OUT;
        member.PostedOutOn = today;
        member.ClearedOn = today;
        member.UpdatedBy = byUserId;
        member.UpdatedAt = DateTime.Now;

        db.Members.Update(member);

        Journal.Note(db, byUserId, "MEMBER_CLEARED", "member", member.MemberId,
            new { member.MembershipNo, cleared_on = today.ToString("yyyy-MM-dd") });

        await db.SaveAndForgetAsync();
    }

    // ----------------------------------------------------------- the pass --

    /// <summary>
    /// The passes for these members, in the order they will be handed out.
    ///
    /// Read here rather than assembled from whatever a screen already had,
    /// because the QR token is the thing the scanner reads and it changes the
    /// moment a pass is reissued. Printing a stale one prints a dead pass, and
    /// nothing about it looks wrong until somebody is standing at the counter
    /// with it.
    /// </summary>
    public async Task<IReadOnlyList<PassFor>> PassesForAsync(IReadOnlyList<long> memberIds)
    {
        var rows = await db.Members.AsNoTracking()
            .Where(m => memberIds.Contains(m.MemberId))
            .OrderBy(m => m.FullName)
            .ToListAsync();

        // The photograph comes back as the path the database records. Where
        // that file actually is on this machine is a question only the
        // application knows the answer to, so it resolves it before printing.
        return
        [
            .. rows.Select(m => new PassFor(
                m.MembershipNo,
                m.FullName,
                m.Rank,
                m.PersonnelNo,
                m.UnitCoy,
                m.ValidUpto,
                m.ClearanceLevel,
                m.QrToken,
                m.PhotoPath))
        ];
    }

    /// <summary>
    /// A new token on the pass, which makes every printed copy of the old one
    /// useless. What you do when a pass is lost.
    /// </summary>
    public async Task<string> ReissuePassAsync(Member member, long byUserId)
    {
        member.QrToken = await UniqueTokenAsync();
        member.UpdatedBy = byUserId;
        member.UpdatedAt = DateTime.Now;

        db.Members.Update(member);

        Journal.Note(db, byUserId, "MEMBER_PASS_REISSUED", "member", member.MemberId,
            new { member.MembershipNo });

        await db.SaveAndForgetAsync();

        return member.QrToken;
    }

    /// <summary>
    /// The next membership number, suggested rather than imposed — a library
    /// coming off paper has its own numbering and should be able to keep it.
    /// </summary>
    public async Task<string> SuggestedNumberAsync()
    {
        var next = (await db.Members.MaxAsync(m => (long?)m.MemberId) ?? 0) + 1;

        return "M" + next.ToString("D4");
    }

    /// <summary>
    /// The token printed on the pass as a QR code.
    ///
    /// Long and random rather than derived from anything about the member: a
    /// token that can be worked out from a membership number is a pass that can
    /// be forged with a pen and a QR generator.
    /// </summary>
    private async Task<string> UniqueTokenAsync()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        while (true)
        {
            var letters = new char[28];

            for (var i = 0; i < letters.Length; i++)
            {
                letters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }

            var token = "MLIB-" + new string(letters);

            if (!await db.Members.AnyAsync(m => m.QrToken == token))
            {
                return token;
            }
        }
    }
}
