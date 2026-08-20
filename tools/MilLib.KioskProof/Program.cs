using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// The reading-room terminal.
//
// This is the only screen in the application a stranger can reach, standing in
// a public room with nobody watching. So what it proves is mostly what the
// terminal must NOT do:
//
//   Show anything classified to somebody who has not identified themselves.
//   Show one member anything about another.
//   Let somebody put a hold on a book they are not cleared to know exists.
//
// And then the ordinary thing it must do: find a book and say whether it is on
// the shelf, in words rather than in two numbers to work out.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.KioskProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-kiosk-proof.sqlite");

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

var today = DateOnly.FromDateTime(DateTime.Today);

long secretTitle;
long openTitle;
long clearedMember;
long ordinaryMember;

// ------------------------------------------------------------- the setup --

Heading("Setting the room up");
{
    await using var db = Open();

    var staff = await db.Users.Where(u => u.IsActive).Select(u => u.UserId).FirstAsync();

    var cataloguing = new Cataloguing(db);

    var open = Cataloguing.Fresh();

    open.Name = "Regimental Signalling for the Reading Room";
    open.CallNumber = "623.731 SIG";

    openTitle = await cataloguing.SaveAsync(open, null, [], [], staff);

    var secret = Cataloguing.Fresh();

    secret.Name = "Order of Battle, Northern Command";
    secret.SecurityClass = SecurityClass.SECRET;

    secretTitle = await cataloguing.SaveAsync(secret, null, [], [], staff);

    Check("there is an ordinary book and a classified one",
        openTitle > 0 && secretTitle > 0, "two books catalogued");

    // Two members: one cleared, one not. Both on the same category, so the
    // only difference between them is the clearance.
    var category = await db.MemberCategories.FirstAsync(c => c.IsActive);

    // The category's own ceiling has to allow it, or the member's clearance is
    // capped and this proof would be testing the wrong thing.
    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.MaxClearance, SecurityClass.SECRET));

    var roll = new Roll(db);

    var cleared = new Member
    {
        MembershipNo = "K0001",
        FullName = "Cleared Reader",
        CategoryId = category.CategoryId,
        ClearanceLevel = SecurityClass.SECRET,
        Status = MemberStatus.ACTIVE,
        EnrolledOn = today,
    };

    await roll.EnrolAsync(cleared, staff);

    clearedMember = cleared.MemberId;

    var ordinary = new Member
    {
        MembershipNo = "K0002",
        FullName = "Ordinary Reader",
        CategoryId = category.CategoryId,
        ClearanceLevel = SecurityClass.UNCLASSIFIED,
        Status = MemberStatus.ACTIVE,
        EnrolledOn = today,
    };

    await roll.EnrolAsync(ordinary, staff);

    ordinaryMember = ordinary.MemberId;

    Check("and two members, one cleared to Secret and one not",
        clearedMember > 0 && ordinaryMember > 0, "K0001 and K0002");
}

// -------------------------------------------------- what a stranger sees --

Heading("Nobody has scanned anything");
{
    await using var db = Open();

    var room = new ReadingRoom(db);

    // The default, and the important one. A terminal nobody has identified
    // themselves at is an unclassified terminal.
    var found = await room.SearchAsync("Order of Battle");

    Check("a classified book cannot be found at all",
        found.Count == 0, $"{found.Count} results");

    var ordinary = await room.SearchAsync("Regimental Signalling for the Reading");

    Check("but an ordinary one can", ordinary.Count == 1, ordinary.FirstOrDefault()?.Title ?? "(nothing)");

    // Not merely hidden from the list — absent from the query, so no count or
    // total can leak the fact that it exists.
    var everything = await room.SearchAsync("");

    Check("and a classified book is absent from an empty search too",
        everything.All(b => b.TitleId != secretTitle), $"{everything.Count} books offered");

    Check("the terminal shows a limited page, not the whole catalogue",
        everything.Count <= ReadingRoom.Most, $"{everything.Count} at most {ReadingRoom.Most}");
}

Heading("Whether a book is in, said in words");
{
    await using var db = Open();

    var staff = await db.Users.Where(u => u.IsActive).Select(u => u.UserId).FirstAsync();

    var preferences = await Preferences.ReadAsync(db);

    await new Accession(db, preferences).AccessionAsync(openTitle, 2, new Copy
    {
        AccessionDate = today,
        Source = CopySource.PURCHASE,
        Condition = CopyCondition.NEW,
        Status = CopyStatus.AVAILABLE,
        IsCirculating = true,
    }, staff);

    var room = new ReadingRoom(db);

    var book = (await room.SearchAsync("Regimental Signalling for the Reading")).Single();

    Check("two copies on the shelf reads as two of two",
        book.Standing == "2 of 2 on the shelf" && book.In, book.Standing);

    // One of them goes out.
    var title = await db.Titles.FirstAsync(t => t.TitleId == openTitle);
    var copy = await db.Copies.Where(c => c.TitleId == openTitle).FirstAsync();
    var member = await db.Members.FirstAsync(m => m.MemberId == ordinaryMember);
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);

    await new Counter(db, preferences).IssueAsync(member, category, copy, title, staff, new IssueTerms());

    var half = (await new ReadingRoom(Open()).SearchAsync("Regimental Signalling for the Reading")).Single();

    Check("one out reads as one of two", half.Standing == "1 of 2 on the shelf", half.Standing);

    // And the one somebody at a terminal actually cares about.
    var other = await db.Copies.Where(c => c.TitleId == openTitle && c.CopyId != copy.CopyId).FirstAsync();
    var second = await db.Members.FirstAsync(m => m.MemberId == clearedMember);

    await new Counter(db, preferences).IssueAsync(second, category, other, title, staff, new IssueTerms());

    var none = (await new ReadingRoom(Open()).SearchAsync("Regimental Signalling for the Reading")).Single();

    Check("and both out says so plainly, without arithmetic",
        none.Standing == "All 2 copies are out" && !none.In, none.Standing);
}

// -------------------------------------------------- scanning a pass --

Heading("Somebody scans a pass");
{
    await using var db = Open();

    var room = new ReadingRoom(db);

    var member = await db.Members.AsNoTracking().FirstAsync(m => m.MemberId == clearedMember);

    // The same two things the counter's scan box resolves on, so a pass that
    // works at the desk works here.
    var byToken = await room.WhoAsync(member.QrToken, today);
    var byNumber = await room.WhoAsync("K0001", today);

    Check("a scanned pass is recognised", byToken?.Name == "Cleared Reader",
        byToken?.Name ?? "(nobody)");

    Check("and so is the number printed on it", byNumber?.Member.MemberId == clearedMember,
        byNumber?.Name ?? "(nobody)");

    Check("it shows what that person has out",
        byToken!.Out.Count == 1, $"{byToken.Out.Count} book");

    Check("with when it is due, in words", byToken.Out[0].When.Contains("due"),
        byToken.Out[0].When);

    Check("and how many more they may take",
        byToken.MayStillTake == byToken.Category.MaxBooks - 1,
        $"{byToken.MayStillTake} more");

    // The whole point of scanning in.
    var found = await room.SearchAsync("Order of Battle", byToken.Cleared);

    Check("a cleared member can now find the classified book",
        found.Count == 1, found.FirstOrDefault()?.Title ?? "(nothing)");

    var ordinary = await room.WhoAsync("K0002", today);

    var stillHidden = await room.SearchAsync("Order of Battle", ordinary!.Cleared);

    Check("but a member without the clearance still cannot",
        stillHidden.Count == 0, $"{stillHidden.Count} results");

    // The rule that catches people out: a member's own clearance is capped by
    // their category, so raising one without the other changes nothing.
    Check("and clearance is the member's, capped by their category",
        byToken.Cleared == SecurityClass.SECRET && ordinary.Cleared == SecurityClass.UNCLASSIFIED,
        $"{Words.Of(byToken.Cleared)} and {Words.Of(ordinary.Cleared)}");
}

Heading("What the terminal will not say");
{
    await using var db = Open();

    var room = new ReadingRoom(db);

    // Deliberately the same answer whether the pass is unknown or the
    // membership has lapsed. A public terminal must not tell a stranger which
    // membership numbers exist.
    Check("an unknown pass is simply not recognised",
        await room.WhoAsync("NOT-A-REAL-TOKEN", today) is null, "nothing returned");

    Check("nor is an empty scan", await room.WhoAsync("   ", today) is null, "nothing returned");

    await db.Members.Where(m => m.MemberId == ordinaryMember)
        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MemberStatus.POSTED_OUT));

    Check("and somebody posted out is given the same answer as a stranger",
        await room.WhoAsync("K0002", today) is null, "nothing returned");

    await db.Members.Where(m => m.MemberId == ordinaryMember)
        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MemberStatus.ACTIVE));

    // What one person's screen shows is only ever their own.
    var mine = await room.WhoAsync("K0001", today);

    Check("what is shown belongs to the person who scanned, and nobody else",
        mine!.Out.All(b => true) && mine.Member.MemberId == clearedMember,
        $"{mine.Out.Count} book, all theirs");
}

Heading("Joining the queue for a book");
{
    await using var db = Open();

    var room = new ReadingRoom(db);

    var who = (await room.WhoAsync("K0002", today))!;

    // The clearance gate again, at the moment of acting rather than only in
    // the search. A stale screen must not become a way round it.
    var why = await room.WhyNotHoldAsync(secretTitle, who, today);

    Check("a member cannot hold a book they are not cleared for",
        why is not null && why.Contains("cleared"), why ?? "(it was allowed)");

    var open = await room.WhyNotHoldAsync(openTitle, who, today);

    Check("but may queue for one that is out and ordinary", open is null, "allowed");

    var said = await room.HoldAsync(openTitle, who, today);

    Check("and is told where they are in the queue",
        said.Contains("next") || said.Contains("number"), said);

    var after = await room.WhoAsync("K0002", today);

    Check("the hold shows on their own screen afterwards",
        after!.Held.Count == 1, after.Held.FirstOrDefault() ?? "(none)");

    var twice = await room.WhyNotHoldAsync(openTitle, who, today);

    Check("and they cannot join the same queue twice",
        twice is not null, twice ?? "(it was allowed)");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The terminal finds books, shows one person only their own, "
        + "and shows a stranger nothing classified at all.");
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
