using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Cataloguing — writing the bibliographic record.
//
// The rule this exists to hold down is the one the whole data model rests on:
// a title is a description and nothing else. Cataloguing a book creates no
// copy, no accession number and no entry in the register. If that ever stops
// being true, the library has a quantity on a title again and the two-level
// model is gone.
//
// After that, the authority lists: authors and publishers are found by name or
// created, so that two people cataloguing the same publisher on the same
// afternoon end up with one publisher rather than two.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.CatalogueProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-catalogue-proof.sqlite");

Sweep();
File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-58}  {saw}");

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

long staffId;
long titleId;
long subjectA;
long subjectB;

// ------------------------------------------------------- a work, described --

Heading("Cataloguing a work");
{
    await using var db = Open();

    staffId = await db.Users.Where(u => u.IsActive).Select(u => u.UserId).FirstAsync();

    // This library was imported from a stock ledger, which carried titles and
    // copies and nothing else — no authors, no publishers, no subject
    // headings. So the proof makes its own two headings rather than assuming
    // the catalogue has any.
    Check("the imported catalogue has no subject headings of its own",
        await db.Categories.CountAsync() == 0, "0 headings — this screen is how they get made");

    foreach (var name in new[] { "Proof — Signals", "Proof — Tactics" })
    {
        db.Categories.Add(new Category { Name = name, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
    }

    await db.SaveAndForgetAsync();

    var subjects = await new Cataloguing(db).SubjectsAsync();

    subjectA = subjects[0].CategoryId;
    subjectB = subjects[1].CategoryId;

    var titlesBefore = await db.Titles.CountAsync();
    var copiesBefore = await db.Copies.CountAsync();
    var counterBefore = await db.AccessionCounters
        .Where(c => c.Scope == "default").Select(c => c.NextSeq).FirstAsync();

    var cataloguing = new Cataloguing(db);

    var fresh = Cataloguing.Fresh();

    Check("a new record starts on sensible defaults",
        fresh.Language == "English"
        && fresh.MaterialType == MaterialType.BOOK
        && fresh.SecurityClass == SecurityClass.UNCLASSIFIED
        && fresh.ClassificationSch == ClassificationScheme.DDC,
        "English, book, unclassified, DDC");

    fresh.Name = "Regimental Signalling in the Field";
    fresh.Subtitle = "A précis for junior leaders";
    fresh.Edition = "3rd ed.";
    fresh.PubPlace = "New Delhi";
    fresh.PubYear = 1987;
    fresh.Pages = "xii, 214 p. : ill.";
    fresh.ClassificationNo = "623.731";
    fresh.CallNumber = "623.731 SIN";
    fresh.MaterialType = MaterialType.PRECIS;

    titleId = await cataloguing.SaveAsync(
        fresh,
        "  Army Publishing Directorate ",
        [
            new AuthorEntry("Singh, R K", "Lt Col", AuthorRole.AUTHOR),
            new AuthorEntry("Verma, A", null, AuthorRole.EDITOR),
        ],
        [subjectA, subjectB],
        staffId);

    Check("it is saved and has an id", titleId > 0, $"title {titleId}");

    Check("the catalogue is one work bigger",
        await db.Titles.CountAsync() == titlesBefore + 1,
        $"{await db.Titles.CountAsync():N0} works");

    // The rule the two-level model rests on. A title is a description; it is
    // not a book on a shelf and it must not have quietly become one.
    Check("and no copy was created with it",
        await db.Copies.CountAsync(c => c.TitleId == titleId) == 0
        && await db.Copies.CountAsync() == copiesBefore,
        "0 copies against it");

    Check("nor was an accession number spent",
        await db.AccessionCounters.Where(c => c.Scope == "default")
            .Select(c => c.NextSeq).FirstAsync() == counterBefore,
        $"counter still {counterBefore}");
}

Heading("The authority lists");
{
    await using var db = Open();

    var publishers = await db.Publishers
        .Where(p => p.Name.Contains("Army Publishing"))
        .ToListAsync();

    Check("the publisher was created, trimmed of its spaces",
        publishers.Count == 1 && publishers[0].Name == "Army Publishing Directorate",
        $"\"{publishers[0].Name}\"");

    var cataloguing = new Cataloguing(db);

    var authors = await cataloguing.AuthorsOnAsync(titleId);

    Check("both people are on it, in the order they were given",
        authors.Count == 2 && authors[0].Name == "Singh, R K" && authors[1].Name == "Verma, A",
        string.Join("; ", authors.Select(a => a.Name)));

    Check("with their ranks and their roles",
        authors[0].Rank == "Lt Col" && authors[0].Role == AuthorRole.AUTHOR
        && authors[1].Role == AuthorRole.EDITOR,
        $"{authors[0].Rank} {authors[0].Name}, {authors[1].Role}");

    Check("and both subjects are filed against it",
        (await cataloguing.SubjectsOnAsync(titleId)).Count == 2, "2 headings");

    // A second work by the same person, and from the same publisher typed a
    // little differently. Two people cataloguing on the same afternoon must
    // not leave the catalogue with two of each.
    var second = Cataloguing.Fresh();

    second.Name = "Field Telephony";

    var secondId = await cataloguing.SaveAsync(
        second,
        "ARMY PUBLISHING DIRECTORATE",
        [new AuthorEntry("singh, r k", null, AuthorRole.AUTHOR)],
        [],
        staffId);

    Check("the same publisher typed in capitals is still one publisher",
        await db.Publishers.CountAsync(p => p.Name.Contains("Army Publishing")) == 1,
        "1 publisher");

    Check("and the same author typed in lower case is still one author",
        await db.Authors.CountAsync(a => a.Name == "Singh, R K") == 1,
        "1 author");

    var reused = await cataloguing.AuthorsOnAsync(secondId);

    Check("the second book points at that same person",
        reused.Count == 1 && reused[0].Name == "Singh, R K", reused[0].Name);

    // The rank was not given the second time; the one already recorded stands.
    Check("and does not lose the rank recorded the first time",
        reused[0].Rank == "Lt Col", reused[0].Rank ?? "(none)");

    await db.Titles.Where(t => t.TitleId == secondId).ExecuteDeleteAsync();
    await db.TitleAuthors.Where(ta => ta.TitleId == secondId).ExecuteDeleteAsync();
}

Heading("Correcting a record");
{
    await using var db = Open();

    var cataloguing = new Cataloguing(db);

    var title = await db.Titles.FirstAsync(t => t.TitleId == titleId);

    title.Name = "Regimental Signalling in the Field";
    title.PubYear = 1988;

    // One person now, where there were two. The pivot is replaced rather than
    // added to — otherwise an editor removed on the form stays on the book.
    await cataloguing.SaveAsync(
        title, "Army Publishing Directorate",
        [new AuthorEntry("Singh, R K", "Lt Col", AuthorRole.AUTHOR)],
        [subjectA],
        staffId);

    var authors = await cataloguing.AuthorsOnAsync(titleId);

    Check("removing somebody on the form removes them from the book",
        authors.Count == 1 && authors[0].Name == "Singh, R K",
        $"{authors.Count} on it");

    Check("and the same for a subject heading",
        (await cataloguing.SubjectsOnAsync(titleId)).Count == 1, "1 heading");

    Check("the correction itself took",
        (await db.Titles.AsNoTracking().FirstAsync(t => t.TitleId == titleId)).PubYear == 1988,
        "1988");

    Check("editing an existing work still creates no copy",
        await db.Copies.CountAsync(c => c.TitleId == titleId) == 0, "0 copies");

    // The same person twice on one form is the same primary key twice, and the
    // save would fail on something that looks perfectly reasonable to type.
    await cataloguing.SaveAsync(
        title, "Army Publishing Directorate",
        [
            new AuthorEntry("Singh, R K", null, AuthorRole.AUTHOR),
            new AuthorEntry("Singh, R K", null, AuthorRole.AUTHOR),
            new AuthorEntry("   ", null, AuthorRole.AUTHOR),
        ],
        [subjectA],
        staffId);

    Check("the same person entered twice does not break the save",
        (await cataloguing.AuthorsOnAsync(titleId)).Count == 1, "kept once");

    // The same person in two capacities is two rows and is perfectly ordinary
    // — somebody who wrote one part and edited the rest.
    await cataloguing.SaveAsync(
        title, "Army Publishing Directorate",
        [
            new AuthorEntry("Singh, R K", null, AuthorRole.AUTHOR),
            new AuthorEntry("Singh, R K", null, AuthorRole.EDITOR),
        ],
        [subjectA],
        staffId);

    Check("but the same person in two capacities is two entries",
        (await cataloguing.AuthorsOnAsync(titleId)).Count == 2, "author and editor");
}

Heading("What the form refuses");
{
    await using var db = Open();

    var cataloguing = new Cataloguing(db);

    var blank = Cataloguing.Fresh();

    var problems = await cataloguing.ProblemsWithAsync(blank, null);

    Check("a book with no title is refused",
        problems.Any(p => p.Contains("title")), problems.FirstOrDefault() ?? "(none)");

    var future = Cataloguing.Fresh();

    future.Name = "Something";

    Check("a year in the far future is refused",
        (await cataloguing.ProblemsWithAsync(future, DateTime.Now.Year + 40)).Count == 1,
        $"{DateTime.Now.Year + 40}");

    Check("but next year is not — a book can be dated ahead",
        (await cataloguing.ProblemsWithAsync(future, DateTime.Now.Year + 1)).Count == 0,
        $"{DateTime.Now.Year + 1}");

    Check("and a book with no year at all is fine",
        (await cataloguing.ProblemsWithAsync(future, null)).Count == 0, "no year given");

    // A real catalogue is messy. A form that refuses a missing ISBN, an odd
    // pagination or an inconsistent classification number refuses the library.
    var messy = Cataloguing.Fresh();

    messy.Name = "Précis No 44";
    messy.ClassificationNo = "MISC/44 (rev)";
    messy.Pages = "unpaged";

    Check("a messy but real record is accepted",
        (await cataloguing.ProblemsWithAsync(messy, null)).Count == 0, "accepted");

    // Warned about, not refused: two editions under one ISBN happens, and the
    // point is that somebody about to catalogue the same book twice notices.
    var duplicate = Cataloguing.Fresh();

    duplicate.Name = "Another book";
    duplicate.Isbn = "978-81-000-0001-7";

    await cataloguing.SaveAsync(duplicate, null, [], [], staffId);

    var again = Cataloguing.Fresh();

    again.Name = "The same book again";
    again.Isbn = "978-81-000-0001-7";

    Check("an ISBN already in the catalogue is pointed out",
        (await cataloguing.ProblemsWithAsync(again, null)).Any(p => p.Contains("Another book")),
        "flagged");

    Check("and the record it clashes with is named",
        (await cataloguing.ProblemsWithAsync(again, null)).Count == 1, "one problem");

    await db.Titles.Where(t => t.TitleId == duplicate.TitleId).ExecuteDeleteAsync();
}

Heading("Removing a work");
{
    await using var db = Open();

    var cataloguing = new Cataloguing(db);

    Check("a work nothing was accessioned against can be removed",
        await cataloguing.WhyNotRemovableAsync(titleId) is null, "removable");

    // Give it a copy, and it becomes part of the register.
    var preferences = await Preferences.ReadAsync(db);

    await new Accession(db, preferences).AccessionAsync(titleId, 1, new Copy
    {
        AccessionDate = DateOnly.FromDateTime(DateTime.Today),
        Source = CopySource.PURCHASE,
        Condition = CopyCondition.NEW,
        Status = CopyStatus.AVAILABLE,
        IsCirculating = true,
    }, staffId);

    var why = await cataloguing.WhyNotRemovableAsync(titleId);

    // The accession number is the register's, it is never reissued, and a
    // register that refers to a title nobody can look up has a hole in it.
    Check("once a copy is accessioned it cannot be", why is not null, why ?? "(it was allowed)");

    Check("and the refusal says where to go instead",
        why!.Contains("Withdrawals"), "points at condemnation");

    var throwaway = Cataloguing.Fresh();

    throwaway.Name = "Cataloguing error";

    var id = await cataloguing.SaveAsync(
        throwaway, "Nowhere Press",
        [new AuthorEntry("Nobody", null, AuthorRole.AUTHOR)], [subjectA], staffId);

    await cataloguing.RemoveAsync(
        await db.Titles.FirstAsync(t => t.TitleId == id), staffId);

    Check("a work catalogued in error goes cleanly",
        !await db.Titles.AnyAsync(t => t.TitleId == id), "gone");

    Check("and takes its authors and subjects with it",
        !await db.TitleAuthors.AnyAsync(ta => ta.TitleId == id)
        && !await db.TitleCategories.AnyAsync(tc => tc.TitleId == id),
        "no orphaned rows");

    // The author record itself stays: it is the catalogue's authority list,
    // not a thing that belongs to one book.
    Check("but the person stays on the authority list",
        await db.Authors.AnyAsync(a => a.Name == "Nobody"), "author kept");
}

Heading("Reading it back");
{
    await using var db = Open();

    var record = await new Catalogue(db).ReadAsync(titleId);

    Check("the work reads back through the catalogue", record is not null, record?.Title.Name ?? "(nothing)");

    Check("with its publisher",
        record!.Publisher?.Name == "Army Publishing Directorate",
        record.Publisher?.Name ?? "(none)");

    Check("its people", record.Authors.Count == 2, $"{record.Authors.Count}");

    Check("its subject", record.Subjects.Count == 1, $"{record.Subjects.Count}");

    Check("and the one copy that was accessioned against it",
        record.Copies.Count == 1 && record.Copies[0].Copy.AccessionSeq > 0,
        record.Copies[0].Copy.AccessionNo);

    Check("the subtitle survived the round trip",
        record.Title.FullTitle.Contains("précis"), record.Title.FullTitle);

    var notes = await db.AuditLog
        .Where(a => a.EntityId == titleId && (a.Action == "TITLE_CREATE" || a.Action == "TITLE_UPDATE"))
        .ToListAsync();

    Check("cataloguing it and correcting it are both on the activity log",
        notes.Any(n => n.Action == "TITLE_CREATE") && notes.Any(n => n.Action == "TITLE_UPDATE"),
        $"{notes.Count} entries");

    Check("and the removal is too",
        await db.AuditLog.AnyAsync(a => a.Action == "TITLE_REMOVED"), "recorded");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("A title is a description. Cataloguing one puts nothing on the register, "
        + "and the authority lists stay single.");
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
