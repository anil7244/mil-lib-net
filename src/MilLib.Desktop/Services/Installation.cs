using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MilLib.Core.Data;

namespace MilLib.Desktop.Services;

/// <summary>
/// The few things this copy of the application has to know before it can open
/// anything: which database to use, and whether to keep backups.
///
/// It cannot live in the database, for the obvious reason. It is a small file
/// beside the executable, which also means a folder handed to somebody carries
/// its own settings and cannot inherit anybody else's.
/// </summary>
public class Installation
{
    public string Provider { get; set; } = "sqlite";
    public string FilePath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>
    /// The password, encrypted for this Windows account on this machine.
    ///
    /// Not a secret store, and not pretending to be one: anybody signed in as
    /// this user can decrypt it, which is the same as saying they could have
    /// read it out of the application anyway. What it does buy is that the file
    /// carries no readable password if it is copied, mailed or backed up
    /// somewhere it should not have been.
    /// </summary>
    public string PasswordProtected { get; set; } = "";

    public bool BackupsOn { get; set; }
    public int BackupEveryHours { get; set; } = 24;
    public int BackupsToKeep { get; set; } = 14;
    public string LastBackupAt { get; set; } = "";

    // ------------------------------------------------------------- storage --

    private static string Path => System.IO.Path.Combine(
        AppContext.BaseDirectory, "data", "installation.json");

    private static Installation? _loaded;

    public static Installation Current => _loaded ??= Load();

    private static Installation Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var text = File.ReadAllText(Path);

                var settings = JsonSerializer.Deserialize<Installation>(text);

                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            // A settings file that cannot be read must not stop the application
            // starting; it falls back to the file beside itself, which is what
            // an untouched installation uses anyway.
            Faults.Record("reading installation.json", ex);
        }

        return new Installation();
    }

    public void Save()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;

        Directory.CreateDirectory(folder);

        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        _loaded = this;

        // Everything that has already opened a connection is now holding the
        // wrong one.
        Workspace.Forget();
    }

    // ------------------------------------------------------------ password --

    /// <summary>
    /// The password in the clear, for the moment it is needed to open a
    /// connection.
    ///
    /// Never written to the file. Without this attribute the serialiser
    /// helpfully includes it, and the encrypted copy beside it becomes
    /// decoration — the whole point was that a folder handed to somebody
    /// carries no readable password.
    /// </summary>
    [JsonIgnore]
    public string Password
    {
        get
        {
            if (PasswordProtected.Length == 0)
            {
                return "";
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Written by a different Windows account, or on a different
                // machine. Treated as no password rather than as a crash; the
                // screen asks for it again.
                return "";
            }
        }
        set => PasswordProtected = value.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    // -------------------------------------------------------------- source --

    [JsonIgnore]
    public DatabaseKind Kind => Provider switch
    {
        "mysql" => DatabaseKind.MySql,
        "postgres" => DatabaseKind.Postgres,
        _ => DatabaseKind.Sqlite,
    };

    public DatabaseSource ToSource(string? sqliteFallback = null) => new()
    {
        Kind = Kind,
        FilePath = Kind == DatabaseKind.Sqlite && string.IsNullOrWhiteSpace(FilePath)
            ? sqliteFallback ?? ""
            : FilePath,
        Host = Host,
        Port = Port,
        Database = Database,
        Username = Username,
        Password = Password,
    };

    public static string NameOf(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => "mysql",
        DatabaseKind.Postgres => "postgres",
        _ => "sqlite",
    };
}
