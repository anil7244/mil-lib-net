using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// The subject headings — the tree the catalogue files its books under.
//
// A small screen with two ways to break something quietly. A heading filed
// beneath itself makes a ring: neither end is reachable from the top, and both
// simply vanish off the screen with nothing said. And a heading deleted out
// from under its books strands them — the books survive, but nobody can find
// them by subject again and nobody is told.
//
// So this proves the guards, and then proves that the walk survives a ring that
// somehow got into the database anyway.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.SubjectProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-subject-proof.sqlite");

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
long history;
long regiments;
long signals;
long titleId;

// ------------------------------------------------------------ building it --

Heading("Building a scheme");
{
    await using var db = Open();

    staffId = await db.Users.Where(u => u.IsActive).Select(u => u.UserId).FirstAsync();

    var subjects = new Subjects(db);

    // The imported catalogue has none, which is the state this screen opens in
    // on the real install and the reason its empty state is written out.
    Check("this library starts with no headings at all",
        (await subjects.TreeAsync()).Count == 0, "0 headings");

    history = await subjects.SaveAsync(new Category { Name = "Military history" }, staffId);
    signals = await subjects.SaveAsync(new Category { Name = "Signals" }, staffId);

    regiments = await subjects.SaveAsync(
        new Category { Name = "Regimental histories", ParentId = history }, staffId);

    Check("three headings, one of them under another",
        (await subjects.TreeAsync()).Count == 3, "3 headings");

    var tree = await subjects.TreeAsync();

    // Drawn order: each heading, then everything beneath it. Without that the
    // indent on the screen would put children under the wrong parent.
    Check("the tree comes back in the order it is drawn",
        tree[0].Name == "Military history"
        && tree[1].Name == "Regimental histories"
        && tree[2].Name == "Signals",
        string.Join(" / ", tree.Select(n => n.Name)));

    Check("with the depth that makes the indent",
        tree[0].Depth == 0 && tree[1].Depth == 1 && tree[2].Depth == 0,
        string.Join(",", tree.Select(n => n.Depth)));

    Check("and knows which are top-level",
        tree[0].IsRoot && !tree[1].IsRoot, "two roots, one under");

    // Position first, name second — so a librarian can put "General" at the
    // top of a list that would otherwise sort it into the middle.
    await db.Categories.Where(c => c.CategoryId == signals)
        .ExecuteUpdateAsync(set => set.SetProperty(c => c.SortOrder, 0));
    await db.Categories.Where(c => c.CategoryId == history)
        .ExecuteUpdateAsync(set => set.SetProperty(c => c.SortOrder, 5));

    Check("position comes before name",
        (await subjects.TreeAsync())[0].Name == "Signals",
        (await subjects.TreeAsync())[0].Name);

    await db.Categories.Where(c => c.CategoryId == history)
        .ExecuteUpdateAsync(set => set.SetProperty(c => c.SortOrder, 0));
}

Heading("What it refuses");
{
    await using var db = Open();

    var subjects = new Subjects(db);

    var blank = new Category { Name = "   " };

    Check("a heading with no name is refused",
        (await subjects.ProblemsWithAsync(blank)).Count == 1,
        (await subjects.ProblemsWithAsync(blank)).FirstOrDefault() ?? "(none)");

    // Compared without regard to case: SQLite would happily hold "Signals" and
    // "signals" side by side as two headings that mean one thing.
    var same = new Category { Name = "signals" };

    Check("the same heading twice at the top level is refused",
        (await subjects.ProblemsWithAsync(same)).Any(p => p.Contains("already")),
        (await subjects.ProblemsWithAsync(same)).FirstOrDefault() ?? "(allowed)");

    // But the same word under two different parents is perfectly ordinary.
    var elsewhere = new Category { Name = "Signals", ParentId = history };

    Check("but the same word under a different heading is fine",
        (await subjects.ProblemsWithAsync(elsewhere)).Count == 0, "allowed");

    var itself = await db.Categories.AsNoTracking().FirstAsync(c => c.CategoryId == history);

    itself.ParentId = history;

    Check("a heading cannot be filed under itself",
        (await subjects.ProblemsWithAsync(itself)).Any(p => p.Contains("itself")),
        (await subjects.ProblemsWithAsync(itself)).FirstOrDefault() ?? "(allowed)");

    // The subtler half. "Military history" under "Regimental histories", which
    // is already under it, makes a ring that takes both off the screen.
    var ring = await db.Categories.AsNoTracking().FirstAsync(c => c.CategoryId == history);

    ring.ParentId = regiments;

    Check("nor under one of its own descendants",
        (await subjects.ProblemsWithAsync(ring)).Any(p => p.Contains("unreachable")),
        (await subjects.ProblemsWithAsync(ring)).FirstOrDefault() ?? "(allowed)");

    // The screen keeps that rule by not offering the move at all.
    var offered = await subjects.MayLiveUnderAsync(history);

    Check("and the move is not even on the list",
        offered.All(n => n.Id != history && n.Id != regiments),
        $"{offered.Count} places offered");

    Check("while a heading with nothing under it may go anywhere",
        (await subjects.MayLiveUnderAsync(signals)).Count == 2, "2 places");
}

Heading("Filing books under it");
{
    await using var db = Open();

    var subjects = new Subjects(db);
    var cataloguing = new Cataloguing(db);

    var book = Cataloguing.Fresh();

    book.Name = "The Regiment 1857–1947";

    titleId = await cataloguing.SaveAsync(book, null, [], [regiments], staffId);

    var tree = await subjects.TreeAsync();

    var under = tree.First(n => n.Id == regiments);
    var above = tree.First(n => n.Id == history);

    Check("the book counts against the heading it is filed under",
        under.Titles == 1, $"{under.Titles} here");

    // The point of a tree. Nothing is filed directly under "Military history",
    // but a book below it is still its business, and a heading that looked
    // empty when it is not would be deleted by somebody tidying up.
    Check("and against the broader heading above it, as 'below'",
        above.Titles == 0 && above.Below == 1,
        $"{above.Titles} here, {above.Below} below");

    var filed = await subjects.FiledUnderAsync(regiments);

    Check("the books under a heading can be listed",
        filed.Count == 1 && filed[0].TitleId == titleId
        && filed[0].Name.StartsWith("The Regiment"), filed[0].Name);
}

Heading("Removing one");
{
    await using var db = Open();

    var subjects = new Subjects(db);

    var why = await subjects.WhyNotRemovableAsync(regiments);

    Check("a heading with a book under it cannot be removed",
        why is not null && why.Contains("filed"), why ?? "(it was allowed)");

    var parent = await subjects.WhyNotRemovableAsync(history);

    Check("nor one with a heading under it",
        parent is not null && parent.Contains("under this one"), parent ?? "(it was allowed)");

    Check("but an empty one can", await subjects.WhyNotRemovableAsync(signals) is null, "removable");

    var titlesBefore = await db.Titles.CountAsync();

    await subjects.RemoveAsync(
        await db.Categories.FirstAsync(c => c.CategoryId == signals), staffId);

    Check("and goes without taking anything with it",
        await db.Titles.CountAsync() == titlesBefore
        && !await db.Categories.AnyAsync(c => c.CategoryId == signals),
        "gone, catalogue untouched");

    Check("what was done is on the activity log",
        await db.AuditLog.AnyAsync(a => a.Action == "SUBJECT_ADDED")
        && await db.AuditLog.AnyAsync(a => a.Action == "SUBJECT_REMOVED"),
        "recorded");
}

Heading("A tree that has been broken anyway");
{
    await using var db = Open();

    // Written straight to the table, past every guard — which is what an older
    // version, the other application, or somebody with a database tool could
    // leave behind. The screen must survive it and say so, not hang.
    await db.Categories.Where(c => c.CategoryId == history)
        .ExecuteUpdateAsync(set => set.SetProperty(c => c.ParentId, regiments));

    var subjects = new Subjects(db);

    // If the walk recursed, this line would never return.
    var tree = await subjects.TreeAsync();

    var total = await subjects.CountAsync();

    Check("the walk finishes rather than following the ring for ever", true,
        $"{tree.Count} of {total} reachable");

    Check("and the headings caught in it are simply not reachable",
        tree.Count < total, $"{total - tree.Count} unreachable");

    // Which is what the screen turns into the line telling somebody the
    // database needs putting right.
    Check("so the screen can tell somebody the count does not add up",
        total - tree.Count == 2, $"{total - tree.Count} missing");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The tree is a tree: nothing sits under itself, "
        + "and nothing is deleted out from under its books.");
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
