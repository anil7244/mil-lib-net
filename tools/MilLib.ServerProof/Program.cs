using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MilLib.Core.Data;

// Proof that the library reads the same on all three databases.
//
// A unit starts on the file. Two years later it has four counters and wants a
// server, and whether that server is the MariaDB somebody already runs or the
// PostgreSQL their IT cell insists on is not this application's business. What
// is this application's business is that the answer to "where are the records"
// never changes what the records say.
//
// Nothing here needs a server running. What is checked is everything that goes
// wrong silently: a default port nobody typed, a connection string missing the
// one setting that makes dates come back as they were written, a table name
// that is fine on MySQL and unfindable on PostgreSQL because it was quoted in
// capitals. Those faults do not show up until a unit has already moved.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.ServerProof

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-46}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

void Heading(string text)
{
    Console.WriteLine();
    Console.WriteLine(text);
    Console.WriteLine(new string('-', text.Length));
}

// ------------------------------------------------------------ the ports ----

Heading("Ports nobody should have to remember");

var mysql = new DatabaseSource { Kind = DatabaseKind.MySql, Host = "srv", Database = "mil_lib", Username = "lib" };
var pg = new DatabaseSource { Kind = DatabaseKind.Postgres, Host = "srv", Database = "mil_lib", Username = "lib" };

Check("MariaDB falls back to 3306", mysql.EffectivePort == 3306, mysql.EffectivePort.ToString());
Check("PostgreSQL falls back to 5432", pg.EffectivePort == 5432, pg.EffectivePort.ToString());

var typed = new DatabaseSource { Kind = DatabaseKind.Postgres, Port = 6543 };
Check("a typed port is left alone", typed.EffectivePort == 6543, typed.EffectivePort.ToString());

// ------------------------------------------------- the connection strings --

Heading("What each connection asks the server for");

Check("PostgreSQL names the host", pg.Connection.Contains("Host=srv"), Redacted(pg.Connection));
Check("PostgreSQL carries the port", pg.Connection.Contains("Port=5432"), "Port=5432");
Check("PostgreSQL tolerates a LAN certificate",
    pg.Connection.Contains("Trust Server Certificate=true"), "trusted");
Check("MariaDB keeps dates unconverted",
    mysql.Connection.Contains("AllowZeroDateTime=true"), "AllowZeroDateTime=true");

// A password must never be in anything shown on a screen or written to a log.
var secret = new DatabaseSource
{
    Kind = DatabaseKind.Postgres, Host = "srv", Database = "mil_lib",
    Username = "lib", Password = "correct-horse",
};

Check("the description omits the password",
    !secret.Describe().Contains("correct-horse") && !secret.ToString().Contains("correct-horse"),
    secret.ToString());

// ------------------------------------------------------ what is refused ----

Heading("Connections refused before anything is dialled");

var nameless = new DatabaseSource { Kind = DatabaseKind.Postgres, Host = "srv", Username = "lib" };
Check("PostgreSQL without a database is refused", nameless.Problems().Count == 1, First(nameless.Problems()));

var hostless = new DatabaseSource { Kind = DatabaseKind.Postgres, Database = "mil_lib", Username = "lib", Host = "" };
Check("PostgreSQL without a host is refused", hostless.Problems().Count == 1, First(hostless.Problems()));

Check("a complete PostgreSQL connection is allowed", pg.Problems().Count == 0, "nothing wrong");

var missingFile = DatabaseSource.File(Path.Combine(Path.GetTempPath(), "no-such-library.sqlite"));
Check("a file that is not there is refused", missingFile.Problems().Count == 1, First(missingFile.Problems()));

// --------------------------------------------------- what each is called --

Heading("What a person is told they are on");

Check("SQLite is named plainly", DatabaseSource.File("x").KindName == "SQLite file", DatabaseSource.File("x").KindName);
Check("MariaDB is named plainly", mysql.KindName == "MySQL or MariaDB", mysql.KindName);
Check("PostgreSQL is named plainly", pg.KindName == "PostgreSQL", pg.KindName);

// ---------------------------------------------------- the model itself ----

Heading("The same schema, built for each provider");

foreach (var source in new[] { DatabaseSource.File("x.sqlite"), mysql, pg })
{
    // Building the model is where a mapping fault surfaces: an enum with no
    // store type, a key EF cannot work out, a column type the provider will
    // not accept. It costs nothing and no server has to be listening.
    await using var db = new MilLibDbContext(source);

    string built;
    var ok = true;

    try
    {
        var model = db.Model;
        var tables = model.GetEntityTypes().Count();

        var capitals = model.GetEntityTypes()
            .Select(e => e.GetTableName() ?? "")
            .Where(t => t.Any(char.IsUpper))
            .ToList();

        // PostgreSQL folds unquoted names to lower case and EF quotes what it
        // is given. One capital letter in a table name is the difference
        // between a working library and "relation does not exist".
        ok = tables >= 20 && capitals.Count == 0;
        built = capitals.Count > 0
            ? $"{capitals.Count} table names carry capitals — {capitals[0]}"
            : $"{tables} tables, all lower case";
    }
    catch (Exception ex)
    {
        ok = false;
        built = ex.Message;
    }

    Check($"the model builds on {source.KindName}", ok, built);
}

// The dates-as-text conventions belong to SQLite alone. Applied to a real
// timestamp column they would write a string into it.
await using (var onFile = new MilLibDbContext(DatabaseSource.File("x.sqlite")))
await using (var onServer = new MilLibDbContext(pg))
{
    var fileDue = onFile.Model.FindEntityType(typeof(Loan))!
        .FindProperty(nameof(Loan.DueOn))!.GetValueConverter();

    var serverDue = onServer.Model.FindEntityType(typeof(Loan))!
        .FindProperty(nameof(Loan.DueOn))!.GetValueConverter();

    Check("SQLite stores a due date as text",
        fileDue?.ProviderClrType == typeof(string), fileDue?.ProviderClrType.Name ?? "as written");

    Check("PostgreSQL stores a due date as a date", serverDue is null, serverDue?.ProviderClrType.Name ?? "as written");
}

// Legacy timestamp behaviour is what stops a due date sliding by the machine's
// offset the first time a unit moves onto PostgreSQL.
Check("PostgreSQL reads timestamps as written",
    AppContext.TryGetSwitch("Npgsql.EnableLegacyTimestampBehavior", out var legacy) && legacy,
    legacy ? "no zone applied" : "TIMES WILL SHIFT");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "All good. The library reads the same on a file, on MariaDB and on PostgreSQL."
    : $"{failures} thing(s) wrong.");

return failures == 0 ? 0 : 1;

static string First(IReadOnlyList<string> problems) => problems.Count > 0 ? problems[0] : "(nothing)";

static string Redacted(string connection) => connection.Split(';')[0];
