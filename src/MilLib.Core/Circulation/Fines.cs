using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// What a late book costs, worked out from the dates and the member's category
/// — <c>fine_per_day</c> and <c>grace_days</c>, never a number in this file.
///
/// Computed on demand rather than accrued by anything scheduled. There is no
/// job runner on an air-gapped library PC that has been switched off for a
/// fortnight, and a fine that is only right when a service happened to run is
/// worse than no fine at all. The amount is frozen into a row only at
/// settlement, when the book comes back.
/// </summary>
public record Overdue(int Days, decimal Amount)
{
    public static Overdue None { get; } = new(0, 0m);

    public bool Any => Days > 0;
}

public static class FineCalculator
{
    public static Overdue For(Loan loan, MemberCategory category, DateOnly asOf)
    {
        var pastDue = asOf.DayNumber - loan.DueOn.DayNumber;

        var chargeable = Math.Max(0, pastDue - category.GraceDays);

        return chargeable == 0
            ? Overdue.None
            : new Overdue(chargeable, Math.Round(chargeable * category.FinePerDay, 2));
    }
}

/// <summary>One charge, with who owes it and what book it was about.</summary>
public record Owing(Fine Fine, Member Member, string Accession, string Title)
{
    public bool AboutABook => Title.Length > 0;

    /// <summary>
    /// Only a pending fine can be settled. Everything else is history, and
    /// history that can be edited is not history.
    /// </summary>
    public bool Settleable => Fine.Status == FineStatus.PENDING;
}

/// <summary>
/// Fines are a record, not a cash book.
///
/// This raises them, marks them paid against a receipt number somebody wrote by
/// hand, and waives them with a reason that goes on the audit log. It never
/// touches money, and it should never be made to look as though it does.
/// </summary>
public class Fines(MilLibDbContext db)
{
    /// <summary>
    /// Freeze what is owed at the moment the book comes back, if anything is.
    /// The amount is final here — after this it is a debt, not a calculation.
    /// </summary>
    public async Task<Fine?> SettleOverdueAsync(Loan loan, MemberCategory category, DateOnly today)
    {
        var overdue = FineCalculator.For(loan, category, today);

        if (!overdue.Any)
        {
            return null;
        }

        var fine = new Fine
        {
            MemberId = loan.MemberId,
            LoanId = loan.LoanId,
            Type = FineType.OVERDUE,
            Amount = overdue.Amount,
            CalculatedOn = today,
            DaysOverdue = overdue.Days,
            Status = FineStatus.PENDING,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        db.Fines.Add(fine);

        await db.SaveAndForgetAsync();

        return fine;
    }

    /// <summary>
    /// The fines in one state, with who owes them and what for.
    ///
    /// Pending first is the default because that is the only list anybody acts
    /// on; the settled ones are looked at when somebody queries a receipt.
    /// </summary>
    public async Task<IReadOnlyList<Owing>> InStateAsync(FineStatus status, string search = "")
    {
        var fines = db.Fines.Where(f => f.Status == status);

        search = search.Trim();

        if (search.Length > 0)
        {
            var like = $"%{search}%";

            fines = fines.Where(f => db.Members.Any(m =>
                m.MemberId == f.MemberId
                && (EF.Functions.Like(m.FullName, like)
                 || EF.Functions.Like(m.MembershipNo, like)
                 || EF.Functions.Like(m.PersonnelNo!, like))));
        }

        var rows = await fines
            .OrderByDescending(f => f.CalculatedOn)
            .ThenByDescending(f => f.FineId)
            .Join(db.Members, f => f.MemberId, m => m.MemberId, (f, m) => new { f, m })
            .ToListAsync();

        // The book a fine is about is reached through the loan, and a fine
        // raised by hand may have no loan at all — so it is fetched separately
        // rather than joined, which would silently drop those.
        var loanIds = rows.Where(r => r.f.LoanId is not null)
            .Select(r => r.f.LoanId!.Value).Distinct().ToList();

        var books = await db.Loans
            .Where(l => loanIds.Contains(l.LoanId))
            .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => new { l.LoanId, c })
            .Join(db.Titles, x => x.c.TitleId, t => t.TitleId,
                (x, t) => new { x.LoanId, x.c.AccessionNo, t.Name })
            .ToDictionaryAsync(x => x.LoanId, x => (x.AccessionNo, x.Name));

        return
        [
            .. rows.Select(r =>
            {
                var book = r.f.LoanId is not null && books.TryGetValue(r.f.LoanId.Value, out var found)
                    ? found
                    : ("", "");

                return new Owing(r.f, r.m, book.Item1, book.Item2);
            })
        ];
    }

    /// <summary>What is outstanding across the whole library, in one figure.</summary>
    public async Task<decimal> OutstandingAsync() =>
        await db.Fines.Where(f => f.Status == FineStatus.PENDING)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;

    /// <summary>
    /// A charge raised by hand — damage, or a loss decided outside a board.
    ///
    /// The amount is a librarian's judgement, not a calculation: what a damaged
    /// book is worth is not something a formula knows.
    /// </summary>
    public async Task<Fine> RaiseAsync(
        Loan loan, FineType type, decimal amount, string? remarks, long byUserId, DateOnly today)
    {
        var fine = new Fine
        {
            MemberId = loan.MemberId,
            LoanId = loan.LoanId,
            Type = type,
            Amount = Math.Round(amount, 2),
            CalculatedOn = today,
            Status = FineStatus.PENDING,
            Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim(),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        db.Fines.Add(fine);

        Journal.Note(db, byUserId, "FINE_RAISED", "fine", null,
            new { type = type.ToString(), amount, loan.LoanId });

        await db.SaveAndForgetAsync();

        return fine;
    }

    public async Task PayAsync(Fine fine, string receiptNo, long byUserId, DateOnly today)
    {
        fine.Status = FineStatus.PAID;
        fine.PaidOn = today;
        fine.ReceiptNo = receiptNo;
        fine.UpdatedAt = DateTime.Now;

        db.Fines.Update(fine);

        Journal.Note(db, byUserId, "FINE_PAID", "fine", fine.FineId,
            new { receipt_no = receiptNo, amount = fine.Amount });

        await db.SaveAndForgetAsync();
    }

    public async Task WaiveAsync(Fine fine, string reason, long byUserId)
    {
        fine.Status = FineStatus.WAIVED;
        fine.WaivedBy = byUserId;
        fine.WaiverReason = reason;
        fine.UpdatedAt = DateTime.Now;

        db.Fines.Update(fine);

        Journal.Note(db, byUserId, "FINE_WAIVED", "fine", fine.FineId,
            new { reason, amount = fine.Amount });

        await db.SaveAndForgetAsync();
    }

    /// <summary>What this member owes right now, on fines already raised.</summary>
    public async Task<decimal> OwedByAsync(long memberId) =>
        await db.Fines
            .Where(f => f.MemberId == memberId && f.Status == FineStatus.PENDING)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;
}
