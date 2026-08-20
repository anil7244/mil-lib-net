using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using QuestPDF.Fluent;

// Taking books off the books.
//
// This is the only way a copy leaves live stock, and it is irreversible in
// practice: the accession number is retired and the row stays for ever. So the
// things worth checking are what it refuses to do — condemn a book somebody is
// holding, reissue a retired number, delete anything — and what it does to the
// borrower when a book really is written off against them.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.WithdrawalProof

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-withdrawal-proof.sqlite");

Sweep();
File.Copy(real, scratch);

var into = Path.Combine(Path.GetTempPath(), "Library Documents");

Directory.CreateDirectory(into);

Console.WriteLine($"A scratch copy of {real}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-52}  {saw}");

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

// ================================================================ numbering ==

Heading("The proceedings");

await using (var db = Open())
{
    var withdrawals = new Withdrawals(db, preferences);

    var number = await withdrawals.NextNumberAsync();

    Check("a withdrawal number is suggested", number.StartsWith("WD-") && number.Length == 8, number);

    var copies = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE)
        .OrderBy(c => c.CopyId)
        .Take(3)
        .ToListAsync();

    var board = new Condemnation(WithdrawalReason.DAMAGED, today, number,
        "BOP 3/2026", "Commanding Officer", today, Remarks: "Water damage in store.");

    var problems = await withdrawals.ProblemsWithAsync(board, copies);

    Check("a sound condemnation has nothing wrong with it", problems.Count == 0,
        problems.Count == 0 ? "clear" : string.Join("; ", problems));

    var withdrawal = await withdrawals.WithdrawAsync(board, copies, staffId);

    Check("the copies are marked withdrawn, not deleted",
        await db.Copies.CountAsync(c => copies.Select(x => x.CopyId).Contains(c.CopyId)) == 3,
        "all three rows still there");

    var after = await db.Copies.AsNoTracking()
        .Where(c => copies.Select(x => x.CopyId).Contains(c.CopyId))
        .ToListAsync();

    Check("each is marked withdrawn and dated",
        after.All(c => c.Status == CopyStatus.WITHDRAWN && c.WithdrawnAt == today),
        $"{after.Count} withdrawn {today:dd MMM yyyy}");

    Check("and linked to the proceedings that condemned it",
        after.All(c => c.WithdrawalId == withdrawal.WithdrawalId),
        withdrawal.WithdrawalNo);

    Check("the value written off is the sum of what they cost",
        withdrawal.TotalValue == copies.Sum(c => c.Cost ?? 0),
        preferences.Money(withdrawal.TotalValue));

    // Numbering is the point of the whole exercise.
    var accession = await db.AccessionCounters.Select(a => a.NextSeq).FirstAsync();

    Check("the accession counter did not go backwards",
        accession > after.Max(c => c.AccessionSeq ?? 0),
        $"next is {accession}");

    var duplicate = await withdrawals.ProblemsWithAsync(
        board with { Number = withdrawal.WithdrawalNo }, copies);

    Check("a withdrawal number cannot be used twice",
        duplicate.Any(p => p.Contains("already been used")),
        duplicate.FirstOrDefault(p => p.Contains("already been used")) ?? "let through");
}

// ============================================================ what it refuses ==

Heading("What it will not do");

await using (var db = Open())
{
    var withdrawals = new Withdrawals(db, preferences);

    // Issue a book, then try to condemn it as damaged.
    var member = await db.Members.FirstAsync();
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);

    var copy = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    await new Counter(db, preferences).IssueAsync(member, category, copy, title, staffId, new IssueTerms());

    var held = await db.Copies.FirstAsync(c => c.CopyId == copy.CopyId);

    var damaged = new Condemnation(WithdrawalReason.DAMAGED, today);

    var problems = await withdrawals.ProblemsWithAsync(damaged, [held]);

    Check("a book somebody is holding cannot be condemned as damaged",
        problems.Any(p => p.Contains("Somebody is holding")),
        problems.FirstOrDefault(p => p.Contains("Somebody is holding")) ?? "let through");

    // The same book as a loss is a different decision, and is allowed.
    var lost = new Condemnation(WithdrawalReason.LOST, today);

    Check("but the same book may be written off as lost",
        (await withdrawals.ProblemsWithAsync(lost, [held])).Count == 0, "allowed");

    Check("an empty condemnation is refused",
        (await withdrawals.ProblemsWithAsync(damaged, [])).Any(p => p.Contains("at least one")),
        "refused");

    var superseded = new Condemnation(WithdrawalReason.SUPERSEDED, today);

    Check("a superseded book must say what replaced it",
        (await withdrawals.ProblemsWithAsync(superseded, [held])).Any(p => p.Contains("replaced by")),
        "refused");

    // An already-withdrawn copy must not come back as a candidate.
    var gone = await db.Copies.Where(c => c.Status == CopyStatus.WITHDRAWN)
        .Select(c => c.AccessionNo).FirstAsync();

    Check("an already-withdrawn copy cannot be condemned again",
        (await withdrawals.FindAsync([gone])).Count == 0, gone);
}

// ============================================================== a real loss ==

Heading("Writing a book off against the borrower");

await using (var db = Open())
{
    var withdrawals = new Withdrawals(db, preferences);

    var loan = await db.Loans
        .Where(l => l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE)
        .OrderByDescending(l => l.LoanId)
        .FirstAsync();

    var copy = await db.Copies.FirstAsync(c => c.CopyId == loan.CopyId);

    var owedBefore = await db.Fines
        .Where(f => f.MemberId == loan.MemberId && f.Status == FineStatus.PENDING)
        .SumAsync(f => (decimal?)f.Amount) ?? 0m;

    var board = new Condemnation(WithdrawalReason.LOST, today,
        BoardProceedings: "BOP 4/2026", LossAmount: 250m);

    await withdrawals.WithdrawAsync(board, [copy], staffId);

    var closed = await db.Loans.AsNoTracking().FirstAsync(l => l.LoanId == loan.LoanId);

    Check("the loan is closed as lost", closed.Status == LoanStatus.LOST, Words.Of(closed.Status));

    if (preferences.Has(Feature.Fines))
    {
        var owedAfter = await db.Fines
            .Where(f => f.MemberId == loan.MemberId && f.Status == FineStatus.PENDING)
            .SumAsync(f => (decimal?)f.Amount) ?? 0m;

        Check("and the loss is charged to whoever had it",
            owedAfter == owedBefore + 250m,
            $"{preferences.Money(owedBefore)} → {preferences.Money(owedAfter)}");

        var raised = await db.Fines
            .Where(f => f.LoanId == loan.LoanId && f.Type == FineType.LOSS)
            .FirstOrDefaultAsync();

        Check("the charge says what it was for",
            raised?.Remarks?.Contains("withdrawn under") == true,
            raised?.Remarks ?? "(nothing)");
    }

    var after = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == copy.CopyId);

    Check("the copy leaves stock", after.Status == CopyStatus.WITHDRAWN, Words.Of(after.Status));
}

// ================================================================ superseding ==

Heading("A later edition");

await using (var db = Open())
{
    var withdrawals = new Withdrawals(db, preferences);

    var old = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    var replacement = await db.Titles
        .Where(t => t.TitleId != old.TitleId)
        .OrderBy(t => t.TitleId)
        .FirstAsync();

    await withdrawals.WithdrawAsync(
        new Condemnation(WithdrawalReason.SUPERSEDED, today, ReplacedBy: replacement.TitleId),
        [old], staffId);

    var oldTitle = await db.Titles.AsNoTracking().FirstAsync(t => t.TitleId == old.TitleId);

    Check("the old edition points at the one that replaced it",
        oldTitle.SupersededBy == replacement.TitleId,
        $"\"{Shorten(oldTitle.Name)}\" → \"{Shorten(replacement.Name)}\"");
}

// ================================================================ the register ==

Heading("The register");

await using (var db = Open())
{
    var register = await new Withdrawals(db, preferences).RegisterAsync();

    Check("every withdrawn copy is in it", register.Count >= 5, $"{register.Count} copies");

    Check("each says which proceedings condemned it",
        register.All(r => r.Under is not null),
        $"{register.Count(r => r.Under is not null)} of {register.Count} linked");

    Check("and it is ordered by the proceedings",
        register.Select(r => r.Copy.WithdrawalId ?? 0)
            .SequenceEqual(register.Select(r => r.Copy.WithdrawalId ?? 0).Order()),
        "ascending");
}

// ================================================================= on paper ==

Heading("The certificate");

await using (var db = Open())
{
    var withdrawals = new Withdrawals(db, preferences);

    var withdrawal = await db.Withdrawals.OrderBy(w => w.WithdrawalId).FirstAsync();

    var books = await withdrawals.UnderAsync(withdrawal.WithdrawalId);

    var unit = new Letterhead(preferences.OrganisationName, preferences.LibraryName,
        preferences.Motto, null, preferences.AccentColour);

    var document = new CondemnationDocument(unit, withdrawal, "Demo Account",
        [.. books.Select(b => new Condemned(
            preferences.Accession(b.Copy.AccessionNo), b.Title.Name,
            Words.Of(b.Copy.Source), b.Copy.Cost, ""))],
        preferences.CurrencySymbol);

    var file = Path.Combine(into, "Certificate of Condemnation.pdf");

    try
    {
        document.GeneratePdf(file);

        Check("it renders", new FileInfo(file).Length > 1000, $"{new FileInfo(file).Length / 1024:N0} KB");
    }
    catch (Exception ex)
    {
        Check("it renders", false, ex.Message.ReplaceLineEndings(" ").Trim());
    }

    var picture = Path.Combine(into, "Certificate of Condemnation (page 1).png");

    try
    {
        var n = 0;

        document.GenerateImages(_ => n++ == 0 ? picture : Path.Combine(into, $"cert-{n}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 150,
            });

        Check("and renders as a picture to look at", File.Exists(picture), "written");
    }
    catch (Exception ex)
    {
        Check("and renders as a picture to look at", false, ex.Message.ReplaceLineEndings(" ").Trim());
    }
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("Books leave the books properly.");
    Console.WriteLine($"  {Path.Combine(into, "Certificate of Condemnation (page 1).png")}");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

static string Shorten(string text) => text.Length <= 30 ? text : text[..27] + "…";

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
