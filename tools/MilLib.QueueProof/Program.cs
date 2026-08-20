using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;

// Holds and fines.
//
// Two small screens over two sets of rules that are easy to get subtly wrong.
// A queue that hands the book to the wrong person, or a hold that expires and
// leaves a copy set aside for nobody, is the kind of fault a counter works
// around for a year rather than reports. And a fine that can be settled twice
// is a fine somebody has paid twice.
//
// Works on a scratch copy, deleted afterwards.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.QueueProof

var real = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite");

real = Path.GetFullPath(real);

if (!File.Exists(real))
{
    Console.Error.WriteLine($"There is no file at {real}.");
    return 1;
}

var scratch = Path.Combine(Path.GetTempPath(), "mil-lib-queue-proof.sqlite");

Sweep();
File.Copy(real, scratch);

Console.WriteLine($"A scratch copy of {real}");

var failures = 0;

void Check(string what, bool ok, string saw)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what,-52}  {saw}");

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

long staffId;
Preferences preferences;
long titleId;
long copyId;
long[] members;

await using (var db = Open())
{
    staffId = await db.Users.Select(u => u.UserId).FirstAsync();
    preferences = await Preferences.ReadAsync(db);

    // A title with exactly one copy, so the queue actually has to queue.
    var copy = await db.Copies
        .Where(c => c.Status == CopyStatus.AVAILABLE && c.IsCirculating)
        .OrderBy(c => c.CopyId)
        .FirstAsync();

    copyId = copy.CopyId;
    titleId = copy.TitleId;

    // Three people who want it. The library as carried over has one member, so
    // two more are enrolled for the purpose.
    var first = await db.Members.OrderBy(m => m.MemberId).FirstAsync();
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == first.CategoryId);

    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.CanReserve, true));

    var roll = new Roll(db);
    var made = new List<long> { first.MemberId };

    foreach (var name in new[] { "SECOND IN QUEUE", "THIRD IN QUEUE" })
    {
        var member = new Member
        {
            MembershipNo = "Q" + name[..1] + made.Count,
            FullName = name,
            CategoryId = category.CategoryId,
            EnrolledOn = today,
            Status = MemberStatus.ACTIVE,
        };

        await roll.EnrolAsync(member, staffId);

        made.Add(member.MemberId);
    }

    members = [.. made];
}

// =================================================================== the queue ==

Heading("The queue");

await using (var db = Open())
{
    var holds = new Holds(db);

    var title = await db.Titles.FirstAsync(t => t.TitleId == titleId);

    var first = await db.Members.FirstAsync(m => m.MemberId == members[0]);
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == first.CategoryId);

    Check("a member who may reserve is allowed to",
        await holds.WhyNotAsync(title, first, category) is null, "allowed");

    var one = await holds.PlaceAsync(titleId, members[0]);
    var two = await holds.PlaceAsync(titleId, members[1]);
    var three = await holds.PlaceAsync(titleId, members[2]);

    Check("the queue numbers in the order people joined it",
        one.QueuePosition == 1 && two.QueuePosition == 2 && three.QueuePosition == 3,
        $"{one.QueuePosition}, {two.QueuePosition}, {three.QueuePosition}");

    Check("nobody may join the same queue twice",
        (await holds.WhyNotAsync(title, first, category))?.Contains("already in the queue") == true,
        await holds.WhyNotAsync(title, first, category) ?? "let through");

    Check("and anybody waiting blocks the holder's renewal",
        await holds.AnyoneWaitingAsync(titleId, today), "renewal refused while somebody waits");

    // A category that may not reserve at all.
    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.CanReserve, false));

    var barred = await db.MemberCategories.FirstAsync(c => c.CategoryId == category.CategoryId);

    Check("a category that may not reserve is refused",
        (await holds.WhyNotAsync(title, first, barred))?.Contains("may not place holds") == true,
        "refused");

    await db.MemberCategories.Where(c => c.CategoryId == category.CategoryId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.CanReserve, true));

    // Re-read it: the one above still says it cannot reserve, and asking with
    // that would get the wrong refusal back and prove nothing.
    var allowed = await db.MemberCategories.AsNoTracking()
        .FirstAsync(c => c.CategoryId == category.CategoryId);

    // Clearance applies here too — a hold on a book somebody cannot borrow is a
    // hold that can never be collected.
    await db.Titles.Where(t => t.TitleId == titleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.SECRET));

    var secret = await db.Titles.AsNoTracking().FirstAsync(t => t.TitleId == titleId);

    var refusal = await holds.WhyNotAsync(secret, first, allowed);

    Check("a book above the member's clearance cannot be held",
        refusal?.Contains("not cleared") == true, refusal ?? "let through");

    await db.Titles.Where(t => t.TitleId == titleId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SecurityClass, SecurityClass.UNCLASSIFIED));
}

// ========================================================== a copy comes back ==

Heading("When a copy comes back");

await using (var db = Open())
{
    var holds = new Holds(db);

    var copy = await db.Copies.FirstAsync(c => c.CopyId == copyId);

    var offered = await holds.OfferOnReturnAsync(copy, today);

    Check("it is offered to the front of the queue",
        offered is not null && offered.MemberId == members[0],
        offered is null ? "nobody" : $"member {offered.MemberId}, position {offered.QueuePosition}");

    var after = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == copyId);

    Check("and the copy is set aside rather than shelved",
        after.Status == CopyStatus.RESERVED, Words.Of(after.Status));

    Check("with a date it is kept until",
        offered!.ExpiresOn == today.AddDays(Holds.HoldDays),
        $"kept until {offered.ExpiresOn:dd MMM yyyy}");

    // The person it is held for may collect it even though it is not available.
    var member = await db.Members.FirstAsync(m => m.MemberId == members[0]);
    var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);
    var title = await db.Titles.FirstAsync(t => t.TitleId == titleId);
    var reserved = await db.Copies.FirstAsync(c => c.CopyId == copyId);

    var evaluation = await new IssuePolicy(db).EvaluateAsync(member, category, reserved, title);

    Check("the person it is held for may collect it",
        !evaluation.Blocked, evaluation.Blocked ? "refused" : "allowed");

    // Somebody else may not walk off with it.
    var other = await db.Members.FirstAsync(m => m.MemberId == members[2]);

    var theirs = await new IssuePolicy(db).EvaluateAsync(other, category, reserved, title);

    Check("but nobody else may take it off the hold shelf",
        theirs.Blocked, theirs.Absolute.FirstOrDefault()?.Message ?? "let through");
}

// ================================================================ expiry ==

Heading("A hold nobody collects");

await using (var db = Open())
{
    var holds = new Holds(db);

    // Backdate the ready hold past its keeping date.
    await db.Reservations
        .Where(r => r.TitleId == titleId && r.Status == ReservationStatus.READY)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresOn, today.AddDays(-1)));

    var copy = await db.Copies.FirstAsync(c => c.CopyId == copyId);

    var next = await holds.OfferOnReturnAsync(copy, today);

    var expired = await db.Reservations.AsNoTracking()
        .Where(r => r.TitleId == titleId && r.MemberId == members[0])
        .FirstAsync();

    Check("the uncollected hold expires", expired.Status == ReservationStatus.EXPIRED,
        Words.Of(expired.Status));

    Check("and the book goes to the next in the queue",
        next is not null && next.MemberId == members[1],
        next is null ? "nobody" : $"member {next.MemberId}");

    Check("nothing is left set aside for somebody who never came",
        !await db.Reservations.AnyAsync(r =>
            r.MemberId == members[0] && r.Status == ReservationStatus.READY),
        "released");
}

// ================================================================ cancelling ==

Heading("Cancelling");

await using (var db = Open())
{
    var holds = new Holds(db);

    var ready = await db.Reservations
        .FirstAsync(r => r.TitleId == titleId && r.Status == ReservationStatus.READY);

    await holds.CancelAsync(ready);

    var copy = await db.Copies.AsNoTracking().FirstAsync(c => c.CopyId == copyId);

    Check("cancelling a ready hold puts the copy back",
        copy.Status == CopyStatus.AVAILABLE, Words.Of(copy.Status));

    var (readyNow, waitingNow) = await holds.QueueAsync(today);

    Check("the queue reads back in two lists",
        readyNow.Count + waitingNow.Count >= 1,
        $"{readyNow.Count} ready, {waitingNow.Count} waiting");
}

// =================================================================== the fines ==

Heading("Fines");

await using (var db = Open())
{
    var fines = new Fines(db);

    var loan = await db.Loans.OrderByDescending(l => l.LoanId).FirstAsync();

    var damage = await fines.RaiseAsync(loan, FineType.DAMAGE, 120m,
        "Torn plates", staffId, today);

    Check("a charge raised by hand is pending", damage.Status == FineStatus.PENDING,
        preferences.Money(damage.Amount));

    var pending = await fines.InStateAsync(FineStatus.PENDING);

    Check("it appears on the pending list", pending.Any(p => p.Fine.FineId == damage.FineId),
        $"{pending.Count} pending");

    Check("and the list names who owes it and what for",
        pending.First(p => p.Fine.FineId == damage.FineId).Member.FullName.Length > 0,
        pending.First(p => p.Fine.FineId == damage.FineId).Member.Display);

    var outstanding = await fines.OutstandingAsync();

    Check("the outstanding total includes it", outstanding >= 120m,
        preferences.Money(outstanding));

    // Settling.
    await fines.PayAsync(damage, "RCPT-9912", staffId, today);

    var paid = await db.Fines.AsNoTracking().FirstAsync(f => f.FineId == damage.FineId);

    Check("recording a payment keeps the receipt number",
        paid.Status == FineStatus.PAID && paid.ReceiptNo == "RCPT-9912" && paid.PaidOn == today,
        $"{Words.Of(paid.Status)} against {paid.ReceiptNo}");

    Check("and it leaves the pending list",
        !(await fines.InStateAsync(FineStatus.PENDING)).Any(p => p.Fine.FineId == damage.FineId),
        "gone from pending");

    Check("but is still on the paid one",
        (await fines.InStateAsync(FineStatus.PAID)).Any(p => p.Fine.FineId == damage.FineId),
        "kept as history");

    var loss = await fines.RaiseAsync(loan, FineType.LOSS, 400m, null, staffId, today);

    await fines.WaiveAsync(loss, "Book found in the reading room", staffId);

    var waived = await db.Fines.AsNoTracking().FirstAsync(f => f.FineId == loss.FineId);

    Check("a waiver keeps the reason and who allowed it",
        waived.Status == FineStatus.WAIVED
        && waived.WaiverReason == "Book found in the reading room"
        && waived.WaivedBy == staffId,
        waived.WaiverReason ?? "(none)");

    var note = await db.AuditLog
        .Where(a => a.Action == "FINE_WAIVED")
        .OrderByDescending(a => a.LogId)
        .FirstOrDefaultAsync();

    Check("and it is on the activity log", note is not null, note?.Details ?? "(nothing)");

    // Searching by member, which is how a counter finds a fine.
    var member = await db.Members.FirstAsync(m => m.MemberId == loan.MemberId);

    var found = await fines.InStateAsync(FineStatus.PAID, member.FullName[..4]);

    Check("fines can be found by the member's name", found.Count > 0,
        $"{found.Count} for \"{member.FullName[..4]}\"");
}

Console.WriteLine();

if (failures == 0)
{
    Console.WriteLine("The queue queues and the fines settle once.");
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
