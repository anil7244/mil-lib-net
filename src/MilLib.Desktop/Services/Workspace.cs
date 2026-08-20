using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

namespace MilLib.Desktop.Services;

/// <summary>
/// Where the records are, and how the rest of the application gets at them.
///
/// By default: the file next to the executable. A folder handed to a client
/// carries its own records and must never quietly open somebody else's, so
/// that file always wins unless somebody has deliberately said otherwise on
/// the Database screen — which is when this points at a server instead and
/// several people share one set of books.
/// </summary>
public static class Workspace
{
    private static string? _resolvedFile;
    private static DatabaseSource? _source;
    private static bool _prepared;

    /// <summary>What this copy is connected to.</summary>
    public static DatabaseSource Source =>
        _source ??= Installation.Current.ToSource(LocateFile());

    /// <summary>
    /// The file, when this copy reads one. Kept as its own name because a great
    /// deal of the application — backups, the logo, the line at the foot of the
    /// menu — is about the file specifically.
    /// </summary>
    public static string DatabasePath => Source.IsFile ? Source.FilePath : "";

    public static bool UsesFile => Source.IsFile;

    /// <summary>
    /// Whether the records can be reached at all. For a file that is a question
    /// about the disk; for a server it is answered by trying.
    /// </summary>
    public static bool DatabaseExists => !Source.IsFile || File.Exists(Source.FilePath);

    /// <summary>Where the path came from, so the screen can say so.</summary>
    public static string Origin { get; private set; } = "";

    /// <summary>
    /// Forgets the current connection, so the next Open() picks up whatever was
    /// just saved. Called when the Database screen changes something.
    /// </summary>
    public static void Forget()
    {
        _source = null;
        _resolvedFile = null;
        _prepared = false;
    }

    /// <summary>
    /// The folder pictures live in — the unit crest, and a cover for every book
    /// that has one.
    ///
    /// Beside the data file rather than inside the executable, so the crest can
    /// be changed without a new build and so a folder handed to a unit carries
    /// its own.
    /// </summary>
    public static string Pictures => Source.IsFile
        ? Path.GetDirectoryName(Source.FilePath) ?? AppContext.BaseDirectory
        : Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// The unit crest, or null if this copy has none. A library without one
    /// simply shows its name, which is what an uncrested library looks like.
    /// </summary>
    public static string? CrestPath(string? recorded)
    {
        // A file simply called crest.png wins, because that is the one somebody
        // handed this folder can replace without being told where to look.
        var plain = Path.Combine(Pictures, "crest.png");

        return File.Exists(plain) ? plain : Find(recorded);
    }

    /// <summary>The cover of one book, if it has one on this machine.</summary>
    public static string? CoverPath(string? recorded) => Find(recorded);

    /// <summary>
    /// A member's photograph, if this machine has it. Resolved exactly as a
    /// cover is — the web application files both under the same public storage
    /// folder — so a database pointed at that install shows the faces on the
    /// passes rather than a column of blanks.
    /// </summary>
    public static string? PhotoPath(string? recorded) => Find(recorded);

    /// <summary>
    /// Where a path recorded in the database might actually be.
    ///
    /// The PHP application stores these relative to its own public storage
    /// folder. When this copy is pointed at that installation's database, its
    /// pictures are worth finding too rather than showing every book with no
    /// cover.
    /// </summary>
    private static string? Find(string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded))
        {
            return null;
        }

        var tidy = recorded
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        string[] candidates =
        [
            Path.Combine(Pictures, tidy),
            Path.Combine(Pictures, "..", "storage", "app", "public", tidy),
            Path.Combine(Pictures, "..", "public", "storage", tidy),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public static MilLibDbContext Open()
    {
        var db = new MilLibDbContext(Source);

        if (!_prepared && Source.IsFile && File.Exists(Source.FilePath))
        {
            // Two applications may have this file open at once during the
            // changeover. Write-ahead logging lets one read while the other
            // writes, and the timeout makes a brief overlap wait rather than
            // fail — without them, saving while the other application is busy
            // gives "database is locked" and loses the edit.
            try
            {
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            }
            catch
            {
                // A read-only file or a locked moment is not a reason to
                // refuse to start; it only means these settings did not take.
            }

            _prepared = true;
        }

        return db;
    }

    /// <summary>
    /// Opens something other than the configured connection — used to try a
    /// server before committing to it.
    /// </summary>
    public static MilLibDbContext OpenOther(DatabaseSource source) => new(source);

    private static string LocateFile()
    {
        if (_resolvedFile is not null)
        {
            return _resolvedFile;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "database.sqlite"),
            Path.Combine(AppContext.BaseDirectory, "database.sqlite"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                Origin = "the copy in this folder";

                return _resolvedFile = candidate;
            }
        }

        Origin = "not found";

        return _resolvedFile = candidates[0];
    }
}
