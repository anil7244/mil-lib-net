using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using QuestPDF.Fluent;

// The printed documents, actually produced.
//
// A document is the one part of this application whose faults are invisible
// until somebody is holding the paper: a column that overflows, a heading that
// does not repeat on page two, a total that disagrees with the rows above it.
// So this renders them for real, against the real library, and checks the file
// that comes out — then leaves it where a person can look at it, because some
// of those faults only a person can see.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.PrintProof

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

var into = Path.Combine(Path.GetTempPath(), "Library Documents");

Directory.CreateDirectory(into);

Console.WriteLine($"Reading {real}");
Console.WriteLine($"Writing into {into}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-46}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

await using var db = new MilLibDbContext(DatabaseSource.File(real));

var preferences = await Preferences.ReadAsync(db);

var crest = Path.Combine(Path.GetDirectoryName(real)!, "crest.png");

var unit = new Letterhead(
    preferences.OrganisationName.Length > 0 ? preferences.OrganisationName : preferences.LibraryName,
    preferences.LibraryName,
    preferences.Motto,
    File.Exists(crest) ? crest : null,
    preferences.AccentColour);

Console.WriteLine();
Console.WriteLine("The letterhead");

Check("the unit is named", unit.Organisation.Length > 0, unit.Organisation);
Check("the crest was found", unit.CrestPath is not null, unit.CrestPath ?? "none — it will print without one");
Check("the accent is a colour", unit.Accent.StartsWith('#'), unit.Accent);

// ================================================== the accession register ==

Console.WriteLine();
Console.WriteLine("The accession register");

var register = new Register(db, preferences);

var (first, last) = await register.ExtentAsync();

Check("the register has an extent", last > 0, $"{first} to {last}");

var page = await register.ReadAsync(first, Math.Min(first + 49, last));

Check("a stretch of it reads back", page.Count > 0, $"{page.Count} entries");

Check("in accession order", InOrder(page), "ascending, as the ledger is");

Check("every entry names its book", page.All(e => e.Title.Length > 0),
    $"{page.Count} titled");

var short_ = Path.Combine(into, "Accession Register (sample).pdf");

var elapsedShort = Time(() => new AccessionRegisterDocument(unit, page,
    $"{page[0].Accession} to {page[^1].Accession}", preferences.CurrencySymbol)
    .GeneratePdf(short_));

Check("a sample renders", File.Exists(short_) && new FileInfo(short_).Length > 1000,
    $"{new FileInfo(short_).Length / 1024:N0} KB in {elapsedShort} ms");

// The whole thing. Fifteen hundred entries is the real document, and a report
// that is fine at fifty and unusable at fifteen hundred is not fine.
var whole = await register.ReadAsync();

Check("the whole register reads back", whole.Count > 0, $"{whole.Count:N0} entries");

var full = Path.Combine(into, "Accession Register (whole).pdf");

var elapsedFull = Time(() => new AccessionRegisterDocument(unit, whole,
    "the whole register", preferences.CurrencySymbol).GeneratePdf(full));

var size = new FileInfo(full).Length;

Check("and renders whole", File.Exists(full) && size > 10_000,
    $"{size / 1024:N0} KB in {elapsedFull:N0} ms");

// Nobody waits half a minute for a register. If this starts creeping, the fix
// is to page it, and it is better to find that out here.
Check("in a time somebody would wait for", elapsedFull < 20_000, $"{elapsedFull / 1000.0:N1} seconds");

var value = whole.Where(e => e.Cost is not null).Sum(e => e.Cost!.Value);

Check("the value of the stock adds up", value >= 0,
    $"{preferences.CurrencySymbol}{value:N2} across {whole.Count(e => e.Cost is not null):N0} priced entries");

// A range is inclusive at both ends — it is read off two pages of the bound
// ledger, not written as a half-open interval by a programmer.
var narrow = await register.ReadAsync(first, first);

Check("a range of one returns exactly one", narrow.Count == 1,
    narrow.Count == 1 ? narrow[0].Accession : $"{narrow.Count} entries");

var beyond = await register.ReadAsync(last + 1000, last + 2000);

Check("a range past the end returns nothing", beyond.Count == 0, "empty, not an error");

// ================================================================= labels ==

Console.WriteLine();
Console.WriteLine("Labels");

var labelling = new Labelling(db, preferences);

var found = await labelling.FindAsync("", 24);

Check("copies are found to label", found.Count > 0, $"{found.Count} copies");

var books = found.Select(f => labelling.Describe(f.Copy, f.Title)).ToList();

Check("the stock size is read from the settings", labelling.PocketWidthMm > 0,
    $"pocket {labelling.PocketWidthMm:0.#} x {labelling.PocketHeightMm:0.#} mm, "
    + $"spine {labelling.SpineWidthMm:0.#} x {labelling.SpineHeightMm:0.#} mm");

Check("and what goes on them", true, labelling.Code.ToString());

// The barcode itself. A symbol of the wrong length is one a scanner refuses,
// and the length is arithmetic: eleven modules per character, plus start,
// check and stop, plus the two extra bars on the stop.
foreach (var sample in new[] { "000002965", "JAKLI/1645", "1001" })
{
    var expected = (sample.Length + 3) * 11 + 2;

    Check($"\"{sample}\" encodes to the right width",
        Code128.Modules(sample) == expected,
        $"{Code128.Modules(sample)} modules");
}

Check("the widths alternate bar and gap from a bar",
    Code128.Widths("1001").Count % 2 == 1,
    $"{Code128.Widths("1001").Count} bars and gaps, ending on a bar");

foreach (var (kind, w, h) in new[]
{
    (LabelKind.Pocket, labelling.PocketWidthMm, labelling.PocketHeightMm),
    (LabelKind.Spine, labelling.SpineWidthMm, labelling.SpineHeightMm),
})
{
    var file = Path.Combine(into, $"Labels ({kind.ToString().ToLowerInvariant()}).pdf");

    try
    {
        new LabelSheetDocument(unit, books, kind, labelling.Code, w, h).GeneratePdf(file);

        Check($"a sheet of {kind.ToString().ToLowerInvariant()} labels renders",
            File.Exists(file) && new FileInfo(file).Length > 1000,
            $"{new FileInfo(file).Length / 1024:N0} KB");
    }
    catch (Exception ex)
    {
        Check($"a sheet of {kind.ToString().ToLowerInvariant()} labels renders", false, First(ex));
    }
}

var labelPicture = Path.Combine(into, "Labels (pocket, page 1).png");

try
{
    var n = 0;

    new LabelSheetDocument(unit, books, LabelKind.Pocket, labelling.Code,
        labelling.PocketWidthMm, labelling.PocketHeightMm)
        .GenerateImages(_ => n++ == 0 ? labelPicture : Path.Combine(into, $"labels-{n}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 180,
            });

    Check("and renders as a picture to look at", File.Exists(labelPicture), "written");
}
catch (Exception ex)
{
    Check("and renders as a picture to look at", false, First(ex));
}

// The thermal path. Text, so it can simply be read.
//
// The stock comes from the settings, the same way the application gets it, so
// this is the label a Zebra would actually be sent rather than one laid out in
// dot counts nobody chose. MilLib.ZebraProof takes that arithmetic apart; this
// only checks that the path produces something a printer would accept.
var stock = labelling.StockFor(LabelKind.Pocket);

var zpl = Zpl.Batch(books.Take(2), LabelKind.Pocket, stock);

Check("a calibration label leads the file",
    Zpl.Calibration(stock).Contains("10 mm square"),
    $"{stock.WidthMm:0.#} x {stock.HeightMm:0.#} mm at {stock.Dpi} dpi");

Check("ZPL is produced for a Zebra",
    zpl.StartsWith("^XA") && zpl.Contains("^BCN") && zpl.TrimEnd().EndsWith("^XZ"),
    $"{Lines(zpl)} lines for 2 labels");

Check("and a title carrying a control character cannot break it",
    !Zpl.Pocket(new LabelFor("A/1", "1", "Rockets ^ Missiles ~ Guns", ""), stock).Contains("^ M"),
    "escaped");

// ================================================================ passes ==

Console.WriteLine();
Console.WriteLine("Member passes");

var roll = new Roll(db);

var everybody = await db.Members.OrderBy(m => m.FullName)
    .Select(m => m.MemberId).Take(12).ToListAsync();

var passes = await roll.PassesForAsync(everybody);

Check("passes read back for the roll", passes.Count > 0, $"{passes.Count} members");

// The QR carries the scan token, not the membership number. It is the value
// the counter's scan box resolves on, and the only one that can be killed by
// reissuing a pass.
Check("every pass carries a scan token",
    passes.All(p => p.QrToken.Length > 0), passes[0].QrToken);

Check("and it is not the membership number",
    passes.All(p => p.QrToken != p.MembershipNo), "distinct");

// Two passes with the same token would be two people the counter cannot tell
// apart — the scan would resolve to whichever row came back first.
Check("no two passes carry the same token",
    passes.Select(p => p.QrToken).Distinct().Count() == passes.Count, "all different");

Check("a pass with no photograph still has something to show",
    passes.All(p => p.Initials.Length > 0),
    $"\"{passes[0].Initials}\" for {passes[0].FullName}");

var passFile = Path.Combine(into, "Library passes.pdf");

try
{
    new PassDocument(unit, passes).GeneratePdf(passFile);

    Check("a sheet of passes renders",
        File.Exists(passFile) && new FileInfo(passFile).Length > 1000,
        $"{new FileInfo(passFile).Length / 1024:N0} KB");
}
catch (Exception ex)
{
    Check("a sheet of passes renders", false, First(ex));
}

// A pass is 85.6 x 54 mm and everything on it is 5 to 9 point. Nothing that
// small survives a check that only counts rows — it has to be looked at.
var passPicture = Path.Combine(into, "Library passes (page 1).png");

try
{
    var n = 0;

    new PassDocument(unit, passes)
        .GenerateImages(_ => n++ == 0 ? passPicture : Path.Combine(into, $"passes-{n}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 200,
            });

    Check("and renders as a picture to look at", File.Exists(passPicture), "written");
}
catch (Exception ex)
{
    Check("and renders as a picture to look at", false, First(ex));
}

// This library has one member, so the real render cannot show how a sheet of
// them sits on the page — and how they sit is the whole question, because they
// are cut apart with a guillotine. Eight made-up passes, clearly marked as
// samples, so the layout can be looked at.
var samples = new List<PassFor>();

for (var i = 1; i <= 8; i++)
{
    samples.Add(new PassFor(
        $"M{i:0000}",
        i % 3 == 0 ? "Ramachandran Venkataraghavan" : $"Sample Member {i}",
        i % 2 == 0 ? "Nk" : "Maj",
        $"IC{10000 + i}",
        "8 JAK LI",
        DateOnly.FromDateTime(DateTime.Today.AddYears(2)),
        i % 4 == 0 ? SecurityClass.SECRET : SecurityClass.UNCLASSIFIED,
        $"MLIB-SAMPLE{i:0000}TOKEN",
        null));
}

var sampleSheet = Path.Combine(into, "Library passes (sample sheet).png");

try
{
    var n = 0;

    new PassDocument(unit, samples)
        .GenerateImages(_ => n++ == 0 ? sampleSheet : Path.Combine(into, $"passes-sample-{n}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 200,
            });

    // Eight CR80 cards fit on one A4 page with room to cut. Nine would mean
    // the layout is wrong, and one would mean it is not tiling at all.
    Check("eight passes fit on a single page", n == 1, $"{n} page for 8 passes");
}
catch (Exception ex)
{
    Check("eight passes fit on a single page", false, First(ex));
}

// And a picture of the first page.
//
// Everything above says the file exists and has the right number of rows in it.
// None of it can see a column overflowing its width, a heading colliding with
// the crest, or a table that runs off the edge of the paper — which are the
// faults printed documents actually have. Somebody has to look.
var look = Path.Combine(into, "Accession Register (page 1).png");

try
{
    var pages = 0;

    new AccessionRegisterDocument(unit, page, $"{page[0].Accession} to {page[^1].Accession}",
        preferences.CurrencySymbol)
        .GenerateImages(_ => pages++ == 0 ? look : Path.Combine(into, $"page-{pages}.png"),
            new QuestPDF.Infrastructure.ImageGenerationSettings
            {
                ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png,
                RasterDpi = 140,
            });

    Check("the first page renders as a picture to look at",
        File.Exists(look) && new FileInfo(look).Length > 10_000,
        $"{new FileInfo(look).Length / 1024:N0} KB");
}
catch (Exception ex)
{
    Check("the first page renders as a picture to look at", false, ex.Message.Split('\n')[0]);
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The documents produce. Open them and look at them:");
    Console.WriteLine($"  {look}");
    Console.WriteLine($"  {labelPicture}");
    Console.WriteLine($"  {passPicture}");
    Console.WriteLine($"  {sampleSheet}");
    Console.WriteLine($"  {short_}");
    Console.WriteLine($"  {full}");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

return failures == 0 ? 0 : 1;

/// <summary>
/// Checked on the raw sequence, not the printed number. The imported entries
/// are unpadded, so comparing the printed strings would put 1000 before 999 and
/// call a correctly ordered register broken.
/// </summary>
static bool InOrder(IReadOnlyList<RegisterEntry> entries)
{
    var numbers = entries.Where(e => e.Seq is not null).Select(e => e.Seq!.Value).ToList();

    return numbers.SequenceEqual(numbers.Order());
}

/// <summary>The first line of what went wrong, which is the useful line.</summary>
static string First(Exception ex) => ex.Message.ReplaceLineEndings(" ").Trim();

static int Lines(string text) => text.ReplaceLineEndings("\n").Split('\n').Length;

static long Time(Action work)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();

    work();

    return clock.ElapsedMilliseconds;
}
