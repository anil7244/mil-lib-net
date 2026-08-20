using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// A single reason an issue is stopped.
///
/// Either absolute — no authority in the building can proceed — or overridable,
/// meaning a supervisor may go ahead with a reason that gets written down. The
/// split is the whole point: a counter that can override everything is a counter
/// with no rules, and one that can override nothing sends people upstairs all
/// day.
/// </summary>
public record Violation(string Code, bool Absolute, string Message)
{
    public static Violation Hard(string code, string message) => new(code, true, message);

    public static Violation Soft(string code, string message) => new(code, false, message);
}

/// <summary>What the rules said, and what the counter can do about it.</summary>
public record IssueEvaluation(IReadOnlyList<Violation> Violations)
{
    public IReadOnlyList<Violation> Absolute => [.. Violations.Where(v => v.Absolute)];

    public IReadOnlyList<Violation> Overridable => [.. Violations.Where(v => !v.Absolute)];

    /// <summary>A hard stop exists — the issue cannot proceed on any authority.</summary>
    public bool Blocked => Absolute.Count > 0;

    /// <summary>Not blocked, but going ahead needs a reason on the record.</summary>
    public bool NeedsOverride => !Blocked && Overridable.Count > 0;

    public bool Clear => Violations.Count == 0;
}

/// <summary>
/// The single place every issue rule is decided.
///
/// No screen re-implements a check. The counter asks, and acts on the answer.
/// This is the same list of rules, in the same order, as the PHP application's
/// IssuePolicy — the two must not be able to disagree about whether a particular
/// member may have a particular book.
/// </summary>
public class IssuePolicy(MilLibDbContext db)
{
    public async Task<IssueEvaluation> EvaluateAsync(Member member, MemberCategory category, Copy copy, Title title)
    {
        var violations = new List<Violation>();

        // Where the member stands.
        if (member.Status is MemberStatus.POSTED_OUT or MemberStatus.CLOSED)
        {
            violations.Add(Violation.Hard("member_status",
                $"Member is {Words.Of(member.Status).ToLowerInvariant()} — no new loans."));
        }
        else if (member.Status is MemberStatus.SUSPENDED or MemberStatus.EXPIRED)
        {
            violations.Add(Violation.Soft("member_status",
                $"Member is {Words.Of(member.Status).ToLowerInvariant()}."));
        }

        // Clearance. Absolute, always: this is the rule the whole security model
        // rests on, and a rule a supervisor can wave through is not one.
        if (!member.CanAccess(category, title.SecurityClass))
        {
            violations.Add(Violation.Hard("clearance",
                $"Book is {Words.Of(title.SecurityClass)}; member is cleared only to "
                + $"{Words.Of(member.EffectiveClearance(category))}."));
        }

        // The copy has to be a thing that can be handed over. A copy being held
        // for this member is issuable to them — that is them collecting their
        // own reservation, not taking somebody else's.
        if (copy.Status != CopyStatus.AVAILABLE && !await HeldForAsync(copy, member))
        {
            violations.Add(Violation.Hard("copy_status",
                $"Copy is {Words.Of(copy.Status).ToLowerInvariant()}, not available to issue."));
        }

        // Reference-only. Overridable, because a sanctioned reading-room loan is
        // a real thing a librarian does.
        if (!copy.IsCirculating)
        {
            violations.Add(Violation.Soft("reference_only", "Copy is reference-only (non-circulating)."));
        }

        // How many they already hold.
        var open = await db.Loans
            .CountAsync(l => l.MemberId == member.MemberId
                          && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE));

        if (open >= category.MaxBooks)
        {
            violations.Add(Violation.Soft("max_books",
                $"Member holds {open} of {category.MaxBooks} permitted loans."));
        }

        return new IssueEvaluation(violations);
    }

    private async Task<bool> HeldForAsync(Copy copy, Member member) =>
        copy.Status == CopyStatus.RESERVED
        && await db.Reservations.AnyAsync(r =>
            r.TitleId == copy.TitleId
            && r.MemberId == member.MemberId
            && r.Status == ReservationStatus.READY
            && r.FulfilledCopyId == copy.CopyId);
}
