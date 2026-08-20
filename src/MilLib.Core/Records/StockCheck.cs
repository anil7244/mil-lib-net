using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>What one scan turned out to be, and where the count now stands.</summary>
public record ScanOutcome(ScanResult Result, Copy? Copy, Title? Title, string Barcode, Tally Counts);

/// <summary>The running figures, for the person at the shelf.</summary>
public record Tally(int Expected, int Found, int NotInRegister, int ScannedTwice)
{
    public int StillToFind => Math.Max(0, Expected - Found);

    public double Progress => Expected == 0 ? 0 : (double)Found / Expected;
}

/// <summary>
/// The count set against the register.
///
/// Not a verdict — a statement of what was found and what was not, which a
/// board then acts on. Nothing here writes a copy off.
/// </summary>
public record Reconciliation(
    int Expected,
    int Found,
    IReadOnlyList<(Copy Copy, Title Title)> Missing,
    IReadOnlyList<string> NotInRegister,
    IReadOnlyList<(Copy Copy, Title Title)> Anomalies,
    IReadOnlyList<(Copy Copy, Title Title)> Moved)
{
    public bool Complete => Missing.Count == 0 && NotInRegister.Count == 0;
}

/// <summary>
/// Counting the shelves against the register.
///
/// Every scan is written down the moment it is made, so a count that takes
/// three days across two shifts survives the machine being switched off — you
/// reopen the session and carry on. The reconciliation is done at the end
/// against the register as it stands *then*, not as it stood when the count
/// began, so the library can keep lending while the count runs.
///
/// What is expected on the shelf is what the system cannot otherwise account
/// for: available, reference-only, and already-missing. A book that is issued,
/// reserved, in transit or at the binder is accounted for — it is not on the
/// shelf and it is not missing. Lost and withdrawn copies are not live stock at
/// all.
/// </summary>
public class StockCheck(MilLibDbContext db)
{
    private static readonly CopyStatus[] ExpectedOnShelf =
        [CopyStatus.AVAILABLE, CopyStatus.REFERENCE_ONLY, CopyStatus.MISSING];

    public async Task<IReadOnlyList<(StockVerification Check, string By)>> AllAsync()
    {
        var checks = await db.StockVerifications
            .OrderByDescending(v => v.StartedOn)
            .ThenByDescending(v => v.VerificationId)
            .ToListAsync();

        var ids = checks.Where(c => c.ConductedBy is not null)
            .Select(c => c.ConductedBy!.Value).Distinct().ToList();

        var people = await db.Users
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Display);

        return
        [
            .. checks.Select(c => (c, c.ConductedBy is null
                ? "—"
                : people.GetValueOrDefault(c.ConductedBy.Value, "—")))
        ];
    }

    public async Task<StockVerification> StartAsync(string name, long byUserId, long? branchId, DateOnly today)
    {
        var check = new StockVerification
        {
            Name = name.Trim(),
            StartedOn = today,
            BranchId = branchId,
            Status = VerificationStatus.IN_PROGRESS,
            ConductedBy = byUserId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        db.StockVerifications.Add(check);

        Journal.Note(db, byUserId, "STOCK_CHECK_STARTED", "stock_verification", null, new { name });

        await db.SaveAndForgetAsync();

        return check;
    }

    /// <summary>
    /// One barcode off the shelf.
    ///
    /// Written down whatever it turns out to be — including a barcode that
    /// matches nothing, which is the most interesting outcome of the three and
    /// the one a board wants to see. Nothing is silently discarded.
    /// </summary>
    public async Task<ScanOutcome> ScanAsync(StockVerification check, string barcode, long byUserId)
    {
        barcode = barcode.Trim();

        var copy = await db.Copies
            .FirstOrDefaultAsync(c => c.Barcode == barcode || c.AccessionNo == barcode);

        var title = copy is null
            ? null
            : await db.Titles.FirstOrDefaultAsync(t => t.TitleId == copy.TitleId);

        ScanResult result;

        if (copy is null)
        {
            result = ScanResult.NOT_IN_REGISTER;
        }
        else
        {
            var already = await db.StockVerificationScans.AnyAsync(s =>
                s.VerificationId == check.VerificationId
                && s.CopyId == copy.CopyId
                && s.Result == ScanResult.FOUND);

            result = already ? ScanResult.DUPLICATE_SCAN : ScanResult.FOUND;
        }

        db.StockVerificationScans.Add(new StockVerificationScan
        {
            VerificationId = check.VerificationId,
            CopyId = copy?.CopyId,
            BarcodeScanned = barcode,
            Result = result,
            ScannedAt = DateTime.Now,
            ScannedBy = byUserId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        });

        await db.SaveAndForgetAsync();

        return new ScanOutcome(result, copy, title, barcode, await CountAsync(check));
    }

    public async Task<Tally> CountAsync(StockVerification check)
    {
        var scans = db.StockVerificationScans.Where(s => s.VerificationId == check.VerificationId);

        var found = await scans
            .Where(s => s.Result == ScanResult.FOUND && s.CopyId != null)
            .Select(s => s.CopyId)
            .Distinct()
            .CountAsync();

        return new Tally(
            await Expected(check).CountAsync(),
            found,
            await scans.CountAsync(s => s.Result == ScanResult.NOT_IN_REGISTER),
            await scans.CountAsync(s => s.Result == ScanResult.DUPLICATE_SCAN));
    }

    /// <summary>The last few scans, so the shelf sees what it just did.</summary>
    public async Task<IReadOnlyList<(StockVerificationScan Scan, string What)>> RecentAsync(
        StockVerification check, int howMany = 15)
    {
        var scans = await db.StockVerificationScans
            .Where(s => s.VerificationId == check.VerificationId)
            .OrderByDescending(s => s.ScanId)
            .Take(howMany)
            .ToListAsync();

        var copyIds = scans.Where(s => s.CopyId is not null).Select(s => s.CopyId!.Value).Distinct().ToList();

        var names = await db.Copies
            .Where(c => copyIds.Contains(c.CopyId))
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c.CopyId, t.Name })
            .ToDictionaryAsync(x => x.CopyId, x => x.Name);

        return
        [
            .. scans.Select(s => (s, s.CopyId is null
                ? s.BarcodeScanned
                : names.GetValueOrDefault(s.CopyId.Value, s.BarcodeScanned)))
        ];
    }

    /// <summary>
    /// What the count found, set against the register as it stands now.
    ///
    /// Four lists, and each is a different question for the board. What is
    /// missing. What was on the shelf but is not in the register at all. What
    /// was found but the register says should have been elsewhere. And what
    /// moved while the count was running — because a book issued on Tuesday and
    /// counted on Monday is not missing, it is just late to the paperwork.
    /// </summary>
    public async Task<Reconciliation> ReconcileAsync(StockVerification check)
    {
        var foundIds = await db.StockVerificationScans
            .Where(s => s.VerificationId == check.VerificationId
                     && s.Result == ScanResult.FOUND
                     && s.CopyId != null)
            .Select(s => s.CopyId!.Value)
            .Distinct()
            .ToListAsync();

        var expected = await Expected(check).CountAsync();

        var missing = await Expected(check)
            .Where(c => !foundIds.Contains(c.CopyId))
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        var notInRegister = await db.StockVerificationScans
            .Where(s => s.VerificationId == check.VerificationId
                     && s.Result == ScanResult.NOT_IN_REGISTER)
            .OrderBy(s => s.ScanId)
            .Select(s => s.BarcodeScanned)
            .ToListAsync();

        // On the shelf, but the register says it should not have been.
        var anomalies = await db.Copies
            .Where(c => foundIds.Contains(c.CopyId) && !ExpectedOnShelf.Contains(c.Status))
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        var since = check.StartedOn.ToDateTime(TimeOnly.MinValue);

        var moved = await db.Loans
            .Where(l => l.IssuedOn >= since || (l.ReturnedOn != null && l.ReturnedOn >= since))
            .Select(l => l.CopyId)
            .Distinct()
            .Join(db.Copies, id => id, c => c.CopyId, (id, c) => c)
            .OrderBy(c => c.AccessionSeq)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .ToListAsync();

        return new Reconciliation(
            expected,
            expected - missing.Count,
            [.. missing.Select(m => (m.c, m.t))],
            notInRegister,
            [.. anomalies.Select(a => (a.c, a.t))],
            [.. moved.Select(m => (m.c, m.t))]);
    }

    /// <summary>
    /// Close it and write the figures onto the session.
    ///
    /// The figures are frozen here because this is the row a board minute will
    /// quote next year, and a report that recalculates itself from a library
    /// that has moved on since is a report that no longer says what was signed.
    /// </summary>
    public async Task<Reconciliation> CloseAsync(
        StockVerification check, string? boardReference, long byUserId, DateOnly today)
    {
        var found = await ReconcileAsync(check);

        check.Status = VerificationStatus.COMPLETED;
        check.CompletedOn = today;
        check.BoardReference = string.IsNullOrWhiteSpace(boardReference) ? null : boardReference.Trim();
        check.TotalExpected = found.Expected;
        check.TotalFound = found.Found;
        check.TotalMissing = found.Missing.Count;
        check.UpdatedAt = DateTime.Now;

        db.StockVerifications.Update(check);

        Journal.Note(db, byUserId, "STOCK_CHECK_CLOSED", "stock_verification", check.VerificationId,
            new
            {
                check.Name,
                expected = found.Expected,
                found = found.Found,
                missing = found.Missing.Count,
                board = check.BoardReference,
            });

        await db.SaveAndForgetAsync();

        return found;
    }

    /// <summary>
    /// Give up on it. The session and every scan in it are kept — an abandoned
    /// count is still a record that somebody spent two days counting.
    /// </summary>
    public async Task AbandonAsync(StockVerification check, long byUserId, DateOnly today)
    {
        check.Status = VerificationStatus.ABANDONED;
        check.CompletedOn = today;
        check.UpdatedAt = DateTime.Now;

        db.StockVerifications.Update(check);

        Journal.Note(db, byUserId, "STOCK_CHECK_ABANDONED", "stock_verification", check.VerificationId,
            new { check.Name });

        await db.SaveAndForgetAsync();
    }

    private IQueryable<Copy> Expected(StockVerification check)
    {
        var copies = db.Copies.Where(c => ExpectedOnShelf.Contains(c.Status));

        return check.BranchId is null
            ? copies
            : copies.Where(c => c.BranchId == check.BranchId);
    }
}
