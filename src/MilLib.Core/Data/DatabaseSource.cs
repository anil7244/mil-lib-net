using System.Text;

namespace MilLib.Core.Data;

public enum DatabaseKind
{
    Sqlite,
    MySql,
    Postgres,
}

/// <summary>
/// Where the records are.
///
/// One installation, one answer — but not always the same answer. A copy on a
/// laptop in a field office reads a file beside itself; the same application in
/// the office reads the server everyone else is on. The schema is identical
/// either way, because it is the same schema the older PHP application built.
///
/// This description of the connection cannot itself live in the database: the
/// application has to know where to look before it can look anywhere. It is
/// held in a small file beside the executable instead.
/// </summary>
public class DatabaseSource
{
    public DatabaseKind Kind { get; set; } = DatabaseKind.Sqlite;

    /// <summary>The file, when this is a SQLite connection.</summary>
    public string FilePath { get; set; } = "";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public static DatabaseSource File(string path) =>
        new() { Kind = DatabaseKind.Sqlite, FilePath = path };

    public bool IsFile => Kind == DatabaseKind.Sqlite;

    /// <summary>The port to use when none was given.</summary>
    public int EffectivePort => Port > 0
        ? Port
        : Kind switch
        {
            DatabaseKind.MySql => 3306,
            DatabaseKind.Postgres => 5432,
            _ => 0,
        };

    public string Connection => Kind switch
    {
        // ReadWrite, not the default ReadWriteCreate.
        //
        // Given a path that does not exist, SQLite will happily create an empty
        // database and the application then opens looking like a business with
        // no clients, no licences and no money — which is far more alarming, and
        // far more confusing, than being told the file is missing.
        DatabaseKind.Sqlite => $"Data Source={FilePath};Mode=ReadWrite",

        DatabaseKind.MySql =>
            $"Server={Host};Port={EffectivePort};Database={Database};User ID={Username};Password={Password};" +
            // The older application wrote these columns as local times with no
            // zone attached. Asking the driver to convert them would shift every
            // date on the screen by the offset of whoever is looking.
            "AllowZeroDateTime=true;ConvertZeroDateTime=true;",

        DatabaseKind.Postgres =>
            $"Host={Host};Port={EffectivePort};Database={Database};Username={Username};Password={Password};" +
            // A unit's own server on its own LAN, usually a machine in the same
            // room. Demanding a certificate it has no way to get would stop the
            // library working over a wire nobody outside the building touches.
            "SSL Mode=Prefer;Trust Server Certificate=true;",

        _ => throw new InvalidOperationException("Unknown kind of database."),
    };

    /// <summary>
    /// What to put on screen. Never the password — not even as dots, because a
    /// row of dots of the right length is itself a fact about it.
    /// </summary>
    public string Describe() => Kind switch
    {
        DatabaseKind.Sqlite => FilePath,
        _ => $"{Username}@{Host}:{EffectivePort}/{Database}",
    };

    public string KindName => Kind switch
    {
        DatabaseKind.MySql => "MySQL or MariaDB",
        DatabaseKind.Postgres => "PostgreSQL",
        _ => "SQLite file",
    };

    /// <summary>
    /// What is wrong with this description, in words, before anything is tried.
    /// A connection that cannot possibly work should say so without a timeout.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Kind == DatabaseKind.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                problems.Add("Choose the database file.");
            }
            else if (!System.IO.File.Exists(FilePath))
            {
                problems.Add($"There is no file at {FilePath}.");
            }

            return problems;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("The server needs an address — a name or an IP.");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            problems.Add("Name the database on that server.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add("A username is needed to sign in to the server.");
        }

        return problems;
    }

    public DatabaseSource Copy() => new()
    {
        Kind = Kind,
        FilePath = FilePath,
        Host = Host,
        Port = Port,
        Database = Database,
        Username = Username,
        Password = Password,
    };

    public override string ToString()
    {
        var text = new StringBuilder(KindName).Append(" — ").Append(Describe());

        return text.ToString();
    }
}
