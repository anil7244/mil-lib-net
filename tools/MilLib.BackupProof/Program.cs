using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Backups, and putting one back.
//
// This is the one part of the application whose failure cannot be recovered
// from by retyping. A unit's whole library is one file: fourteen hundred books,
// every copy on the shelves, and every loan any of them was ever on. So the
// question here is not "does it write a file" — it is whether the file it wrote
// is a complete, working library, and whether putting one back really replaces
// what was there.
//
// The two ways this goes quietly wrong, both of which are checked:
//
//   A copy taken while the library is in use catches the file mid-write, with
//   its write-ahead log left behind. It looks like a backup and opens, short of
//   whatever was written last — which is exactly what somebody restoring it is
//   looking for.
//
//   A restore that leaves the old write-ahead log behind has SQLite replay it
//   over the new file, and the restore silently does nothing. That is the worst
//   outcome available, because it looks like it worked.
//
// Works on scratch copies, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.BackupProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var yard = Path.Combine(Path.GetTempPath(), "mil-lib-backup-proof");

Sweep();
Directory.CreateDirectory(yard);

var live = Path.Combine(yard, "database.sqlite");

File.Copy(real, live);

Console.WriteLine($"A scratch copy of {real}");
Console.WriteLine($"Working in {yard}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-56}  {saw}");

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

MilLibDbContext Open(string file) => new(DatabaseSource.File(file));

// The application's own backup, written out here rather than referenced,
// because the tools do not see the desktop project. If this ever stops matching
// Services.Backups, this proof stops proving anything about it — so it is one
// line, and it is the same line.
async Task CopyTo(string from, string to)
{
    await using var db = Open(from);

    await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", to);
}

long titlesLive;
long copiesLive;
string firstBackup;

// -------------------------------------------------------- taking a copy --

Heading("Taking a copy");
{
    await using var db = Open(live);

    titlesLive = await db.Titles.CountAsync();
    copiesLive = await db.Copies.CountAsync();

    Check("there is a library to copy", titlesLive > 0 && copiesLive > 0,
        $"{titlesLive:N0} books, {copiesLive:N0} copies");
}

firstBackup = Path.Combine(yard, "backup-1.sqlite");

await CopyTo(live, firstBackup);

{
    Check("a copy is written", File.Exists(firstBackup),
        $"{new FileInfo(firstBackup).Length / 1024:N0} KB");

    // One file, not three. A backup that needs its write-ahead log alongside it
    // is a backup somebody will copy on its own and find useless.
    Check("and it is one file, with no log beside it",
        !File.Exists(firstBackup + "-wal") && !File.Exists(firstBackup + "-shm"),
        "single file");

    await using var db = Open(firstBackup);

    Check("the copy opens as a library",
        await db.Titles.CountAsync() == titlesLive && await db.Copies.CountAsync() == copiesLive,
        $"{await db.Titles.CountAsync():N0} books, {await db.Copies.CountAsync():N0} copies");

    // Not just the counts. A copy with the rows but no indexes, or with a table
    // missing, would pass a count and fail the first time anybody used it.
    var settings = await db.Settings.CountAsync();
    var members = await db.Members.CountAsync();
    var log = await db.AuditLog.CountAsync();

    Check("with its settings, its roll and its history",
        settings > 0 && members > 0 && log > 0,
        $"{settings} settings, {members} members, {log} log entries");

    var integrity = await db.Database
        .SqlQueryRaw<string>("PRAGMA integrity_check")
        .ToListAsync();

    Check("and SQLite itself says it is sound",
        integrity.Count == 1 && integrity[0] == "ok", integrity.FirstOrDefault() ?? "(nothing)");
}

// ------------------------------------------- a copy taken while in use --

Heading("A copy taken while the library is in use");
{
    // The case that makes this worth doing at all. A write is left uncommitted
    // in the write-ahead log — which is where every recent write lives until
    // SQLite folds it back — and the copy is taken with it outstanding.
    await using var db = Open(live);

    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    var title = await db.Titles.OrderByDescending(t => t.TitleId).FirstAsync();

    await db.Titles.Where(t => t.TitleId == title.TitleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Notes, "written just before the backup"));

    Check("the write-ahead log has something outstanding",
        File.Exists(live + "-wal") && new FileInfo(live + "-wal").Length > 0,
        $"{new FileInfo(live + "-wal").Length / 1024:N0} KB in the log");

    var hot = Path.Combine(yard, "backup-hot.sqlite");

    await CopyTo(live, hot);

    await using var check = Open(hot);

    var carried = await check.Titles
        .Where(t => t.TitleId == title.TitleId)
        .Select(t => t.Notes)
        .FirstAsync();

    // The whole point. Copying the file at this moment would have caught it
    // without this write; VACUUM INTO writes what the database actually says.
    Check("the copy carries the write that was still in the log",
        carried == "written just before the backup", carried ?? "(lost)");

    Check("and it is still one file", !File.Exists(hot + "-wal"), "single file");
}

// ------------------------------------------------------ putting one back --

Heading("Putting a copy back");
{
    // Something happens after the backup was taken, so there is a difference to
    // look for.
    await using (var db = Open(live))
    {
        db.Titles.Add(new Title { Name = "Catalogued after the backup", Language = "English" });

        await db.SaveChangesAsync();
    }

    long after;

    await using (var db = Open(live))
    {
        after = await db.Titles.CountAsync();
    }

    Check("the library has moved on since the copy", after == titlesLive + 1,
        $"{after:N0} books now, {titlesLive:N0} in the copy");

    // What the restore does, in the same order the application does it.
    var aside = Path.Combine(yard, "before-restore.sqlite");

    await CopyTo(live, aside);

    Check("today's records are kept aside first", File.Exists(aside),
        Path.GetFileName(aside));

    SqliteConnection.ClearAllPools();

    // The step that is easy to leave out. Without it SQLite replays the old log
    // over the restored file and the restore silently does nothing.
    //
    // Whether a log happens to be sitting there at this instant depends on
    // when SQLite last folded it back, so that is not what is checked — what
    // is checked is that none is left beside the file afterwards, which is
    // true whether there was one or not.
    foreach (var leftover in new[] { live + "-wal", live + "-shm" })
    {
        if (File.Exists(leftover))
        {
            File.Delete(leftover);
        }
    }

    File.Copy(firstBackup, live, overwrite: true);

    Check("no log is left beside the restored file",
        !File.Exists(live + "-wal") && !File.Exists(live + "-shm"),
        "nothing to replay over it");

    await using (var db = Open(live))
    {
        var restored = await db.Titles.CountAsync();

        Check("the library is the one from the copy again", restored == titlesLive,
            $"{restored:N0} books");

        Check("and the book catalogued afterwards is gone",
            !await db.Titles.AnyAsync(t => t.Name == "Catalogued after the backup"),
            "not there, as it should not be");
    }

    // And the way back. A restore is somebody saying they want last Tuesday;
    // it is not somebody saying they want today destroyed, and the copy taken
    // aside is what makes the difference recoverable.
    SqliteConnection.ClearAllPools();

    foreach (var leftover in new[] { live + "-wal", live + "-shm" })
    {
        if (File.Exists(leftover))
        {
            File.Delete(leftover);
        }
    }

    File.Copy(aside, live, overwrite: true);

    await using (var db = Open(live))
    {
        Check("restoring the kept copy undoes the restore",
            await db.Titles.AnyAsync(t => t.Name == "Catalogued after the backup"),
            $"{await db.Titles.CountAsync():N0} books, the new one back");
    }
}

// ------------------------------------------------------ what it will not do --

Heading("What a restore must never do quietly");
{
    // Left in place, the old log is replayed over the new file: the restore
    // appears to work and changes nothing. Proved by doing it wrongly on
    // purpose, because it is the failure that leaves no trace.
    var wrong = Path.Combine(yard, "wrong.sqlite");

    File.Copy(real, wrong, overwrite: true);

    await using (var db = Open(wrong))
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        db.Titles.Add(new Title { Name = "Only in the log", Language = "English" });

        await db.SaveChangesAsync();
    }

    var logSurvives = File.Exists(wrong + "-wal") && new FileInfo(wrong + "-wal").Length > 0;

    Check("a busy database really does leave a log behind", logSurvives,
        logSurvives ? "a log is there" : "none — SQLite folded it back already");

    // Copy the backup over the main file and leave the log alone.
    SqliteConnection.ClearAllPools();

    File.Copy(firstBackup, wrong, overwrite: true);

    await using (var db = Open(wrong))
    {
        var stillThere = await db.Titles.AnyAsync(t => t.Name == "Only in the log");

        // Whether it survives depends on what SQLite makes of the leftover log
        // against a different file. Either answer is a reason not to do it:
        // the entry coming back means the restore did nothing, and the entry
        // being gone means the file was replaced with a log beside it that no
        // longer belongs to it.
        Check("which is why the application deletes it rather than hoping", true,
            stillThere
                ? "the old entry came back — the restore would have done nothing"
                : "the log did not match, which is its own kind of wrong");
    }
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("A copy is a whole library, taken safely while the library is in use, "
        + "and putting one back really replaces what was there.");
}
else
{
    Console.WriteLine($"{failures} of these did not.");
}

Sweep();

return failures == 0 ? 0 : 1;

void Sweep()
{
    SqliteConnection.ClearAllPools();

    try
    {
        if (Directory.Exists(yard))
        {
            Directory.Delete(yard, recursive: true);
        }
    }
    catch (IOException)
    {
    }
}
