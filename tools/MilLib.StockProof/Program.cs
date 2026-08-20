using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using QuestPDF.Fluent;

// Counting the shelves, and what the count is set against.
//
// The figure that matters is "not found", because a book declared missing goes
// to a board and somebody has to answer for it. Declaring a book missing that
// was merely issued last Tuesday wastes a board's time and somebody's
// reputation; failing to declare one that is genuinely gone is worse. So what
// counts as expected on the shelf is checked here, case by case.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.StockProof

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

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-stock-proof.sqlite");

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

// A small shelf to count, so the arithmetic is checkable by hand. Everything
// else is put out of scope by marking it withdrawn, which is not live stock.
long[] onShelf;
long issuedCopy;
long bindingCopy;

await using (var db = Open())
{
    var ids = await db.Copies.OrderBy(c => c.CopyId).Select(c => c.CopyId).Take(12).ToListAsync();

    await db.Copies.Where(c => !ids.Contains(c.CopyId))
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CopyStatus.WITHDRAWN));

    await db.Copies.Where(c => ids.Contains(c.CopyId))
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CopyStatus.AVAILABLE));

    // One of them is out on loan, one is at the binder. Neither is on the
    // shelf, and neither is missing.
    issuedCopy = ids[10];
    bindingCopy = ids[11];

    await db.Copies.Where(c => c.CopyId == issuedCopy)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CopyStatus.ISSUED));

    await db.Copies.Where(c => c.CopyId == bindingCopy)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CopyStatus.BINDING));

    onShelf = [.. ids.Take(10)];
}

// ================================================================= counting ==

Heading("Counting a shelf of ten");

long checkId;

await using (var db = Open())
{
    var stock = new StockCheck(db);

    var check = await stock.StartAsync("Proof count", staffId, null, today);

    checkId = check.VerificationId;

    var tally = await stock.CountAsync(check);

    Check("what is expected on the shelf is counted", tally.Expected == 10,
        $"{tally.Expected} expected — the issued and the binding one are not");

    Check("and nothing has been found yet", tally.Found == 0, "0 found");

    // Scan eight of the ten.
    var barcodes = await db.Copies
        .Where(c => onShelf.Take(8).Contains(c.CopyId))
        .Select(c => c.Barcode)
        .ToListAsync();

    foreach (var barcode in barcodes)
    {
        await stock.ScanAsync(check, barcode, staffId);
    }

    tally = await stock.CountAsync(check);

    Check("each scan is counted once", tally.Found == 8, $"{tally.Found} found of 8 scanned");

    // The same book twice. It happens constantly on a shelf; it must not count
    // as two, and it must not be silently discarded either.
    var outcome = await stock.ScanAsync(check, barcodes[0], staffId);

    Check("the same book scanned twice is noticed", outcome.Result == ScanResult.DUPLICATE_SCAN,
        Words.Of(outcome.Result));

    tally = await stock.CountAsync(check);

    Check("and does not count as another find", tally.Found == 8, $"{tally.Found} found");

    Check("but is written down", tally.ScannedTwice == 1, $"{tally.ScannedTwice} scanned twice");

    // Something on the shelf the register has never heard of.
    var stray = await stock.ScanAsync(check, "NOT-A-REAL-BARCODE", staffId);

    Check("a barcode matching nothing is recorded, not dropped",
        stray.Result == ScanResult.NOT_IN_REGISTER, Words.Of(stray.Result));

    tally = await stock.CountAsync(check);

    Check("and counted separately", tally.NotInRegister == 1, $"{tally.NotInRegister} unknown");
}

// A count that survives being interrupted is the whole point of writing each
// scan down as it is made.
await using (var db = Open())
{
    var check = await db.StockVerifications.FirstAsync(v => v.VerificationId == checkId);

    var tally = await new StockCheck(db).CountAsync(check);

    Check("the count survives the application being closed", tally.Found == 8,
        $"reopened on a new connection, {tally.Found} still found");
}

// =========================================================== the moving parts ==

Heading("What happens while the count is running");

await using (var db = Open())
{
    var stock = new StockCheck(db);
    var check = await db.StockVerifications.FirstAsync(v => v.VerificationId == checkId);

    // The ninth book is issued mid-count. It is no longer on the shelf, and it
    // must not be reported missing for that reason.
    var member = await db.Members.FirstAsync();
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);
    var copy = await db.Copies.FirstAsync(c => c.CopyId == onShelf[8]);
    var title = await db.Titles.FirstAsync(t => t.TitleId == copy.TitleId);

    await new Counter(db, preferences).IssueAsync(member, category, copy, title, staffId, new IssueTerms());

    var found = await stock.ReconcileAsync(check);

    Check("a book issued during the count is not missing",
        !found.Missing.Any(m => m.Copy.CopyId == onShelf[8]),
        "it left the shelf legitimately");

    Check("and is listed as having moved",
        found.Moved.Any(m => m.Copy.CopyId == onShelf[8]),
        $"{found.Moved.Count} moved during the count");

    Check("the tenth, never scanned and never issued, is missing",
        found.Missing.Any(m => m.Copy.CopyId == onShelf[9]),
        $"{found.Missing.Count} not found");

    Check("the barcode nobody knows is reported separately",
        found.NotInRegister.Contains("NOT-A-REAL-BARCODE"),
        string.Join(", ", found.NotInRegister));
}

// ================================================================ anomalies ==

Heading("Found on the shelf but the register says otherwise");

await using (var db = Open())
{
    var stock = new StockCheck(db);
    var check = await db.StockVerifications.FirstAsync(v => v.VerificationId == checkId);

    // The book at the binder is physically on the shelf after all.
    var barcode = await db.Copies.Where(c => c.CopyId == bindingCopy)
        .Select(c => c.Barcode).FirstAsync();

    await stock.ScanAsync(check, barcode, staffId);

    var found = await stock.ReconcileAsync(check);

    Check("a copy found where the register did not expect it is flagged",
        found.Anomalies.Any(a => a.Copy.CopyId == bindingCopy),
        $"{found.Anomalies.Count} anomalies");

    Check("and does not inflate the found count",
        found.Found <= found.Expected,
        $"{found.Found} found of {found.Expected} expected");
}

// ================================================================= closing ==

Heading("Closing it");

Reconciliation closed;
StockVerification closedCheck;

await using (var db = Open())
{
    var stock = new StockCheck(db);
    var check = await db.StockVerifications.FirstAsync(v => v.VerificationId == checkId);

    closed = await stock.CloseAsync(check, "Board of Officers 7/2026", staffId, today);

    closedCheck = await db.StockVerifications.AsNoTracking()
        .FirstAsync(v => v.VerificationId == checkId);

    Check("the count is closed and dated", closedCheck.Status == VerificationStatus.COMPLETED
        && closedCheck.CompletedOn == today,
        $"{Words.Of(closedCheck.Status)}, {closedCheck.CompletedOn:dd MMM yyyy}");

    Check("the figures are frozen onto it",
        closedCheck.TotalExpected == closed.Expected
        && closedCheck.TotalFound == closed.Found
        && closedCheck.TotalMissing == closed.Missing.Count,
        $"{closedCheck.TotalFound} of {closedCheck.TotalExpected}, {closedCheck.TotalMissing} not found");

    Check("the board reference is kept", closedCheck.BoardReference == "Board of Officers 7/2026",
        closedCheck.BoardReference ?? "(none)");

    Check("closing writes nothing off",
        !await db.Copies.AnyAsync(c => onShelf.Contains(c.CopyId) && c.Status == CopyStatus.LOST),
        "no copy was marked lost — that is the board's decision");

    var note = await db.AuditLog
        .Where(a => a.Action == "STOCK_CHECK_CLOSED")
        .OrderByDescending(a => a.LogId)
        .FirstOrDefaultAsync();

    Check("and is written to the activity log", note is not null,
        note?.Details ?? "(nothing)");
}

// ================================================================ on paper ==

Heading("The shortage statement");

var unit = new Letterhead(preferences.OrganisationName, preferences.LibraryName,
    preferences.Motto, null, preferences.AccentColour);

var statement = new ShortageDocument(
    unit,
    closedCheck.Name,
    "Demo Account",
    closedCheck.StartedOn,
    closedCheck.CompletedOn,
    closedCheck.BoardReference,
    closedCheck.TotalExpected,
    closedCheck.TotalFound,
    [.. closed.Missing.Select(m => new Shortage(
        preferences.Accession(m.Copy.AccessionNo), m.Title.Name,
        Words.Of(m.Copy.Status), m.Copy.Cost))],
    closed.NotInRegister,
    preferences.CurrencySymbol);

var file = Path.Combine(into, "Shortage Statement.pdf");

try
{
    statement.GeneratePdf(file);

    Check("it renders", new FileInfo(file).Length > 1000, $"{new FileInfo(file).Length / 1024:N0} KB");
}
catch (Exception ex)
{
    Check("it renders", false, ex.Message.ReplaceLineEndings(" ").Trim());
}

var picture = Path.Combine(into, "Shortage Statement (page 1).png");

try
{
    var n = 0;

    statement.GenerateImages(_ => n++ == 0 ? picture : Path.Combine(into, $"shortage-{n}.png"),
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

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The count reconciles as it should.");
    Console.WriteLine($"  {picture}");
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
