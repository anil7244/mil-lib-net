using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>What a board decided, and about which books.</summary>
public record Condemnation(
    WithdrawalReason Reason,
    DateOnly On,
    string? Number = null,
    string? BoardProceedings = null,
    string? SanctionAuthority = null,
    DateOnly? SanctionDate = null,
    decimal? LossAmount = null,
    string? Remarks = null,
    long? ReplacedBy = null);

/// <summary>
/// Taking books off the register.
///
/// The only way a copy leaves live stock, and it is not a deletion: the row
/// stays for ever, marked withdrawn, and its accession number is retired rather
/// than reused. A register with a hole where a book used to be is a register
/// nobody can audit.
///
/// It is a batch, because a board condemns a shelf of books in one sitting and
/// one set of proceedings covers all of them.
/// </summary>
public class Withdrawals(MilLibDbContext db, Preferences preferences)
{
    public async Task<IReadOnlyList<(Withdrawal Withdrawal, int Copies, string By)>> AllAsync()
    {
        var withdrawals = await db.Withdrawals
            .OrderByDescending(w => w.WithdrawalDate)
            .ThenByDescending(w => w.WithdrawalId)
            .ToListAsync();

        var counts = await db.Copies
            .Where(c => c.WithdrawalId != null)
            .GroupBy(c => c.WithdrawalId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);

        var ids = withdrawals.Where(w => w.CreatedBy is not null)
            .Select(w => w.CreatedBy!.Value).Distinct().ToList();

        var people = await db.Users
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Display);

        return
        [
            .. withdrawals.Select(w => (
                w,
                counts.GetValueOrDefault(w.WithdrawalId),
                w.CreatedBy is null ? "—" : people.GetValueOrDefault(w.CreatedBy.Value, "—")))
        ];
    }

    /// <summary>The copies condemned under one set of proceedings.</summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title)>> UnderAsync(long withdrawalId)
    {
        var rows = await db.Copies
            .Where(c => c.WithdrawalId == withdrawalId)
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        return [.. rows.Select(r => (r.c, r.t))];
    }

    /// <summary>
    /// Copies a stock check reported missing.
    ///
    /// These are what a loss board actually sits on, so they are offered
    /// directly rather than left to be looked up one at a time.
    /// </summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title)>> MissingAsync()
    {
        var rows = await db.Copies
            .Where(c => c.Status == CopyStatus.MISSING)
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        return [.. rows.Select(r => (r.c, r.t))];
    }

    /// <summary>
    /// Copies named by barcode or accession number, however they were typed.
    ///
    /// Already-withdrawn copies are never returned: condemning a book twice
    /// would put it on two sets of proceedings and double-count the loss.
    /// </summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title)>> FindAsync(IEnumerable<string> identifiers)
    {
        var tokens = identifiers
            .SelectMany(i => i.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        if (tokens.Count == 0)
        {
            return [];
        }

        // The prefix is how the number is said out loud, not how it is stored.
        var bare = tokens
            .Select(t => preferences.AccessionPrefix.Length > 0
                && t.StartsWith(preferences.AccessionPrefix, StringComparison.OrdinalIgnoreCase)
                    ? t[preferences.AccessionPrefix.Length..]
                    : t)
            .Distinct()
            .ToList();

        var rows = await db.Copies
            .Where(c => c.Status != CopyStatus.WITHDRAWN
                     && (bare.Contains(c.Barcode) || bare.Contains(c.AccessionNo)))
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        return [.. rows.Select(r => (r.c, r.t))];
    }

    /// <summary>
    /// What is wrong with this condemnation, in words, or nothing.
    ///
    /// The rule that matters: a book somebody is holding cannot be condemned as
    /// damaged or obsolete. It is either recalled first, or it is written off as
    /// lost — and writing it off as lost is a different decision, with a fine
    /// against the borrower attached to it.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProblemsWithAsync(
        Condemnation board, IReadOnlyList<Copy> copies)
    {
        var problems = new List<string>();

        if (copies.Count == 0)
        {
            problems.Add("Name at least one copy to withdraw.");
        }

        if (board.Reason == WithdrawalReason.SUPERSEDED && board.ReplacedBy is null)
        {
            problems.Add("A superseded book is replaced by another. Say which one.");
        }

        if (!string.IsNullOrWhiteSpace(board.Number)
            && await db.Withdrawals.AnyAsync(w => w.WithdrawalNo == board.Number))
        {
            problems.Add($"Withdrawal number {board.Number} has already been used.");
        }

        if (board.Reason != WithdrawalReason.LOST && copies.Count > 0)
        {
            var ids = copies.Select(c => c.CopyId).ToList();

            var out_ = await db.Loans
                .Where(l => ids.Contains(l.CopyId)
                         && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
                .Join(db.Copies, l => l.CopyId, c => c.CopyId, (l, c) => c.AccessionNo)
                .ToListAsync();

            if (out_.Count > 0)
            {
                problems.Add("Somebody is holding "
                    + string.Join(", ", out_.Select(preferences.Accession))
                    + ". Recall the book first, or withdraw it as lost.");
            }
        }

        return problems;
    }

    public async Task<Withdrawal> WithdrawAsync(
        Condemnation board, IReadOnlyList<Copy> copies, long byUserId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var now = DateTime.Now;

        var withdrawal = new Withdrawal
        {
            WithdrawalNo = string.IsNullOrWhiteSpace(board.Number)
                ? await NextNumberAsync()
                : board.Number.Trim(),
            WithdrawalDate = board.On,
            Reason = board.Reason,
            BoardProceedings = Blank(board.BoardProceedings),
            SanctionAuthority = Blank(board.SanctionAuthority),
            SanctionDate = board.SanctionDate,
            TotalValue = copies.Sum(c => c.Cost ?? 0m),
            Remarks = Blank(board.Remarks),
            CreatedBy = byUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Withdrawals.Add(withdrawal);

        await db.SaveAndForgetAsync();

        foreach (var copy in copies)
        {
            // A copy still on loan can only be here as lost. Its loan is closed
            // as lost, and — if the library charges for losses — the cost goes
            // against the borrower, because that is the whole point of writing
            // a book off against a person rather than against the shelf.
            var loan = await db.Loans
                .FirstOrDefaultAsync(l => l.CopyId == copy.CopyId
                    && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE));

            if (loan is not null)
            {
                loan.Status = LoanStatus.LOST;
                loan.UpdatedAt = now;

                db.Loans.Update(loan);

                await db.SaveAndForgetAsync();

                if (preferences.Has(Feature.Fines))
                {
                    var amount = board.LossAmount ?? copy.Cost ?? 0m;

                    if (amount > 0)
                    {
                        db.Fines.Add(new Fine
                        {
                            MemberId = loan.MemberId,
                            LoanId = loan.LoanId,
                            Type = FineType.LOSS,
                            Amount = amount,
                            CalculatedOn = board.On,
                            Status = FineStatus.PENDING,
                            Remarks = "Book lost — withdrawn under " + withdrawal.WithdrawalNo,
                            CreatedAt = now,
                            UpdatedAt = now,
                        });

                        await db.SaveAndForgetAsync();
                    }
                }
            }

            copy.Status = CopyStatus.WITHDRAWN;
            copy.WithdrawnAt = board.On;
            copy.WithdrawalId = withdrawal.WithdrawalId;
            copy.UpdatedBy = byUserId;
            copy.UpdatedAt = now;

            db.Copies.Update(copy);

            await db.SaveAndForgetAsync();
        }

        // A superseded edition points at the one that replaced it, so anybody
        // finding the old book on a shelf can be told what to read instead.
        if (board.Reason == WithdrawalReason.SUPERSEDED && board.ReplacedBy is not null)
        {
            var titleIds = copies.Select(c => c.TitleId).Distinct().ToList();

            await db.Titles
                .Where(t => titleIds.Contains(t.TitleId) && t.TitleId != board.ReplacedBy)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SupersededBy, board.ReplacedBy));
        }

        Journal.Note(db, byUserId, "COPIES_WITHDRAWN", "withdrawal", withdrawal.WithdrawalId,
            new
            {
                withdrawal.WithdrawalNo,
                reason = board.Reason.ToString(),
                copies = copies.Count,
                value = withdrawal.TotalValue,
                board = withdrawal.BoardProceedings,
            });

        await db.SaveAndForgetAsync();

        await transaction.CommitAsync();

        return withdrawal;
    }

    /// <summary>
    /// The whole condemnation register: every copy ever taken off the books, in
    /// the order it left.
    /// </summary>
    public async Task<IReadOnlyList<(Copy Copy, Title Title, Withdrawal? Under)>> RegisterAsync()
    {
        var rows = await db.Copies
            .Where(c => c.Status == CopyStatus.WITHDRAWN)
            .OrderBy(c => c.WithdrawalId)
            .ThenBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        var ids = rows.Where(r => r.c.WithdrawalId is not null)
            .Select(r => r.c.WithdrawalId!.Value).Distinct().ToList();

        var under = await db.Withdrawals
            .Where(w => ids.Contains(w.WithdrawalId))
            .ToDictionaryAsync(w => w.WithdrawalId);

        return
        [
            .. rows.Select(r => (r.c, r.t, r.c.WithdrawalId is null
                ? null
                : under.GetValueOrDefault(r.c.WithdrawalId.Value)))
        ];
    }

    /// <summary>
    /// The next withdrawal number, suggested rather than imposed — a unit with
    /// its own numbering for board proceedings should be able to keep it.
    /// </summary>
    public async Task<string> NextNumberAsync()
    {
        var next = (await db.Withdrawals.MaxAsync(w => (long?)w.WithdrawalId) ?? 0) + 1;

        return "WD-" + next.ToString("D5");
    }

    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
