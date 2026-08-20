using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// The roll and the lending rules, checked against the rules the PHP application
// enforces.
//
// Two of these matter more than the rest. A member must never be given a
// clearance above what their category allows — that is the rule the whole
// security model rests on, and the form is where it is caught rather than the
// counter. And nobody is signed off with a book still out or a fine still
// owing, because a no-dues chit that skips either is worth nothing.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.RollProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-roll-proof.sqlite");

Sweep();
File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-48}  {saw}");

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

long staffId;
Preferences preferences;

await using (var db = Open())
{
    staffId = await db.Users.Select(u => u.UserId).FirstAsync();
    preferences = await Preferences.ReadAsync(db);
}

// ================================================================ enrolling ==

Heading("Enrolling somebody");

long enrolledId;

await using (var db = Open())
{
    var roll = new Roll(db);
    var category = await db.MemberCategories.OrderBy(c => c.CategoryId).FirstAsync();

    var suggested = await roll.SuggestedNumberAsync();

    Check("a membership number is suggested", suggested.StartsWith('M') && suggested.Length >= 5, suggested);

    var member = new Member
    {
        MembershipNo = suggested,
        FullName = "TEST PERSON",
        Rank = "HAV",
        CategoryId = category.CategoryId,
        ClearanceLevel = SecurityClass.UNCLASSIFIED,
        Status = MemberStatus.ACTIVE,
        EnrolledOn = today,
    };

    var problems = await roll.ProblemsWithAsync(member, category);

    Check("a sound record has nothing wrong with it", problems.Count == 0,
        problems.Count == 0 ? "clear" : string.Join("; ", problems));

    await roll.EnrolAsync(member, staffId);

    enrolledId = member.MemberId;

    Check("the pass carries a long random code",
        member.QrToken.StartsWith("MLIB-") && member.QrToken.Length == 33,
        member.QrToken[..14] + "…");

    // The token is what a printed pass is worth. Two members with the same one
    // would each open the other's record at the counter.
    var tokens = await db.Members.Select(m => m.QrToken).ToListAsync();

    Check("no two passes carry the same code", tokens.Distinct().Count() == tokens.Count,
        $"{tokens.Count} members, {tokens.Distinct().Count()} distinct codes");
}

await using (var db = Open())
{
    var roll = new Roll(db);
    var category = await db.MemberCategories.OrderBy(c => c.CategoryId).FirstAsync();
    var existing = await db.Members.FirstAsync(m => m.MemberId == enrolledId);

    var clash = new Member
    {
        MembershipNo = existing.MembershipNo,
        FullName = "SOMEBODY ELSE",
        CategoryId = category.CategoryId,
        EnrolledOn = today,
    };

    var problems = await roll.ProblemsWithAsync(clash, category);

    Check("a membership number cannot be used twice",
        problems.Any(p => p.Contains("already belongs")),
        problems.FirstOrDefault(p => p.Contains("already belongs")) ?? "let through");

    // The same record checked against itself must not report a clash with
    // itself, or nobody could ever edit their own phone number.
    var itself = await roll.ProblemsWithAsync(existing, category);

    Check("but a member may keep their own", itself.Count == 0,
        itself.Count == 0 ? "clear" : string.Join("; ", itself));
}

// ================================================================ clearance ==

Heading("The clearance ceiling");

await using (var db = Open())
{
    var roll = new Roll(db);

    var narrow = await db.MemberCategories
        .Where(c => c.MaxClearance == SecurityClass.UNCLASSIFIED)
        .FirstOrDefaultAsync();

    if (narrow is null)
    {
        Check("a category with an unclassified ceiling exists", false, "none in this file");
    }
    else
    {
        var overreaching = new Member
        {
            MembershipNo = "TEST-CLEARANCE",
            FullName = "TEST PERSON",
            CategoryId = narrow.CategoryId,
            ClearanceLevel = SecurityClass.SECRET,
            EnrolledOn = today,
        };

        var problems = await roll.ProblemsWithAsync(overreaching, narrow);

        Check("a clearance above the category is refused",
            problems.Any(p => p.Contains("allows at most")),
            problems.FirstOrDefault(p => p.Contains("allows at most")) ?? "let through");

        // Caught at the form, so it never reaches the counter as a surprise.
        overreaching.ClearanceLevel = SecurityClass.UNCLASSIFIED;

        Check("and at or below it is fine",
            !(await roll.ProblemsWithAsync(overreaching, narrow)).Any(p => p.Contains("allows at most")),
            "accepted");
    }

    var backwards = new Member
    {
        MembershipNo = "TEST-DATES",
        FullName = "TEST PERSON",
        CategoryId = (await db.MemberCategories.FirstAsync()).CategoryId,
        EnrolledOn = today,
        ValidUpto = today.AddDays(-1),
    };

    var dateProblems = await roll.ProblemsWithAsync(backwards,
        await db.MemberCategories.FirstAsync());

    Check("membership cannot expire before it starts",
        dateProblems.Any(p => p.Contains("before it starts")),
        dateProblems.FirstOrDefault(p => p.Contains("before it starts")) ?? "let through");
}

// ================================================================== no dues ==

Heading("Signing somebody off");

await using (var db = Open())
{
    var roll = new Roll(db);

    var member = await db.Members.FirstAsync(m => m.MemberId == enrolledId);
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);

    var clean = await roll.OutstandingForAsync(member, category, today);

    Check("somebody with nothing out can be signed off", clean.Eligible,
        $"{clean.OpenLoans.Count} books, {preferences.Money(clean.Total)} owed");

    // Give them a book, and the answer must change.
    var copy = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    var loan = await new Counter(db, preferences)
        .IssueAsync(member, category, copy, title, staffId, new IssueTerms());

    var holding = await roll.OutstandingForAsync(member, category, today);

    Check("a book still out blocks it", !holding.Eligible,
        $"{holding.OpenLoans.Count} book(s) out");

    // Backdate it and the accrual has to show, without any fine row existing.
    await db.Loans.Where(l => l.LoanId == loan.LoanId)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.DueOn, today.AddDays(-(category.GraceDays + 5))));

    var late = await roll.OutstandingForAsync(member, category, today);

    Check("a fine accruing on a late book is counted before it is raised",
        late.Accrued == 5 * category.FinePerDay,
        $"{preferences.Money(late.Accrued)} accruing, nothing raised yet");

    // Take it back; the fine becomes real, and still blocks.
    var fresh = await db.Loans.FirstAsync(l => l.LoanId == loan.LoanId);
    var freshCopy = await db.Copies.FirstAsync(c => c.CopyId == copy.CopyId);

    await new Counter(db, preferences)
        .ReturnAsync(fresh, freshCopy, title, category, fresh.IssueCondition, null, staffId);

    var owing = await roll.OutstandingForAsync(member, category, today);

    if (preferences.Has(Feature.Fines))
    {
        Check("an unpaid fine blocks it even with nothing out",
            !owing.Eligible && owing.OpenLoans.Count == 0,
            $"nothing out, {preferences.Money(owing.Total)} owed");

        // Settle it, and only then may they go.
        await db.Fines.Where(f => f.MemberId == member.MemberId && f.Status == FineStatus.PENDING)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FineStatus.PAID));
    }

    var settled = await roll.OutstandingForAsync(member, category, today);

    Check("with both settled, they may be signed off", settled.Eligible,
        "nothing out, nothing owed");

    await roll.ClearAsync(member, staffId, today);

    var after = await db.Members.AsNoTracking().FirstAsync(m => m.MemberId == member.MemberId);

    Check("signing off stamps the date and the standing",
        after.Status == MemberStatus.POSTED_OUT && after.ClearedOn == today,
        $"{Words.Of(after.Status)}, cleared {after.ClearedOn:dd MMM yyyy}");
}

// ================================================================= removal ==

Heading("What cannot be deleted");

await using (var db = Open())
{
    var roll = new Roll(db);

    var borrowed = await db.Members.FirstAsync(m => m.MemberId == enrolledId);

    Check("a member who has borrowed is kept",
        await roll.WhyNotRemovableAsync(borrowed) is not null,
        (await roll.WhyNotRemovableAsync(borrowed))?[..48] + "…");

    // Somebody entered by mistake, who never borrowed anything, may go.
    var mistake = new Member
    {
        MembershipNo = "TEST-MISTAKE",
        FullName = "ENTERED BY MISTAKE",
        CategoryId = (await db.MemberCategories.FirstAsync()).CategoryId,
        EnrolledOn = today,
    };

    await roll.EnrolAsync(mistake, staffId);

    Check("but one who never borrowed may go",
        await roll.WhyNotRemovableAsync(mistake) is null, "removable");

    await roll.RemoveAsync(mistake, staffId);

    Check("and is gone",
        !await db.Members.AnyAsync(m => m.MembershipNo == "TEST-MISTAKE"), "removed");
}

// =========================================================== lending rules ==

Heading("The lending rules");

await using (var db = Open())
{
    var policies = new Policies(db);

    var all = await policies.AllAsync();

    Check("every category says how many it governs", all.Count > 0,
        string.Join(", ", all.Select(a => $"{a.Category.Name} ({a.Members})")));

    var used = all.FirstOrDefault(a => a.Members > 0);

    if (used.Category is not null)
    {
        Check("a category with members in it cannot be deleted",
            await policies.WhyNotRemovableAsync(used.Category) is not null,
            await policies.WhyNotRemovableAsync(used.Category) ?? "let through");
    }

    var fresh = Policies.Fresh();

    Check("a new category starts somewhere sensible",
        fresh.MaxBooks > 0 && fresh.LoanDays > 0,
        $"{fresh.MaxBooks} books, {fresh.LoanDays} days, {fresh.MaxRenewals} renewals");

    var nameless = Policies.Fresh();

    var problems = await policies.ProblemsWithAsync(nameless);

    Check("a category needs a name and a code", problems.Count >= 2,
        string.Join("; ", problems));

    var unlendable = Policies.Fresh();
    unlendable.Name = "Test";
    unlendable.Code = "TESTCODE";
    unlendable.MaxBooks = 0;

    Check("a category nobody could borrow from says so",
        (await policies.ProblemsWithAsync(unlendable)).Any(p => p.Contains("could borrow")),
        "flagged");

    var clash = Policies.Fresh();
    clash.Name = "Clash";
    clash.Code = all[0].Category.Code;

    Check("a code cannot be used twice",
        (await policies.ProblemsWithAsync(clash)).Any(p => p.Contains("already in use")),
        $"code {clash.Code}");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The roll and the lending rules behave as the web application does.");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

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
