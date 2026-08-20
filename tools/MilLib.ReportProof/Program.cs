using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using QuestPDF.Fluent;

// The reports, and the one rule that runs through all of them.
//
// No report may show material above the clearance of the person who asked for
// it. That is easy to state and easy to get wrong in one report out of six —
// and the one that is wrong is the one somebody notices in a board of enquiry,
// not in testing. So it is checked here on every report, by running each of
// them twice: once as somebody cleared for everything, once as somebody cleared
// for nothing, and comparing.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.ReportProof

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

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-report-proof.sqlite");

Sweep();
File.Copy(real, scratch);

var into = Path.Combine(Path.GetTempPath(), "Library Documents");

Directory.CreateDirectory(into);

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
string memberNo;

await using (var db = Open())
{
    staffId = await db.Users.Select(u => u.UserId).FirstAsync();
    preferences = await Preferences.ReadAsync(db);
    memberNo = await db.Members.Select(m => m.MembershipNo).FirstAsync();
}

// Something classified to find, and something overdue to count. The library as
// carried over has neither, and a report proved only against empty data is a
// report proved against nothing.
await using (var db = Open())
{
    var secret = await db.Titles.OrderBy(t => t.TitleId).Skip(1).FirstAsync();

    await db.Titles.Where(t => t.TitleId == secret.TitleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.SECRET));

    var restricted = await db.Titles.OrderBy(t => t.TitleId).Skip(2).FirstAsync();

    await db.Titles.Where(t => t.TitleId == restricted.TitleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.RESTRICTED));

    // Push the one open loan well past its date.
    await db.Loans
        .Where(l => l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.DueOn, today.AddDays(-40)));
}

async Task<Report> RunAsync(ReportKind kind, SecurityClass clearance, ReportAsk? ask = null)
{
    await using var db = Open();

    return await new Reports(db, preferences, clearance)
        .RunAsync(ask ?? new ReportAsk(kind), staffId, today);
}

// =============================================================== every one ==

Heading("Every report runs");

foreach (var kind in Enum.GetValues<ReportKind>())
{
    var ask = kind switch
    {
        ReportKind.MemberActivity => new ReportAsk(kind, Member: memberNo),
        _ => new ReportAsk(kind),
    };

    try
    {
        var report = await RunAsync(kind, SecurityClass.TOP_SECRET, ask);

        Check(Reports.Name(kind), report.Columns.Count > 0,
            $"{report.Rows.Count:N0} rows, {report.Columns.Count} columns");
    }
    catch (Exception ex)
    {
        Check(Reports.Name(kind), false, ex.Message.ReplaceLineEndings(" ").Trim());
    }
}

// ============================================================== the gate ==

Heading("What a clearance lets through");

foreach (var kind in new[] { ReportKind.Holdings, ReportKind.CopyStatus, ReportKind.Popular })
{
    var cleared = await RunAsync(kind, SecurityClass.TOP_SECRET);
    var not = await RunAsync(kind, SecurityClass.UNCLASSIFIED);

    var clearedTotal = Total(cleared);
    var notTotal = Total(not);

    Check($"{Reports.Name(kind)} counts less for the uncleared",
        notTotal <= clearedTotal,
        $"{clearedTotal:N0} cleared, {notTotal:N0} not");
}

// The holdings report names its classifications, so an uncleared reader must
// not see the word "Secret" in it at all — not even as a row with a count.
var openHoldings = await RunAsync(ReportKind.Holdings, SecurityClass.UNCLASSIFIED,
    new ReportAsk(ReportKind.Holdings, By: HoldingsBy.Classification));

Check("and never names a classification above it",
    !openHoldings.Rows.Any(r => r[0] is "Secret" or "Top Secret" or "Confidential" or "Restricted"),
    string.Join(", ", openHoldings.Rows.Select(r => r[0])));

var fullHoldings = await RunAsync(ReportKind.Holdings, SecurityClass.TOP_SECRET,
    new ReportAsk(ReportKind.Holdings, By: HoldingsBy.Classification));

Check("while somebody cleared for it does",
    fullHoldings.Rows.Any(r => r[0] == "Secret"),
    string.Join(", ", fullHoldings.Rows.Select(r => r[0])));

// The classified report is the sharpest case: run by somebody with no
// clearance it must come back empty, not merely filtered.
var noneClassified = await RunAsync(ReportKind.Classified, SecurityClass.UNCLASSIFIED);

Check("the classified report shows nothing to the uncleared",
    noneClassified.Rows.Count == 0, $"{noneClassified.Rows.Count} rows");

var someClassified = await RunAsync(ReportKind.Classified, SecurityClass.SECRET);

Check("and shows the material to somebody cleared for it",
    someClassified.Rows.Count > 0, $"{someClassified.Rows.Count} rows");

var partly = await RunAsync(ReportKind.Classified, SecurityClass.RESTRICTED);

Check("a middling clearance sees the middle and no higher",
    partly.Rows.All(r => r[2] is "Restricted"),
    partly.Rows.Count == 0 ? "nothing" : string.Join(", ", partly.Rows.Select(r => r[2]).Distinct()));

// ============================================================ the writing ==

Heading("Looking at it is written down");

await using (var db = Open())
{
    var before = await db.AuditLog.CountAsync(a => a.Action == "VIEW_CLASSIFIED");

    await new Reports(db, preferences, SecurityClass.SECRET)
        .RunAsync(new ReportAsk(ReportKind.Classified), staffId, today);

    var after = await db.AuditLog.CountAsync(a => a.Action == "VIEW_CLASSIFIED");

    Check("producing the classified report leaves a note", after == before + 1,
        $"{before} → {after}");

    var note = await db.AuditLog
        .Where(a => a.Action == "VIEW_CLASSIFIED")
        .OrderByDescending(a => a.LogId)
        .FirstAsync();

    Check("and the note says who and what", note.UserId == staffId
        && (note.Details?.Contains("classified_holdings") ?? false),
        note.Details ?? "(nothing)");
}

// ============================================================== the figures ==

Heading("What the figures say");

var overdue = await RunAsync(ReportKind.Overdue, SecurityClass.TOP_SECRET);

Check("the overdue report finds the late book", overdue.Rows.Count > 0,
    $"{overdue.Rows.Count} overdue");

if (overdue.Rows.Count > 0)
{
    var days = int.Parse(overdue.Rows[0][5].Replace(",", ""));

    Check("and counts the days from the due date, not from today",
        days is > 30 and < 45, $"{days} days late");
}

var activity = await RunAsync(ReportKind.MemberActivity, SecurityClass.TOP_SECRET,
    new ReportAsk(ReportKind.MemberActivity, Member: memberNo));

Check("member activity finds them by their number", activity.Title.Contains(memberNo),
    activity.Title);

var nobody = await RunAsync(ReportKind.MemberActivity, SecurityClass.TOP_SECRET,
    new ReportAsk(ReportKind.MemberActivity, Member: "zzz-nobody"));

Check("and says so plainly when nobody matches", nobody.Rows.Count == 0
    && (nobody.Note?.Contains("Nobody matches") ?? false), nobody.Note ?? "(nothing)");

var status = await RunAsync(ReportKind.CopyStatus, SecurityClass.TOP_SECRET);

Check("copies by state totals to the whole library",
    status.Rows.Count > 0 && status.Rows[^1][0] == "Total",
    status.Rows.Count > 0 ? $"total {status.Rows[^1][1]}" : "no rows");

// ============================================================== on paper ==

Heading("On paper, and in a spreadsheet");

var unit = new Letterhead(preferences.OrganisationName, preferences.LibraryName,
    preferences.Motto, null, preferences.AccentColour);

foreach (var kind in new[] { ReportKind.Overdue, ReportKind.Holdings, ReportKind.Classified })
{
    var ask = new ReportAsk(kind);
    var report = await RunAsync(kind, SecurityClass.TOP_SECRET, ask);
    var file = Path.Combine(into, $"Report — {Reports.Name(kind)}.pdf");

    try
    {
        new ReportDocument(unit, report).GeneratePdf(file);

        Check($"{Reports.Name(kind)} renders", new FileInfo(file).Length > 1000,
            $"{new FileInfo(file).Length / 1024:N0} KB");
    }
    catch (Exception ex)
    {
        Check($"{Reports.Name(kind)} renders", false, ex.Message.ReplaceLineEndings(" ").Trim());
    }
}

// A picture of one, to look at.
var picture = Path.Combine(into, "Report (page 1).png");

try
{
    var n = 0;

    new ReportDocument(unit, overdue)
        .GenerateImages(_ => n++ == 0 ? picture : Path.Combine(into, $"report-{n}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 150,
            });

    Check("and one renders as a picture to look at", File.Exists(picture), "written");
}
catch (Exception ex)
{
    Check("and one renders as a picture to look at", false, ex.Message.ReplaceLineEndings(" ").Trim());
}

var csv = Spreadsheet.From(overdue);

Check("the spreadsheet starts with a byte-order mark", csv.StartsWith('\uFEFF'),
    "so Excel reads the Hindi titles and the rupee sign");

Check("and has a heading row plus one line per row",
    csv.TrimEnd().Split('\n').Length == overdue.Rows.Count + 1,
    $"{csv.TrimEnd().Split('\n').Length} lines for {overdue.Rows.Count} rows");

var tricky = new Report("Awkward", ["Title"],
    [["Wellington, Waterloo"], ["He said \"stop\""]]);

var quoted = Spreadsheet.From(tricky);

Check("a comma in a title does not become two columns",
    quoted.Contains("\"Wellington, Waterloo\""), "quoted");

Check("and a quotation mark is doubled, not dropped",
    quoted.Contains("\"\"stop\"\""), "escaped");

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The reports say only what they should.");
    Console.WriteLine($"  {picture}");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

static long Total(Report report)
{
    // The last row of a counted report is its total; otherwise count the rows.
    if (report.Rows.Count > 0 && report.Rows[^1][0] == "Total")
    {
        return long.TryParse(report.Rows[^1][^1].Replace(",", ""), out var total) ? total : 0;
    }

    return report.Rows.Count;
}

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
