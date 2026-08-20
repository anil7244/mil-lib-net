using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Proof that a unit's own branding survives being saved.
//
// This is sold to units that put their own name, crest and colours on it, and
// for the buyer those are not preferences — they are what their headed paper
// looks like. So the one thing that must never happen is a save that quietly
// replaces them with somebody else's.
//
// It nearly did. A colour the code could not read was written down as the
// house red instead of being refused, so a half-typed hex code or a form saved
// before it had finished loading rebranded the unit, silently, with an
// audit-log entry saying the change was deliberate.
//
// Works on a copy of the library, never the library itself.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.BrandProof -- <path to database.sqlite>

var source = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

source = Path.GetFullPath(source);

if (!File.Exists(source))
{
    Console.Error.WriteLine($"There is no file at {source}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), $"mil-lib-brand-proof-{Environment.ProcessId}.sqlite");

File.Copy(source, scratch, overwrite: true);

Console.WriteLine($"Working on a copy of {source}");
Console.WriteLine();

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-48}  {saw}");

    if (!ok)
    {
        failures++;
    }
}

try
{
    await using var db = new MilLibDbContext(DatabaseSource.File(scratch));

    var setup = new Setup(db);

    async Task<string> AccentAsync()
    {
        db.ChangeTracker.Clear();

        return (await Preferences.ReadAsync(db)).AccentColour;
    }

    Branding Saving(string accent, string circle = "#0d0d0d") =>
        new("A UNIT", "Unit Library", "A motto", accent, "light", false, circle, null);

    // The unit sets its colour. Nothing surprising here — this is the case
    // that already worked, and it is checked so the rest means something.
    await setup.SaveBrandingAsync(Saving("#1F3A5F"), 1);
    Check("a colour a unit picks is stored", await AccentAsync() == "#1f3a5f", await AccentAsync());

    Console.WriteLine();
    Console.WriteLine("  ... and now the ways it used to be lost:");
    Console.WriteLine();

    // A form saved before it had finished loading. The fields are empty; the
    // unit's colour must not be.
    await setup.SaveBrandingAsync(Saving(""), 1);
    Check("an empty colour keeps the unit's", await AccentAsync() == "#1f3a5f", await AccentAsync());

    // Somebody deleting the last two characters to retype them, and the save
    // landing in between.
    await setup.SaveBrandingAsync(Saving("#1f3a"), 1);
    Check("half a colour keeps the unit's", await AccentAsync() == "#1f3a5f", await AccentAsync());

    // A word where a colour goes.
    await setup.SaveBrandingAsync(Saving("navy blue"), 1);
    Check("a colour by name keeps the unit's", await AccentAsync() == "#1f3a5f", await AccentAsync());

    Console.WriteLine();

    // What is still accepted, so the refusal has not been made so strict that
    // ordinary typing stops working.
    await setup.SaveBrandingAsync(Saving("2F5D3A"), 1);
    Check("a colour typed without its hash is taken", await AccentAsync() == "#2f5d3a", await AccentAsync());

    await setup.SaveBrandingAsync(Saving("  #8E2434  "), 1);
    Check("a colour pasted with spaces is taken", await AccentAsync() == "#8e2434", await AccentAsync());

    // Only an install that has never had a colour falls back to the house one.
    var virgin = await db.Settings.FirstAsync(s => s.Key == "branding.accent_colour");
    virgin.Value = "";
    db.Settings.Update(virgin);
    await db.SaveAndForgetAsync();

    await setup.SaveBrandingAsync(Saving("not a colour"), 1);
    Check("an install with no colour yet gets the house one",
        await AccentAsync() == Setup.DefaultAccent, await AccentAsync());

    // The crest's circle is the unit's too, and goes the same way.
    await setup.SaveBrandingAsync(Saving("#1f3a5f", "#2f5d3a"), 1);
    await setup.SaveBrandingAsync(Saving("#1f3a5f", "rubbish"), 1);

    db.ChangeTracker.Clear();
    var circle = (await Preferences.ReadAsync(db)).CrestCircleColour;
    Check("the crest's circle is kept the same way", circle == "#2f5d3a", circle);

    // And the rest of the record still saves, so none of this has quietly
    // stopped the name and motto from being changed.
    db.ChangeTracker.Clear();
    var after = await Preferences.ReadAsync(db);
    Check("the name and motto still save",
        after.LibraryName == "Unit Library" && after.Motto == "A motto",
        $"{after.LibraryName} — {after.Motto}");
}
finally
{
    // The pool keeps the file open after the connection is closed, so the
    // copy cannot be deleted until the pool lets go of it.
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
            // A scratch file in the temp folder. Worth tidying, not worth
            // failing a proof that has already answered its question.
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "All good. A unit's branding cannot be lost by saving over it."
    : $"{failures} thing(s) wrong.");

return failures == 0 ? 0 : 1;
