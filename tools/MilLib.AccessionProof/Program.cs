using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Accession numbers, and what may be changed about a copy afterwards.
//
// The accession number is the library's statutory identity for one physical
// book. Sequential, gap-free, never reused. A register with a gap in it, or one
// with the same number against two books, is a register somebody has to stand
// up and explain — so the guarantee is worth checking rather than assuming,
// including when two people accession at the same moment.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.AccessionProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-accession-proof.sqlite");

Sweep();
File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-50}  {saw}");

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
long titleId;
Preferences preferences;

await using (var db = Open())
{
    staffId = await db.Users.Select(u => u.UserId).FirstAsync();
    titleId = await db.Titles.OrderBy(t => t.TitleId).Select(t => t.TitleId).FirstAsync();
    preferences = await Preferences.ReadAsync(db);
}

Copy Pattern() => new()
{
    AccessionDate = today,
    Source = CopySource.PURCHASE,
    Condition = CopyCondition.NEW,
    IsCirculating = true,
};

// ============================================================== the numbers ==

Heading("Handing out numbers");

int firstSeq;

await using (var db = Open())
{
    var accession = new Accession(db, preferences);

    var peeked = await accession.PeekAsync();

    var made = await accession.AccessionAsync(titleId, 1, Pattern(), staffId);

    firstSeq = made[0].AccessionSeq!.Value;

    Check("the number handed out is the one that was promised", firstSeq == peeked,
        $"peeked {peeked}, got {firstSeq}");

    Check("the counter moved on by exactly one", await accession.PeekAsync() == firstSeq + 1,
        $"next is {await accession.PeekAsync()}");

    Check("it is stored padded, without the prefix",
        made[0].AccessionNo == accession.Format(firstSeq) && !made[0].AccessionNo.Contains('/'),
        made[0].AccessionNo);

    Check("the barcode carries the same number", made[0].Barcode == made[0].AccessionNo,
        made[0].Barcode);

    Check("and it goes on the shelf available", made[0].Status == CopyStatus.AVAILABLE,
        Words.Of(made[0].Status));
}

await using (var db = Open())
{
    var accession = new Accession(db, preferences);

    var before = await accession.PeekAsync();

    // A box of eight identical books on one bill, which is when somebody
    // actually reaches for this.
    var batch = await accession.AccessionAsync(titleId, 8, Pattern(), staffId);

    var numbers = batch.Select(c => c.AccessionSeq!.Value).ToList();

    Check("a batch runs in an unbroken sequence",
        numbers.SequenceEqual(Enumerable.Range(before, 8)),
        $"{numbers[0]} to {numbers[^1]}");

    Check("the counter moved on by the whole batch",
        await accession.PeekAsync() == before + 8,
        $"next is {await accession.PeekAsync()}");
}

// ============================================================= never reused ==

Heading("A number is never handed out twice");

await using (var db = Open())
{
    var accession = new Accession(db, preferences);

    // Withdraw the first copy made. Its number must not come back.
    var withdrawn = await db.Copies.FirstAsync(c => c.AccessionSeq == firstSeq);

    await db.Copies.Where(c => c.CopyId == withdrawn.CopyId)
        .ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, CopyStatus.WITHDRAWN)
            .SetProperty(c => c.WithdrawnAt, today));

    var next = await accession.AccessionAsync(titleId, 1, Pattern(), staffId);

    Check("a withdrawn copy's number is not reissued",
        next[0].AccessionSeq!.Value > firstSeq,
        $"{firstSeq} withdrawn, next given out was {next[0].AccessionSeq}");

    var all = await db.Copies
        .Where(c => c.AccessionSeq != null)
        .Select(c => c.AccessionSeq!.Value)
        .ToListAsync();

    Check("no two copies share a number", all.Distinct().Count() == all.Count,
        $"{all.Count} numbered copies, {all.Distinct().Count()} distinct");
}

// ============================================================== at once ==

Heading("Two people accessioning at the same moment");

await using (var db = Open())
{
    var before = await new Accession(db, preferences).PeekAsync();

    // Eight callers, each on its own connection, each asking for three. If the
    // counter were read before it were bumped, they would overlap and the
    // unique index would turn that into a failed save with somebody standing
    // at the desk.
    var racers = Enumerable.Range(0, 8).Select(async _ =>
    {
        await using var mine = Open();

        var made = await new Accession(mine, preferences)
            .AccessionAsync(titleId, 3, Pattern(), staffId);

        return made.Select(c => c.AccessionSeq!.Value).ToList();
    });

    List<int>[] results;

    try
    {
        results = await Task.WhenAll(racers);
    }
    catch (Exception ex)
    {
        Check("eight at once all succeed", false, ex.Message.Split('\n')[0]);

        results = [];
    }

    if (results.Length > 0)
    {
        var handed = results.SelectMany(r => r).OrderBy(n => n).ToList();

        Check("eight at once all succeed", handed.Count == 24, $"{handed.Count} numbers handed out");

        Check("none of them collide", handed.Distinct().Count() == handed.Count,
            $"{handed.Distinct().Count()} distinct");

        Check("and the run has no gaps in it",
            handed.SequenceEqual(Enumerable.Range(before, 24)),
            $"{handed[0]} to {handed[^1]}, unbroken");

        Check("each caller's own three are consecutive",
            results.All(r => r[1] == r[0] + 1 && r[2] == r[1] + 1),
            "every batch is a block");
    }
}

// =========================================================== what may change ==

Heading("What may be changed afterwards");

await using (var db = Open())
{
    var catalogue = new Catalogue(db);

    var copy = await db.Copies
        .Where(c => c.TitleId == titleId && c.Status == CopyStatus.AVAILABLE)
        .OrderByDescending(c => c.CopyId)
        .FirstAsync();

    var wasNo = copy.AccessionNo;
    var wasDate = copy.AccessionDate;

    await catalogue.ReviseCopyAsync(copy, CopyStatus.BINDING, CopyCondition.POOR,
        "Bindery, rack 3", false, staffId);

    var after = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == copy.CopyId);

    Check("state, condition and place may be changed",
        after.Status == CopyStatus.BINDING && after.Condition == CopyCondition.POOR
        && after.Location == "Bindery, rack 3" && !after.IsCirculating,
        $"{Words.Of(after.Status)}, {Words.Of(after.Condition)}, {after.Location}");

    Check("the register's own entry is untouched",
        after.AccessionNo == wasNo && after.AccessionDate == wasDate,
        $"{after.AccessionNo}, {after.AccessionDate:dd MMM yyyy}");

    await catalogue.AnnotateAsync(after, "Spine reglued, returned from bindery.", staffId);
    await catalogue.AnnotateAsync(after, "Second note.", staffId);

    var notes = await catalogue.NotesOnAsync(after.CopyId);

    Check("notes are appended, newest first", notes.Count == 2 && notes[0].Note.Note == "Second note.",
        $"{notes.Count} notes, by {notes[0].By}");
}

await using (var db = Open())
{
    var catalogue = new Catalogue(db);

    // A book somebody is holding must not be put back on the shelf by editing
    // it — that would leave the loan open against a copy the register says is
    // available.
    var member = await db.Members.FirstAsync();
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);
    var title = await db.Titles.FirstAsync(t => t.TitleId == titleId);

    var copy = await db.Copies
        .Where(c => c.TitleId == titleId && c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    await new Counter(db, preferences).IssueAsync(member, category, copy, title, staffId, new IssueTerms());

    var issued = await db.Copies.FirstAsync(c => c.CopyId == copy.CopyId);

    var why = await catalogue.WhyNotAsync(issued, CopyStatus.AVAILABLE);

    Check("a book somebody is holding cannot be shelved by editing it",
        why is not null, why?[..46] + "…" ?? "let through");

    Check("but leaving it as issued is not obstructed",
        await catalogue.WhyNotAsync(issued, CopyStatus.ISSUED) is null, "allowed");
}

// ============================================================ reading it back ==

Heading("Reading a whole work");

await using (var db = Open())
{
    var book = await new Catalogue(db).ReadAsync(titleId);

    Check("the work reads back with its copies", book is not null && book.Copies.Count > 0,
        book is null ? "not found" : $"\"{Shorten(book.Title.Name)}\" — {book.Copies.Count} copies");

    if (book is not null)
    {
        var seqs = book.Copies
            .Where(c => c.Copy.AccessionSeq is not null)
            .Select(c => c.Copy.AccessionSeq!.Value)
            .ToList();

        Check("in the order the register is in",
            seqs.SequenceEqual(seqs.OrderBy(n => n)), "ascending by accession number");

        var out_ = book.Copies.Count(c => c.OnLoan is not null);

        Check("and says who is holding which", out_ > 0
            ? book.Copies.First(c => c.OnLoan is not null).HeldBy is not null
            : true,
            out_ == 0 ? "none out" : $"{out_} out, holder named");
    }
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The register keeps its numbers.");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "…";

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
