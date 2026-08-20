using Microsoft.EntityFrameworkCore;

namespace MilLib.Desktop.Services;

/// <summary>
/// Keeping copies of the records.
///
/// What this can honestly do depends on where the records are:
///
/// A SQLite file is copied whole, after the write-ahead log has been folded
/// back into it. The copy is the database — byte for byte — so restoring is
/// putting the file back, and nothing about the copy can be subtly wrong.
///
/// A server database is not this application's to back up. Copying rows out
/// through the model would produce a file containing everything the model
/// knows about and silently missing anything it does not, which is the worst
/// kind of backup: one that looks fine until the day it is needed. Where the
/// records live on a server, that server's own backup is the answer, and this
/// screen says so rather than pretending otherwise.
/// </summary>
public static class Backups
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "data", "backups");

    /// <summary>Whether the current connection is one this can copy.</summary>
    public static bool Possible => Workspace.UsesFile;

    public static IReadOnlyList<BackupFile> List()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            return new DirectoryInfo(Folder)
                .GetFiles("database-*.sqlite")
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new BackupFile
                {
                    Name = f.Name,
                    Path = f.FullName,
                    Taken = f.LastWriteTime,
                    Size = f.Length,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Faults.Record("listing backups", ex);

            return [];
        }
    }

    /// <summary>
    /// Takes a copy now. Returns the path written, or the reason it could not
    /// be — never a silent failure, because a backup that quietly did not
    /// happen is worse than no backup at all.
    /// </summary>
    public static async Task<(string? Path, string? Problem)> TakeAsync(bool byHand = false)
    {
        if (!Possible)
        {
            return (null, "This copy is connected to a server. Its backups are the server's own — this application does not take them.");
        }

        var file = Workspace.DatabasePath;

        if (!File.Exists(file))
        {
            return (null, "There is no data file to copy.");
        }

        try
        {
            Directory.CreateDirectory(Folder);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var target = Path.Combine(Folder, $"database-{stamp}{(byHand ? "-by-hand" : "")}.sqlite");

            await using var db = Workspace.Open();

            // SQLite writes the copy itself, rather than this copying the file
            // after a checkpoint.
            //
            // Checkpoint-then-copy is nearly safe, and nearly is not good
            // enough for the one thing standing between a unit and losing its
            // library: a write landing between the two steps leaves a copy of a
            // file caught mid-write. VACUUM INTO produces one complete,
            // consistent database in a single operation, and is safe while the
            // library is in use — which is exactly when a backup gets taken.
            //
            // The path goes in as a parameter rather than as text in the
            // statement: a folder with an apostrophe in its name would
            // otherwise end the string early.
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", target);

            var settings = Installation.Current;
            settings.LastBackupAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            settings.Save();

            Prune(settings.BackupsToKeep);

            return (target, null);
        }
        catch (Exception ex)
        {
            Faults.Record("taking a backup", ex);

            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Put a backup back over the live file.
    ///
    /// Today's records are copied aside first, always, without being asked.
    /// Restoring is somebody saying they want last Tuesday's library back; it
    /// is not somebody saying they want today's destroyed, and the difference
    /// between those two only becomes clear about ten seconds afterwards.
    ///
    /// Returns where today's was put, or the reason nothing happened.
    /// </summary>
    public static async Task<(string? Aside, string? Problem)> RestoreAsync(BackupFile backup)
    {
        if (!Possible)
        {
            return (null, "This copy is connected to a server. There is no file here to put back.");
        }

        if (!File.Exists(backup.Path))
        {
            return (null, $"{backup.Name} is no longer on the disk.");
        }

        var live = Workspace.DatabasePath;

        try
        {
            Directory.CreateDirectory(Folder);

            var aside = Path.Combine(Folder,
                $"database-{DateTime.Now:yyyy-MM-dd-HHmmss}-before-restore.sqlite");

            await using (var db = Workspace.Open())
            {
                await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", aside);
            }

            // Everything has to let go of the file before it can be replaced,
            // and the pooled connections hold it open long after the context
            // that opened them has gone.
            Workspace.Forget();

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // The write-ahead log and the shared-memory file belong to the
            // database being replaced. Left behind, SQLite replays the old log
            // over the new file and the restore silently does nothing at all —
            // which is the worst outcome available, because it looks like it
            // worked.
            foreach (var leftover in new[] { live + "-wal", live + "-shm" })
            {
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }

            File.Copy(backup.Path, live, overwrite: true);

            Workspace.Forget();

            return (aside, null);
        }
        catch (Exception ex)
        {
            Faults.Record("putting a backup back", ex);

            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Takes one only if the last is older than the chosen interval. Called at
    /// startup, so an application opened once a week still leaves a trail.
    /// </summary>
    public static async Task TakeIfDueAsync()
    {
        var settings = Installation.Current;

        if (!settings.BackupsOn || !Possible)
        {
            return;
        }

        if (DateTime.TryParse(settings.LastBackupAt, out var last)
            && DateTime.Now - last < TimeSpan.FromHours(Math.Max(1, settings.BackupEveryHours)))
        {
            return;
        }

        await TakeAsync();
    }

    /// <summary>
    /// Keeps the newest few and removes the rest, so a folder of backups does
    /// not quietly become the largest thing on the disk.
    /// </summary>
    private static void Prune(int keep)
    {
        if (keep <= 0)
        {
            return;
        }

        // The ones taken by hand, and the one kept just before a restore, stay
        // whatever the rule says. Somebody made those deliberately, at a moment
        // they were unsure about, and it is not for a tidying rule to decide
        // they are finished with them.
        foreach (var old in List().Where(b => !b.Deliberate).Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex)
            {
                Faults.Record("removing an old backup", ex);
            }
        }
    }
}

public class BackupFile
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public DateTime Taken { get; init; }
    public long Size { get; init; }

    public string When => Taken.ToString("dd MMM yyyy 'at' HH:mm");

    public string Weight => Size < 1024 * 1024
        ? $"{Size / 1024.0:N0} KB"
        : $"{Size / 1024.0 / 1024.0:N1} MB";

    /// <summary>Taken by somebody, rather than by the clock.</summary>
    public bool Deliberate =>
        Name.Contains("-by-hand", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("-before-restore", StringComparison.OrdinalIgnoreCase);

    public string Why =>
        Name.Contains("-before-restore", StringComparison.OrdinalIgnoreCase)
            ? "kept before a restore"
            : Name.Contains("-by-hand", StringComparison.OrdinalIgnoreCase)
                ? "taken by hand"
                : "automatic";
}
