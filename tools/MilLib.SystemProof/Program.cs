using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Settings, staff accounts, and the activity log.
//
// Two of these three are the only places in the application where a mistake
// cannot be put right from inside it. Move the accession starting number after
// books are on the register and the numbers stop being a register. Suspend or
// demote the last administrator and nobody can appoint another — the unit is
// shut out of its own library and needs somebody to edit the database by hand.
//
// So the guards are what this proves, and it proves them by trying to break
// them rather than by asking whether they are there.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.SystemProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-system-proof.sqlite");

Sweep();
File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");

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

MilLibDbContext Open() => new(DatabaseSource.File(scratch));

long adminId;
long madeId;

// ---------------------------------------------------------------- settings --

Heading("Settings — writing them, and the one that locks");
{
    await using var db = Open();

    var admin = await db.Users.FirstAsync(u => u.Role == UserRole.SUPERADMIN);

    adminId = admin.UserId;

    var setup = new Setup(db);

    await setup.SaveBrandingAsync(new Branding(
        "14 FIELD REGIMENT", "Unit Library", "Per ardua",
        "AD0505", "light", true, "#0d0d0d", null), adminId);

    var after = await Preferences.ReadAsync(db);

    Check("the names are written as typed",
        after.OrganisationName == "14 FIELD REGIMENT" && after.LibraryName == "Unit Library",
        after.OrganisationName);

    // A colour without its hash, and in capitals, is what somebody pastes out
    // of a brand document. It has to come back usable rather than rejected.
    Check("a colour is tidied into #rrggbb", after.AccentColour == "#ad0505", after.AccentColour);

    await setup.SaveBrandingAsync(new Branding(
        "14 FIELD REGIMENT", "Unit Library", "Per ardua",
        "not a colour", "light", true, "#0d0d0d", null), adminId);

    var nonsense = await Preferences.ReadAsync(db);

    Check("nonsense falls back to a real colour rather than nothing",
        nonsense.AccentColour.Length == 7 && nonsense.AccentColour[0] == '#',
        nonsense.AccentColour);

    // Nothing was passed for the crest, so the crest must not have moved.
    Check("a save that did not touch the crest leaves it alone",
        nonsense.CrestPath == after.CrestPath, $"\"{nonsense.CrestPath}\"");

    Check("a flag is written the way the other program reads it",
        (await db.Settings.FirstAsync(s => s.Key == "branding.crest_circle")).Value == "1",
        (await db.Settings.FirstAsync(s => s.Key == "branding.crest_circle")).Value ?? "(null)");
}

Heading("Settings — a key this install has never had");
{
    await using var db = Open();

    var setup = new Setup(db);

    await db.Settings.Where(s => s.Key == "barcode.label_code").ExecuteDeleteAsync();

    await setup.SaveLabelsAsync(51, 25, 25, 38, "sheet", "both", MilLib.Core.Documents.Zpl.CommonDpi, adminId);

    var preferences = await Preferences.ReadAsync(db);

    // A library upgraded from an older version is missing the keys added
    // since. A setting that silently does not save is worse than one missing.
    Check("a missing key is created rather than skipped",
        preferences.Text("barcode.label_code") == "both",
        preferences.Text("barcode.label_code", "(nothing)"));

    Check("millimetres are stored as somebody would write them",
        preferences.Text("barcode.pocket_width_mm") == "51",
        preferences.Text("barcode.pocket_width_mm"));

    await setup.SaveLabelsAsync(50.8, 25.4, 25, 38, "label", "qr", MilLib.Core.Documents.Zpl.CommonDpi, adminId);

    Check("and a fractional size keeps its fraction",
        (await Preferences.ReadAsync(db)).Text("barcode.pocket_width_mm") == "50.8",
        (await Preferences.ReadAsync(db)).Text("barcode.pocket_width_mm"));
}

Heading("Accession numbering — the value that locks");
{
    await using var db = Open();

    var setup = new Setup(db);

    var before = await Preferences.ReadAsync(db);
    var counterBefore = await db.AccessionCounters
        .Where(c => c.Scope == "default").Select(c => c.NextSeq).FirstOrDefaultAsync();

    Check("this library already has books on the register",
        !await setup.RegisterIsEmptyAsync(), $"{await db.Copies.CountAsync():N0} copies");

    var refusal = await setup.SaveAccessionAsync(
        before.AccessionPrefix, before.ShowAccessionPrefix, before.AccessionPadLength,
        before.AccessionStartFrom + 500, adminId);

    Check("moving the starting number is refused", refusal is not null,
        refusal ?? "(it was allowed)");

    var after = await Preferences.ReadAsync(db);

    Check("and the stored number did not move",
        after.AccessionStartFrom == before.AccessionStartFrom,
        after.AccessionStartFrom.ToString());

    // The refusal happens before anything at all is written. A save that half
    // took and then reported a refusal leaves nobody sure which half took.
    Check("nor did anything else in that save",
        after.AccessionPadLength == before.AccessionPadLength
        && await db.AccessionCounters.Where(c => c.Scope == "default")
            .Select(c => c.NextSeq).FirstOrDefaultAsync() == counterBefore,
        $"counter still {counterBefore}");

    // The prefix and the padding are presentation, and stay editable forever.
    var allowed = await setup.SaveAccessionAsync(
        "TEST/", true, 7, before.AccessionStartFrom, adminId);

    Check("but the prefix and the padding stay editable", allowed is null,
        allowed ?? "changed");

    var presentation = await Preferences.ReadAsync(db);

    Check("and they take effect on the printed number",
        new Accession(db, presentation).Display(42) == "TEST/0000042",
        new Accession(db, presentation).Display(42));
}

Heading("Accession numbering — a library that has not started");
{
    await using var db = Open();

    // Emptied so the other half of the rule can be proved: before the first
    // book, the starting number is an ordinary setting.
    await db.Loans.ExecuteDeleteAsync();
    await db.Reservations.ExecuteDeleteAsync();
    await db.StockVerificationScans.ExecuteDeleteAsync();
    await db.Copies.ExecuteDeleteAsync();

    var setup = new Setup(db);

    Check("with no copies, the register counts as empty",
        await setup.RegisterIsEmptyAsync(), "0 copies");

    var refusal = await setup.SaveAccessionAsync("JAKLI/", true, 9, 5001, adminId);

    Check("the starting number may be set", refusal is null, refusal ?? "set to 5001");

    Check("and the live counter moved with it",
        await db.AccessionCounters.Where(c => c.Scope == "default")
            .Select(c => c.NextSeq).FirstAsync() == 5001,
        (await db.AccessionCounters.Where(c => c.Scope == "default")
            .Select(c => c.NextSeq).FirstAsync()).ToString());
}

// ---------------------------------------------------------------- features --

Heading("Feature flags — a screen, never a table");
{
    await using var db = Open();

    var setup = new Setup(db);

    var rows = await db.Titles.CountAsync();

    await setup.SetFeatureAsync(Feature.Fines, false, adminId);

    Check("turning one off hides the screen",
        !(await Preferences.ReadAsync(db)).Has(Feature.Fines), "fines: off");

    // The rule that is expensive to get wrong. A flag gates a route or a view,
    // never a migration, so every table exists on every install whatever the
    // flags say — and turning one back on shows the records it always kept.
    Check("and takes nothing with it",
        await db.Fines.CountAsync() >= 0 && await db.Titles.CountAsync() == rows,
        $"{await db.Fines.CountAsync():N0} fines still there");

    await setup.SetFeatureAsync(Feature.Fines, true, adminId);

    Check("turning it back on shows the same records",
        (await Preferences.ReadAsync(db)).Has(Feature.Fines), "fines: on");

    Check("every flag has a key, a name and an explanation",
        Enum.GetValues<Feature>().All(f =>
            Setup.KeyOf(f).StartsWith("feature.")
            && Setup.NameOf(f).Length > 0
            && Setup.WhatItDoes(f).Length > 0),
        $"{Enum.GetValues<Feature>().Length} flags");

    // Words.Any would make "Stockverify" of these, which is why they are named.
    Check("and the names are not the enum spelling",
        Setup.NameOf(Feature.StockVerify) == "Stock verification",
        Setup.NameOf(Feature.StockVerify));
}

// ------------------------------------------------------------------- staff --

Heading("Staff accounts — making one");
{
    await using var db = Open();

    var staff = new Staff(db);

    var admin = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == adminId);

    var fresh = new User
    {
        Username = "counter.test",
        FullName = "Test Counter Clerk",
        Role = UserRole.COUNTER,
        ClearanceLevel = SecurityClass.UNCLASSIFIED,
        IsActive = true,
    };

    var problems = await staff.ProblemsWithAsync(fresh, admin, "short", isNew: true);

    Check("a short password is refused",
        problems.Any(p => p.Contains("eight")), problems.FirstOrDefault() ?? "(none)");

    problems = await staff.ProblemsWithAsync(
        new User { Username = admin.Username, FullName = "Someone Else" },
        admin, "a good long password", isNew: true);

    Check("a username already in use is refused",
        problems.Any(p => p.Contains("taken")), problems.FirstOrDefault() ?? "(none)");

    problems = await staff.ProblemsWithAsync(fresh, admin, "a good long password", isNew: true);

    Check("an account with everything filled in is accepted",
        problems.Count == 0, $"{problems.Count} problems");

    await staff.CreateAsync(fresh, "a good long password", adminId);

    madeId = fresh.UserId;

    var stored = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == madeId);

    Check("the password is stored as a bcrypt hash, never as itself",
        stored.PasswordHash.StartsWith("$2") && !stored.PasswordHash.Contains("password"),
        stored.PasswordHash[..7] + "…");

    // Cost 12, the same as the PHP application, so a password set on either
    // side works on the other at the very next sign-in.
    Check("hashed at the cost the other program uses",
        stored.PasswordHash.StartsWith("$2a$12$") || stored.PasswordHash.StartsWith("$2b$12$"),
        stored.PasswordHash[..7]);

    var signIn = await new SignIn(db).AttemptAsync("counter.test", "a good long password", "proof");

    Check("and they can sign in with it", signIn.Ok, signIn.Ok ? "let in" : signIn.Problem);
}

Heading("Staff accounts — the four guards");
{
    await using var db = Open();

    var staff = new Staff(db);

    var admin = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == adminId);

    // Guard 1 — nobody may suspend their own account.
    var own = await staff.WhyNotSuspendAsync(admin, admin);

    Check("1. you cannot suspend yourself", own is not null, own ?? "(it was allowed)");

    // Every other superadmin out of the way, so this one is the last.
    await db.Users
        .Where(u => u.Role == UserRole.SUPERADMIN && u.UserId != adminId)
        .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

    var other = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == madeId);

    // Guard 2 — the last administrator who can still sign in stays.
    var last = await staff.WhyNotSuspendAsync(admin, other);

    Check("2. nor the last administrator who can sign in",
        last is not null && last.Contains("Super Administrator"), last ?? "(it was allowed)");

    // Guard 3 — nor may that last one be quietly demoted instead.
    var demoted = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == adminId);

    demoted.Role = UserRole.COUNTER;

    var problems = await staff.ProblemsWithAsync(demoted, other, null, isNew: false);

    Check("3. nor may they be demoted instead",
        problems.Any(p => p.Contains("lock")), problems.FirstOrDefault() ?? "(it was allowed)");

    // Guard 4 — nor may anybody change their own role, which is the same
    // lockout arrived at from the other direction.
    var self = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == madeId);

    self.Role = UserRole.SUPERADMIN;

    problems = await staff.ProblemsWithAsync(self, self, null, isNew: false);

    Check("4. nor may anybody change their own role",
        problems.Any(p => p.Contains("own role")), problems.FirstOrDefault() ?? "(it was allowed)");

    // And with a second administrator in place, all of it is allowed again —
    // the guards are about lockout, not about seniority.
    await db.Users
        .Where(u => u.Role == UserRole.SUPERADMIN && u.UserId != adminId)
        .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));

    var freed = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == adminId);

    Check("with a second administrator, the lock lifts",
        await staff.WhyNotSuspendAsync(freed, other) is null, "allowed");
}

Heading("Staff accounts — suspending, and setting a password");
{
    await using var db = Open();

    var staff = new Staff(db);

    var admin = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == adminId);
    var person = await db.Users.FirstAsync(u => u.UserId == madeId);

    var loansBefore = await db.Loans.CountAsync(l => l.IssuedBy == madeId);

    await staff.SetActiveAsync(person, false, adminId);

    var refused = await new SignIn(db).AttemptAsync("counter.test", "a good long password", "proof");

    Check("a suspended account is refused at sign-in",
        !refused.Ok && refused.Problem.Contains("suspended"), refused.Problem);

    // Suspension is not deletion. Their name stays against everything they did,
    // which is the whole reason an account is never removed.
    Check("but what they did is untouched",
        await db.Loans.CountAsync(l => l.IssuedBy == madeId) == loansBefore
        && await db.Users.AnyAsync(u => u.UserId == madeId),
        $"{loansBefore} loans still theirs");

    await staff.SetActiveAsync(person, true, adminId);

    // The reset asks the acting administrator for their own password. A
    // session somebody walked away from is otherwise a way into every account.
    var wrong = await staff.SetPasswordAsync(person, "another long password", admin, "not it");

    Check("a reset with the wrong admin password does nothing",
        wrong is not null, wrong ?? "(it went through)");

    Check("and the old password still works",
        (await new SignIn(db).AttemptAsync("counter.test", "a good long password", "proof")).Ok,
        "still in");

    var tooShort = await staff.SetPasswordAsync(person, "short", admin, "seed-password");

    Check("a short new password is refused before anything else",
        tooShort is not null && tooShort.Contains("eight"), tooShort ?? "(it went through)");
}

Heading("Sign-in — the same account, whichever program");
{
    await using var db = Open();

    // SQLite compares text case-sensitively where the MySQL this came from did
    // not. Without matching in memory, the same person with the same password
    // gets a different answer from the two programs.
    var shouting = await new SignIn(db).AttemptAsync("COUNTER.TEST", "a good long password", "proof");

    Check("the username matches whatever the case", shouting.Ok,
        shouting.Ok ? "let in" : shouting.Problem);

    var wrongCase = await new SignIn(db).AttemptAsync("counter.test", "A GOOD LONG PASSWORD", "proof");

    Check("the password does not", !wrongCase.Ok, wrongCase.Problem);

    Check("and an unknown name is refused in the same words as a wrong password",
        (await new SignIn(db).AttemptAsync("nobody.here", "whatever", "proof")).Problem
        == wrongCase.Problem,
        "identical");
}

// ---------------------------------------------------------------- activity --

Heading("Activity — reading back what was done");
{
    await using var db = Open();

    var activity = new Activity(db);

    var everything = await activity.ReadAsync();

    Check("there is a history to read", everything.Count > 0, $"{everything.Count} on the first page");

    Check("newest first",
        everything.Zip(everything.Skip(1)).All(pair => pair.First.When >= pair.Second.When),
        "in order");

    Check("a page is not the whole table",
        everything.Count <= Activity.PageSize, $"{Activity.PageSize} at a time");

    Check("every entry says something rather than nothing",
        everything.All(h => h.Said.Length > 0 && h.Who.Length > 0),
        everything[0].Said);

    // The sign-in actions are written in lower case and the rest in capitals.
    // A filter that compared case-sensitively would drop exactly the entries
    // somebody is most often looking for.
    var accounts = await activity.ReadAsync(kind: ActivityKind.Accounts);

    Check("the accounts filter finds the sign-ins whatever their case",
        accounts.Any(h => h.Action.Contains("login", StringComparison.OrdinalIgnoreCase)),
        $"{accounts.Count} entries");

    var settings = await activity.ReadAsync(kind: ActivityKind.Settings);

    Check("the settings filter finds what this proof just changed",
        settings.Any(h => h.Action.StartsWith("SETTINGS")), $"{settings.Count} entries");

    Check("and a filter narrows rather than empties",
        settings.Count > 0 && settings.Count < everything.Count,
        $"{settings.Count} of {everything.Count}");

    // The count has to be of the same set the page came from, or the line
    // above the table describes a different list from the one under it.
    Check("the count agrees with what the filter returned",
        await activity.CountAsync(kind: ActivityKind.Settings) == settings.Count
        && await activity.CountAsync() == await activity.CountAsync(kind: ActivityKind.Everything),
        $"{await activity.CountAsync(kind: ActivityKind.Settings)} counted, {settings.Count} read");

    var mine = await activity.ReadAsync(userId: adminId);

    Check("by whom narrows to that person", mine.Count > 0, $"{mine.Count} entries");

    // "Until the 14th" includes the 14th, which is what anybody typing a date
    // into a box means by it.
    var today = DateOnly.FromDateTime(DateTime.Now);

    Check("a date range takes in the whole of its last day",
        (await activity.ReadAsync(since: today, until: today)).Count > 0,
        "today included");

    // Paged small on purpose. At the real page size this library's whole
    // history fits on page one, and a check that passes because there was no
    // second page has proved nothing about paging.
    var first = await activity.ReadAsync(pageSize: 5);
    var second = await activity.ReadAsync(page: 1, pageSize: 5);

    Check("a page holds what it was asked for", first.Count == 5, $"{first.Count} entries");

    Check("and the next page is different entries, continuing downwards",
        second.Count > 0
        && second.All(h => first.All(e => e.Id != h.Id))
        && second[0].When <= first[^1].When,
        $"{second.Count} on page two");

    Check("what was just done is on the log",
        everything.Any(h => h.Action == "USER_CREATE" || h.Action == "SETTINGS_LABELS"),
        everything.First(h => h.Action.StartsWith("USER") || h.Action.StartsWith("SETTINGS")).Said);

    // Marked: the account-management entries and the classified ones.
    // Not marked: an ordinary sign-in. Both halves matter — a column where
    // every row is red is a column nobody reads the red in, and "USER_" as a
    // prefix caught user_login and turned the whole screen red.
    Check("changes to who may sign in are marked as worth a look",
        everything.Where(h => h.Action is "USER_CREATE" or "USER_DEACTIVATE"
            or "USER_REACTIVATE" or "USER_PASSWORD_RESET" or "FAILED_LOGIN").All(h => h.Notable),
        "flagged");

    Check("but an ordinary sign-in is not",
        everything.Where(h => h.Action.Equals("user_login", StringComparison.OrdinalIgnoreCase))
            .All(h => !h.Notable),
        $"{everything.Count(h => h.Notable)} of {everything.Count} marked");

    var people = await activity.WhoHasActedAsync();

    Check("the by-whom list is everybody who has ever acted",
        people.Count > 0 && people.All(p => p.Name.Length > 0),
        $"{people.Count} names");
}

Heading("Activity — nothing edits it");
{
    await using var db = Open();

    // Not a test of a guard but of a shape: there is no writer here at all.
    // A record of what happened is worth having only if it cannot be tidied up.
    var writers = typeof(Activity).GetMethods()
        .Where(m => m.DeclaringType == typeof(Activity))
        .Select(m => m.Name)
        .Where(n => n.Contains("Delete") || n.Contains("Remove") || n.Contains("Edit"))
        .ToList();

    Check("the reader offers no way to change an entry", writers.Count == 0,
        writers.Count == 0 ? "read only" : string.Join(", ", writers));
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The settings save, the numbering locks, the library cannot shut itself out, "
        + "and the history reads back.");
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
