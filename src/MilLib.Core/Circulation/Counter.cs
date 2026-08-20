using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>What the operator asked for on top of the bare transaction.</summary>
public record IssueTerms(
    string? OverrideReason = null,
    IReadOnlyList<string>? OverrideRules = null,
    string? CustodyWitness = null,
    string? CustodySignature = null,
    string? IssuedToSubunit = null,
    string? Remarks = null);

/// <summary>What happened when a book came back.</summary>
public record ReturnOutcome(bool Damaged, int Steps, Fine? Fine, Reservation? HeldFor);

/// <summary>
/// The counter: handing a book over, taking it back, and extending a loan.
///
/// Whether an issue is allowed is <see cref="IssuePolicy"/>'s decision, not
/// this class's — this carries out a decision already made. What it adds is the
/// bookkeeping around it: the due date off the member's category, moving the
/// copy, settling what is owed, offering the book to whoever was waiting, and
/// writing down anything that a board might one day ask about.
///
/// Every one of these is a transaction. A loan recorded without the copy moving,
/// or a copy moved with no loan against it, is a book the library cannot account
/// for — and the counter is exactly where the machine gets switched off mid-task.
/// </summary>
public class Counter(MilLibDbContext db, Preferences preferences)
{
    private readonly Holds _holds = new(db);
    private readonly Fines _fines = new(db);

    public async Task<Loan> IssueAsync(
        Member member, MemberCategory category, Copy copy, Title title, long byUserId, IssueTerms terms)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var classified = title.SecurityClass.IsClassified();
        var now = DateTime.Now;

        // From the category, always. There is no default loan period in this
        // application and there must never be one.
        var due = DateOnly.FromDateTime(now.Date).AddDays(category.LoanDays);

        var loan = new Loan
        {
            CopyId = copy.CopyId,
            MemberId = member.MemberId,
            IssuedOn = now,
            DueOn = due,
            Status = LoanStatus.OPEN,
            RenewalCount = 0,
            IssuedBy = byUserId,
            IssueCondition = copy.Condition,

            // Custody details are kept only where they mean something. Recording
            // a witness against an unclassified paperback makes the ones that
            // matter harder to find.
            CustodyWitness = classified ? Blank(terms.CustodyWitness) : null,
            CustodySignature = classified ? Blank(terms.CustodySignature) : null,

            IssuedToSubunit = Blank(terms.IssuedToSubunit),
            Remarks = Blank(terms.Remarks),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Loans.Add(loan);

        copy.Status = CopyStatus.ISSUED;
        copy.UpdatedBy = byUserId;
        copy.UpdatedAt = now;

        db.Copies.Update(copy);

        if (classified)
        {
            Journal.Note(db, byUserId, "ISSUE_CLASSIFIED", "loan", null,
                new { witness = terms.CustodyWitness }, title.SecurityClass);
        }

        if (!string.IsNullOrWhiteSpace(terms.OverrideReason))
        {
            Journal.Note(db, byUserId, "ISSUE_OVERRIDE", "loan", null,
                new { reason = terms.OverrideReason, rules = terms.OverrideRules ?? [] },
                title.SecurityClass);
        }

        await db.SaveAndForgetAsync();

        // If they had a hold ready on this title, they have now collected it.
        if (preferences.Has(Feature.Reservations))
        {
            await _holds.CollectedAsync(member.MemberId, copy.TitleId);
        }

        await transaction.CommitAsync();

        return loan;
    }

    public async Task<ReturnOutcome> ReturnAsync(
        Loan loan, Copy copy, Title title, MemberCategory category,
        CopyCondition returnedIn, string? remarks, long byUserId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now.Date);

        var issuedIn = loan.IssueCondition;
        var damaged = returnedIn.IsWorseThan(issuedIn);

        loan.Status = LoanStatus.RETURNED;
        loan.ReturnedOn = now;
        loan.ReturnCondition = returnedIn;
        loan.ReturnedTo = byUserId;
        loan.Remarks = Append(loan.Remarks, remarks);
        loan.UpdatedAt = now;

        db.Loans.Update(loan);

        copy.Status = CopyStatus.AVAILABLE;
        copy.Condition = returnedIn;
        copy.UpdatedBy = byUserId;
        copy.UpdatedAt = now;

        db.Copies.Update(copy);

        // Worth a line in the log when it is classified, or when the book is in
        // a worse state than it left in. Everything else is an ordinary return
        // and does not need a record of its own.
        if (title.SecurityClass.IsClassified() || damaged)
        {
            Journal.Note(db, byUserId, "RETURN", "loan", loan.LoanId,
                new { damaged, from = issuedIn.ToString(), to = returnedIn.ToString() },
                title.SecurityClass);
        }

        await db.SaveAndForgetAsync();

        var fine = preferences.Has(Feature.Fines)
            ? await _fines.SettleOverdueAsync(loan, category, today)
            : null;

        var heldFor = preferences.Has(Feature.Reservations)
            ? await _holds.OfferOnReturnAsync(copy, today)
            : null;

        await transaction.CommitAsync();

        return new ReturnOutcome(damaged, returnedIn.DegradedFrom(issuedIn), fine, heldFor);
    }

    /// <summary>
    /// Extend a loan. Whether it may be extended — the renewal limit, and
    /// whether anybody is waiting — is decided before this is called; this
    /// carries out an approved renewal.
    /// </summary>
    public async Task<Renewal> RenewAsync(Loan loan, MemberCategory category, long byUserId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now.Date);
        var oldDue = loan.DueOn;

        // A loan already past its date is renewed from today, not from the date
        // it missed. Renewing from the old date would hand back a book that is
        // still overdue the moment it is renewed.
        var from = oldDue < today ? today : oldDue;
        var newDue = from.AddDays(category.LoanDays);

        var renewal = new Renewal
        {
            LoanId = loan.LoanId,
            RenewedOn = now,
            OldDueOn = oldDue,
            NewDueOn = newDue,
            RenewedBy = byUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Renewals.Add(renewal);

        loan.DueOn = newDue;
        loan.RenewalCount += 1;
        loan.Status = LoanStatus.OPEN;
        loan.UpdatedAt = now;

        db.Loans.Update(loan);

        await db.SaveAndForgetAsync();

        await transaction.CommitAsync();

        return renewal;
    }

    /// <summary>
    /// Why this loan may not be renewed, or null if it may.
    ///
    /// Kept beside the renewal itself so the counter screen and any other caller
    /// get the same answer in the same words.
    /// </summary>
    public async Task<string?> WhyNotRenewableAsync(Loan loan, MemberCategory category, long titleId)
    {
        if (loan.Status is not (LoanStatus.OPEN or LoanStatus.OVERDUE))
        {
            return "That loan is not open.";
        }

        if (!preferences.Has(Feature.Renewals))
        {
            return "Renewals are turned off for this library.";
        }

        if (loan.RenewalCount >= category.MaxRenewals)
        {
            return category.MaxRenewals == 0
                ? "This member's category does not allow renewals."
                : $"Renewal limit reached ({category.MaxRenewals} allowed).";
        }

        if (preferences.Has(Feature.Reservations)
            && await _holds.AnyoneWaitingAsync(titleId, DateOnly.FromDateTime(DateTime.Today)))
        {
            return "Cannot renew — another member is waiting for this title.";
        }

        return null;
    }

    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Remarks are added to, never replaced. What somebody wrote about this loan
    /// in March is still true in June, and the note taken at return does not
    /// supersede it.
    /// </summary>
    private static string? Append(string? existing, string? note)
    {
        note = note?.Trim();

        if (string.IsNullOrEmpty(note))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + "\n" + note;
    }
}
