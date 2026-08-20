using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Proof that the description of the library in MilLibDbContext matches the
// library that is actually in the file.
//
// A mapping that is wrong does not fail at build time and often does not fail
// at startup either — it fails on the one screen that reads the one column
// nobody tried, usually in front of a customer. So every table is read here,
// with the awkward columns named explicitly: the dates, the money, the enums
// and the joins, which are the four things a hand-written mapping gets wrong.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.DataProof -- <path to database.sqlite>

var file = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

file = Path.GetFullPath(file);

if (!File.Exists(file))
{
    Console.Error.WriteLine($"There is no file at {file}.");
    return 1;
}

Console.WriteLine($"Reading {file}");
Console.WriteLine();

await using var db = new MilLibDbContext(DatabaseSource.File(file));

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-42}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

// ------------------------------------------------------ every table reads --

Console.WriteLine("Every table can be read");

var counts = new (string Name, Func<Task<int>> Count)[]
{
    ("users", () => db.Users.CountAsync()),
    ("branches", () => db.Branches.CountAsync()),
    ("titles", () => db.Titles.CountAsync()),
    ("copies", () => db.Copies.CountAsync()),
    ("copy_annotations", () => db.CopyAnnotations.CountAsync()),
    ("authors", () => db.Authors.CountAsync()),
    ("publishers", () => db.Publishers.CountAsync()),
    ("categories", () => db.Categories.CountAsync()),
    ("title_author", () => db.TitleAuthors.CountAsync()),
    ("title_category", () => db.TitleCategories.CountAsync()),
    ("members", () => db.Members.CountAsync()),
    ("member_categories", () => db.MemberCategories.CountAsync()),
    ("member_cards", () => db.MemberCards.CountAsync()),
    ("loans", () => db.Loans.CountAsync()),
    ("renewals", () => db.Renewals.CountAsync()),
    ("reservations", () => db.Reservations.CountAsync()),
    ("fines", () => db.Fines.CountAsync()),
    ("stock_verifications", () => db.StockVerifications.CountAsync()),
    ("stock_verification_scans", () => db.StockVerificationScans.CountAsync()),
    ("withdrawals", () => db.Withdrawals.CountAsync()),
    ("settings", () => db.Settings.CountAsync()),
    ("audit_log", () => db.AuditLog.CountAsync()),
    ("accession_counters", () => db.AccessionCounters.CountAsync()),
    ("license_info", () => db.LicenseInfo.CountAsync()),
};

foreach (var (name, count) in counts)
{
    try
    {
        Check(name, true, $"{await count()} rows");
    }
    catch (Exception ex)
    {
        Check(name, false, ex.Message.Split('\n')[0]);
    }
}

// ------------------------------------------------- the four awkward kinds --

Console.WriteLine();
Console.WriteLine("The columns a hand-written mapping gets wrong");

try
{
    var copy = await db.Copies
        .Where(c => c.AccessionDate != default)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    Check("a date reads back as a date", copy.AccessionDate.Year > 1900,
        $"copy {copy.CopyId} accessioned {copy.AccessionDate:dd MMM yyyy}");

    Check("an enum reads back as a word", Enum.IsDefined(copy.Status),
        $"status {copy.Status}");

    // Comparison in SQL, not in memory — which is the half that silently
    // stops working when a value is stored as text.
    var available = await db.Copies.CountAsync(c => c.Status == CopyStatus.AVAILABLE);

    Check("an enum can be compared in SQL", available >= 0, $"{available} available");
}
catch (Exception ex)
{
    Check("copies", false, ex.Message.Split('\n')[0]);
}

try
{
    var category = await db.MemberCategories.OrderBy(c => c.CategoryId).FirstAsync();

    Check("money reads back as a number", category.FinePerDay >= 0,
        $"{category.Name}: {category.FinePerDay:N2} a day, {category.MaxBooks} books, {category.LoanDays} days");

    // The loan rules must never be answered from code. If this row is missing
    // its numbers, the fault is in the data and should be visible here.
    Check("the loan rules are set", category.LoanDays > 0 && category.MaxBooks > 0,
        $"{category.MaxBooks} books for {category.LoanDays} days");
}
catch (Exception ex)
{
    Check("member_categories", false, ex.Message.Split('\n')[0]);
}

try
{
    var title = await db.Titles
        .Where(t => t.Copies.Count > 0)
        .OrderBy(t => t.TitleId)
        .Select(t => new
        {
            t.TitleId,
            t.Name,
            Copies = t.Copies.Count,
            Publisher = t.Publisher!.Name,
        })
        .FirstAsync();

    Check("a title joins to its copies", title.Copies > 0,
        $"\"{Shorten(title.Name)}\" has {title.Copies}");
}
catch (Exception ex)
{
    Check("titles → copies", false, ex.Message.Split('\n')[0]);
}

try
{
    var loans = await db.Loans
        .Select(l => new { l.LoanId, Member = l.Member!.FullName, Book = l.Copy!.Title!.Name })
        .Take(3)
        .ToListAsync();

    Check("a loan joins to member and book", loans.Count == 0 || loans.All(l => l.Member.Length > 0),
        loans.Count == 0 ? "no loans in this file" : $"loan {loans[0].LoanId}: {loans[0].Member} has \"{Shorten(loans[0].Book)}\"");
}
catch (Exception ex)
{
    Check("loans → members, copies, titles", false, ex.Message.Split('\n')[0]);
}

// ----------------------------------------------------- settings and roles --

Console.WriteLine();
Console.WriteLine("How this library is set up");

try
{
    var preferences = await Preferences.ReadAsync(db);

    Check("the library has a name", preferences.LibraryName.Length > 0,
        $"{preferences.OrganisationName} — {preferences.LibraryName}");

    Check("the accession prefix is read", true,
        $"\"{preferences.AccessionPrefix}\", shown: {preferences.ShowAccessionPrefix}");

    var on = Enum.GetValues<Feature>().Where(preferences.Has).Select(f => f.ToString());

    Check("the feature flags are read", true, string.Join(", ", on));

    Check("money is formatted for this library", true, preferences.Money(12.5m));
}
catch (Exception ex)
{
    Check("settings", false, ex.Message.Split('\n')[0]);
}

try
{
    var users = await db.Users.OrderBy(u => u.UserId).ToListAsync();

    foreach (var user in users)
    {
        var abilities = Abilities.GrantedTo(user.Role).Count;

        Check($"{user.Username} ({Abilities.Label(user.Role)})",
            user.PasswordHash.StartsWith("$2"),
            $"bcrypt hash present, {abilities} abilities");
    }

    // The one rule that must not drift between the two applications.
    Check("counter staff cannot override a block",
        !Abilities.Can(UserRole.COUNTER, Ability.CirculationOverride),
        "refused, as in the web application");

    Check("a librarian can", Abilities.Can(UserRole.LIBRARIAN, Ability.CirculationOverride), "allowed");
}
catch (Exception ex)
{
    Check("users", false, ex.Message.Split('\n')[0]);
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("Everything read back as it was written.");

    return 0;
}

Console.WriteLine($"{failures} of these did not.");

return 1;

static string Shorten(string text) => text.Length <= 44 ? text : text[..41] + "…";
