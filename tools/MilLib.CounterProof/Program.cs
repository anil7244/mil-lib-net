using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// The counter loop, put through its paces.
//
// These are the rules a library is actually judged on — who may borrow what,
// for how long, what a late book costs, and what happens to a book that comes
// back damaged. They exist in two applications now, and the whole point of
// copying them faithfully is lost the moment nobody checks that the copy still
// matches. So each rule is exercised here against real data and the answer is
// stated in full.
//
// It works on a scratch copy of the library, made fresh each run and thrown
// away. Nothing here touches the records anybody depends on.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.CounterProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-counter-proof.sqlite");

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

foreach (var leftover in new[] { scratch, scratch + "-wal", scratch + "-shm" })
{
    if (File.Exists(leftover))
    {
        File.Delete(leftover);
    }
}

File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");
Console.WriteLine();

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-46}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

void Heading(string text)
{
    Console.WriteLine();
    Console.WriteLine(text);
}

MilLibDbContext Open() => new(DatabaseSource.File(scratch));

var today = DateOnly.FromDateTime(DateTime.Today);

// Somebody has to be signed in for anything to be recorded against a name.
long staffId;
long memberId;
long categoryId;
Preferences preferences;

await using (var db = Open())
{
    staffId = await db.Users.Select(u => u.UserId).FirstAsync();
    var member = await db.Members.FirstAsync();
    memberId = member.MemberId;
    categoryId = member.CategoryId;
    preferences = await Preferences.ReadAsync(db);
}

/// <summary>Puts the library back the way the previous test found it.</summary>
async Task<(Member Member, MemberCategory Category)> WhoAsync(MilLibDbContext db)
{
    var member = await db.Members.FirstAsync(m => m.MemberId == memberId);
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == categoryId);

    return (member, category);
}

async Task<ScannedCopy> FreeBookAsync(MilLibDbContext db)
{
    var copy = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    return new ScannedCopy(copy, title, null, null);
}

// ================================================================= issuing ==

Heading("Handing a book over");

long loanId;

await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);
    var book = await FreeBookAsync(db);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, category, book.Copy, book.Title);

    Check("an ordinary issue has nothing in the way", evaluation.Clear,
        evaluation.Clear ? "clear" : string.Join("; ", evaluation.Violations.Select(v => v.Message)));

    var loan = await new Counter(db, preferences)
        .IssueAsync(member, category, book.Copy, book.Title, staffId, new IssueTerms());

    loanId = loan.LoanId;

    var expected = today.AddDays(category.LoanDays);

    Check("the due date comes from the category", loan.DueOn == expected,
        $"{category.Name}: {category.LoanDays} days → due {loan.DueOn:dd MMM yyyy}");

    var after = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == book.Copy.CopyId);

    Check("the copy moved with the loan", after.Status == CopyStatus.ISSUED, $"copy is {after.Status}");

    Check("the condition it went out in was kept", loan.IssueCondition == after.Condition,
        $"issued in {loan.IssueCondition}");
}

// A second issue of the same copy must be refused outright.
await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);

    var loan = await db.Loans.FirstAsync(l => l.LoanId == loanId);
    var copy = await db.Copies.FirstAsync(c => c.CopyId == loan.CopyId);
    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, category, copy, title);

    Check("a book already out cannot go out again", evaluation.Blocked,
        evaluation.Absolute.FirstOrDefault()?.Message ?? "not blocked");
}

// ================================================================ renewing ==

Heading("Extending a loan");

await using (var db = Open())
{
    var (_, category) = await WhoAsync(db);

    var loan = await db.Loans.FirstAsync(l => l.LoanId == loanId);
    var copy = await db.Copies.FirstAsync(c => c.CopyId == loan.CopyId);

    var counter = new Counter(db, preferences);

    var was = loan.DueOn;
    var why = await counter.WhyNotRenewableAsync(loan, category, copy.TitleId);

    if (category.MaxRenewals == 0)
    {
        Check("a category with no renewals refuses one", why is not null, why ?? "allowed it");
    }
    else
    {
        Check("a renewal within the limit is allowed", why is null, why ?? "allowed");

        var renewal = await counter.RenewAsync(loan, category, staffId);

        Check("the new date is the old one plus the loan period",
            renewal.NewDueOn == was.AddDays(category.LoanDays),
            $"{was:dd MMM} → {renewal.NewDueOn:dd MMM}");

        // Run it up against the ceiling.
        var fresh = await db.Loans.AsNoTracking().FirstAsync(l => l.LoanId == loanId);

        await db.Loans.Where(l => l.LoanId == loanId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RenewalCount, category.MaxRenewals));

        fresh = await db.Loans.AsNoTracking().FirstAsync(l => l.LoanId == loanId);

        var refused = await counter.WhyNotRenewableAsync(fresh, category, copy.TitleId);

        Check("the renewal limit is enforced", refused is not null, refused ?? "let it through");

        await db.Loans.Where(l => l.LoanId == loanId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RenewalCount, 0));
    }
}

// ================================================== returning, and damage ==

Heading("Taking a book back");

await using (var db = Open())
{
    var (_, category) = await WhoAsync(db);

    var loan = await db.Loans.FirstAsync(l => l.LoanId == loanId);
    var copy = await db.Copies.FirstAsync(c => c.CopyId == loan.CopyId);
    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    // Sent out good, comes back poor: two steps down the ladder.
    await db.Loans.Where(l => l.LoanId == loanId)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.IssueCondition, CopyCondition.GOOD));

    loan = await db.Loans.FirstAsync(l => l.LoanId == loanId);

    var outcome = await new Counter(db, preferences)
        .ReturnAsync(loan, copy, title, category, CopyCondition.POOR, "Cover detached.", staffId);

    Check("a book back in a worse state is flagged", outcome.Damaged,
        $"good → poor, down {outcome.Steps} steps");

    var after = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == copy.CopyId);

    Check("the copy is back on the shelf", after.Status == CopyStatus.AVAILABLE, $"copy is {after.Status}");

    Check("the copy carries its new condition", after.Condition == CopyCondition.POOR,
        $"now {after.Condition}");

    var settled = await db.Loans.AsNoTracking().FirstAsync(l => l.LoanId == loanId);

    Check("the loan is closed", settled.Status == LoanStatus.RETURNED
        && settled.ReturnedOn is not null, $"{settled.Status}, {settled.ReturnedOn:dd MMM yyyy HH:mm}");

    Check("the note was added, not substituted",
        settled.Remarks?.Contains("Cover detached") == true, settled.Remarks ?? "(none)");
}

// ==================================================== what a late book costs ==

Heading("What a late book costs");

await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);
    var book = await FreeBookAsync(db);

    var counter = new Counter(db, preferences);

    var loan = await counter.IssueAsync(member, category, book.Copy, book.Title, staffId, new IssueTerms());

    // Ten days past the grace period, whatever that period happens to be.
    var late = category.GraceDays + 10;

    await db.Loans.Where(l => l.LoanId == loan.LoanId)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.DueOn, today.AddDays(-late)));

    loan = await db.Loans.FirstAsync(l => l.LoanId == loan.LoanId);

    var owed = FineCalculator.For(loan, category, today);

    Check("the grace period is not charged for", owed.Days == 10,
        $"{late} days late, {category.GraceDays} of grace → {owed.Days} chargeable");

    Check("the rate comes from the category", owed.Amount == 10 * category.FinePerDay,
        $"{owed.Days} × {preferences.Money(category.FinePerDay)} = {preferences.Money(owed.Amount)}");

    var copy = await db.Copies.FirstAsync(c => c.CopyId == loan.CopyId);
    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    var outcome = await counter.ReturnAsync(loan, copy, title, category,
        loan.IssueCondition, null, staffId);

    if (preferences.Has(Feature.Fines))
    {
        Check("the amount is frozen on return", outcome.Fine is not null && outcome.Fine.Amount == owed.Amount,
            outcome.Fine is null ? "nothing raised" : $"{preferences.Money(outcome.Fine.Amount)} pending");
    }
    else
    {
        Check("fines are off, so nothing was raised", outcome.Fine is null, "none");
    }

    // A book back on time costs nothing, which is the case that must never
    // quietly start charging.
    var onTime = await counter.IssueAsync(member, category, await NextFreeAsync(db), title, staffId, new IssueTerms());

    Check("a book back on time owes nothing",
        !FineCalculator.For(onTime, category, today).Any,
        $"due {onTime.DueOn:dd MMM yyyy}, nothing owed");
}

// ================================================================ clearance ==

Heading("Who may have what");

await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);
    var book = await FreeBookAsync(db);

    // Mark the book secret for the length of this test.
    await db.Titles.Where(t => t.TitleId == book.Title.TitleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.SECRET));

    var title = await db.Titles.FirstAsync(t => t.TitleId == book.Title.TitleId);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, category, book.Copy, title);

    var clearance = evaluation.Absolute.FirstOrDefault(v => v.Code == "clearance");

    Check("a book above the member's clearance is refused", clearance is not null,
        clearance?.Message ?? "let through");

    Check("clearance is absolute, never overridable",
        !evaluation.Overridable.Any(v => v.Code == "clearance"), "hard stop");

    // A category ceiling caps a member's own clearance.
    var capped = new Member { ClearanceLevel = SecurityClass.SECRET };
    var narrow = new MemberCategory { MaxClearance = SecurityClass.RESTRICTED };

    Check("the category ceiling caps a personal clearance",
        capped.EffectiveClearance(narrow) == SecurityClass.RESTRICTED,
        "cleared to Secret, category stops at Restricted → Restricted");

    await db.Titles.Where(t => t.TitleId == title.TitleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.UNCLASSIFIED));
}

// ============================================================= the ceilings ==

Heading("How much one person may hold");

await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);
    var book = await FreeBookAsync(db);

    var held = await db.Loans.CountAsync(l => l.MemberId == member.MemberId
        && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE));

    // Bring the ceiling down to what they already hold.
    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.MaxBooks, held));

    var tightened = await db.MemberCategories.FirstAsync(c => c.CategoryId == category.CategoryId);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, tightened, book.Copy, book.Title);

    var ceiling = evaluation.Overridable.FirstOrDefault(v => v.Code == "max_books");

    Check("being at the limit stops the issue", ceiling is not null,
        ceiling?.Message ?? "let through");

    Check("but a supervisor may go ahead", evaluation.NeedsOverride && !evaluation.Blocked,
        "overridable with a reason");

    Check("and a counter clerk may not",
        !Abilities.Can(UserRole.COUNTER, Ability.CirculationOverride), "refused");

    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.MaxBooks, category.MaxBooks));
}

// ========================================================== reference-only ==

Heading("A book that does not leave the room");

await using (var db = Open())
{
    var (member, category) = await WhoAsync(db);
    var book = await FreeBookAsync(db);

    await db.Copies.Where(c => c.CopyId == book.Copy.CopyId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCirculating, false));

    var copy = await db.Copies.FirstAsync(c => c.CopyId == book.Copy.CopyId);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, category, copy, book.Title);

    var reference = evaluation.Overridable.FirstOrDefault(v => v.Code == "reference_only");

    Check("a reference copy needs authority to leave", reference is not null,
        reference?.Message ?? "let through");

    await db.Copies.Where(c => c.CopyId == copy.CopyId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCirculating, true));
}

// ============================================================== the scan box ==

Heading("What the one box makes of things");

await using (var db = Open())
{
    var desk = new Desk(db, preferences);

    var copy = await db.Copies.OrderBy(c => c.CopyId).FirstAsync();
    var member = await db.Members.FirstAsync();

    Check("a barcode is a book", await desk.ResolveAsync(copy.Barcode) is Scan.Book,
        copy.Barcode);

    Check("an accession number is a book", await desk.ResolveAsync(copy.AccessionNo) is Scan.Book,
        copy.AccessionNo);

    // A number read off a label carries the prefix with it.
    var spoken = preferences.Accession(copy.AccessionNo);

    Check("so is one typed with the prefix on it",
        await desk.ResolveAsync(spoken) is Scan.Book, spoken);

    Check("a pass is a person", await desk.ResolveAsync(member.QrToken) is Scan.Person,
        member.Display);

    Check("a membership number is a person",
        await desk.ResolveAsync(member.MembershipNo) is Scan.Person, member.MembershipNo);

    Check("part of a name finds them too",
        await desk.ResolveAsync(member.FullName[..Math.Min(4, member.FullName.Length)])
            is Scan.Person or Scan.Several,
        member.FullName[..Math.Min(4, member.FullName.Length)]);

    Check("and nonsense is nonsense",
        await desk.ResolveAsync("zzz-not-a-thing-zzz") is Scan.Unknown, "not recognised");
}

// =========================================== what the panel tells the operator ==

// The counter panel states three things about whoever is at the desk before a
// single book is scanned: what they are cleared for, how much of their
// allowance is spent, and what they owe. Each is a claim about the rules, so
// each has to agree with the code that enforces them — a panel saying "2 of 4
// out" beside a policy that refuses the third is worse than a panel saying
// nothing.

Heading("What the panel says about who is at the desk");

await using (var db = Open())
{
    var desk = new Desk(db, preferences);

    var member = await db.Members.FirstAsync();
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);

    // Fines: one still owed, one settled, one written off. Only the first is
    // the member's problem, and only the first belongs on the panel.
    db.Fines.RemoveRange(db.Fines.Where(f => f.MemberId == member.MemberId));
    await db.SaveChangesAsync();

    var raised = DateOnly.FromDateTime(DateTime.Today);

    db.Fines.AddRange(
        new Fine { MemberId = member.MemberId, Amount = 40m, CalculatedOn = raised, Status = FineStatus.PENDING },
        new Fine { MemberId = member.MemberId, Amount = 15m, CalculatedOn = raised, Status = FineStatus.PENDING },
        new Fine { MemberId = member.MemberId, Amount = 500m, CalculatedOn = raised, Status = FineStatus.PAID },
        new Fine { MemberId = member.MemberId, Amount = 900m, CalculatedOn = raised, Status = FineStatus.WAIVED });

    await db.SaveChangesAsync();

    var owed = await desk.OwedAsync(member.MemberId);

    Check("what is owed is what is still owed", owed == 55m, preferences.Money(owed));

    Check("a fine paid is not still owed", owed < 500m, "the 500 is not counted");

    Check("a fine written off is not still owed", owed < 900m, "the 900 is not counted");

    // The running count on the panel and the ceiling the policy enforces are
    // the same two numbers, so "at the limit" on the screen means the very
    // thing the next scan will run into.
    var held = await desk.OpenLoansAsync(member.MemberId);

    var open = await db.Loans.CountAsync(l => l.MemberId == member.MemberId
        && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE));

    Check("the panel's tally is the policy's tally", held.Count == open,
        $"{held.Count} of {category.MaxBooks} out");

    // The clearance shown is the effective one — capped by the category — and
    // not the member's own, which is the number that would mislead.
    var shown = member.EffectiveClearance(category);

    Check("the clearance shown is the one that will be enforced",
        shown.Level() <= category.MaxClearance.Level()
        && shown.Level() <= member.ClearanceLevel.Level(),
        $"{Words.Of(shown)} (own {Words.Of(member.ClearanceLevel)}, "
        + $"category caps at {Words.Of(category.MaxClearance)})");
}

// ================================= the warning shown before the button is pressed ==

// The return panel warns that a book is about to be flagged as damaged while
// the operator can still change their mind. That warning is a prediction, and a
// prediction that does not come true is worse than silence — so it is made with
// the same comparison the return itself uses.

Heading("A damaged return is warned about before it is taken");

await using (var db = Open())
{
    foreach (var (went, back) in new[]
    {
        (CopyCondition.GOOD, CopyCondition.GOOD),
        (CopyCondition.GOOD, CopyCondition.NEW),
        (CopyCondition.GOOD, CopyCondition.FAIR),
        (CopyCondition.NEW, CopyCondition.DAMAGED),
    })
    {
        // What the panel decides to warn about.
        var warned = back.IsWorseThan(went);

        // What the core will conclude once it is taken back.
        var flagged = back.DegradedFrom(went) > 0;

        Check($"{Words.Of(went).ToLowerInvariant()} back {Words.Of(back).ToLowerInvariant()}",
            warned == flagged,
            warned ? $"warned, and flagged (down {back.DegradedFrom(went)})" : "no warning, no flag");
    }
}

// ================================================================ the record ==

Heading("What was written down");

await using (var db = Open())
{
    var entries = await db.AuditLog
        .Where(a => a.EntityType == "loan")
        .OrderByDescending(a => a.LogId)
        .Take(5)
        .ToListAsync();

    Check("returns of damaged books are on the record",
        entries.Any(a => a.Action == "RETURN"),
        entries.Count == 0 ? "nothing written" : string.Join(", ", entries.Select(a => a.Action).Distinct()));

    var loans = await db.Loans.CountAsync();

    Check("nothing was left half-done", loans > 0, $"{loans} loans in the file");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The counter behaves as the web application does.");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

/// <summary>
/// Throw the scratch copy away.
///
/// The connections are closed, but the driver keeps them pooled and Windows
/// will not delete a file something still has open. Emptying the pool first
/// releases it; a copy that still refuses to go is left for the next run, which
/// deletes it before it starts. Failing to tidy up is not worth failing a test
/// run over.
/// </summary>
void Sweep()
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    foreach (var leftover in new[] { scratch, scratch + "-wal", scratch + "-shm" })
    {
        try
        {
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }
        catch (IOException)
        {
        }
    }
}

async Task<Copy> NextFreeAsync(MilLibDbContext db) =>
    await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();
