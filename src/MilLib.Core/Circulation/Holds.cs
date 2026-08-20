using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>One hold, with the book and the person it is for.</summary>
public record HeldFor(Reservation Reservation, Title Title, Member Member, int? DaysLeft)
{
    public bool IsReady => Reservation.Status == ReservationStatus.READY;

    /// <summary>
    /// A hold nobody has collected is about to go to the next person in the
    /// queue, and that is the one the counter needs to act on today.
    /// </summary>
    public bool ExpiringToday => IsReady && DaysLeft is <= 0;
}

/// <summary>
/// The waiting list, kept against the title rather than any one copy — a member
/// wants the book, not a particular object on the shelf. The first copy back
/// goes to the front of the queue.
///
/// Holds that nobody collected expire lazily, whenever the queue is next looked
/// at, for the same reason fines are worked out on demand: there is nothing
/// running in the background on this machine, and there should not be.
/// </summary>
public class Holds(MilLibDbContext db)
{
    /// <summary>Days a ready hold is kept before the next person is offered it.</summary>
    public const int HoldDays = 3;

    /// <summary>
    /// The queue, in the two states that matter at a counter: put aside and
    /// waiting to be collected, or still waiting for a copy to come back.
    /// </summary>
    public async Task<(IReadOnlyList<HeldFor> Ready, IReadOnlyList<HeldFor> Waiting)> QueueAsync(DateOnly today)
    {
        var live = await db.Reservations
            .Where(r => r.Status == ReservationStatus.READY || r.Status == ReservationStatus.WAITING)
            .OrderBy(r => r.TitleId)
            .ThenBy(r => r.QueuePosition)
            .Join(db.Titles, r => r.TitleId, t => t.TitleId, (r, t) => new { r, t })
            .Join(db.Members, x => x.r.MemberId, m => m.MemberId, (x, m) => new { x.r, x.t, m })
            .ToListAsync();

        var held = live
            .Select(x => new HeldFor(x.r, x.t, x.m,
                x.r.ExpiresOn is null ? null : x.r.ExpiresOn.Value.DayNumber - today.DayNumber))
            .ToList();

        return
        (
            [.. held.Where(h => h.Reservation.Status == ReservationStatus.READY)
                .OrderBy(h => h.Reservation.ReadyOn)],
            [.. held.Where(h => h.Reservation.Status == ReservationStatus.WAITING)]
        );
    }

    /// <summary>Whether this member is already in the queue for this title.</summary>
    public async Task<bool> AlreadyWaitingAsync(long titleId, long memberId) =>
        await db.Reservations.AnyAsync(r =>
            r.TitleId == titleId
            && r.MemberId == memberId
            && (r.Status == ReservationStatus.WAITING || r.Status == ReservationStatus.READY));

    /// <summary>
    /// Why this hold cannot be placed, or null.
    ///
    /// The category rule is the one worth stating: some kinds of member may not
    /// reserve at all, and finding that out after typing everything in is worse
    /// than being told at the start.
    /// </summary>
    public async Task<string?> WhyNotAsync(Title title, Member member, MemberCategory category)
    {
        if (!category.CanReserve)
        {
            return $"{category.Name} members may not place holds.";
        }

        if (member.Status != MemberStatus.ACTIVE)
        {
            return $"{member.Display} is {Words.Of(member.Status).ToLowerInvariant()} and may not place holds.";
        }

        if (!member.CanAccess(category, title.SecurityClass))
        {
            return $"That book is {Words.Of(title.SecurityClass)} and {member.Display} is not cleared for it.";
        }

        if (await AlreadyWaitingAsync(title.TitleId, member.MemberId))
        {
            return $"{member.Display} is already in the queue for this book.";
        }

        return null;
    }

    public async Task<Reservation> PlaceAsync(long titleId, long memberId)
    {
        var last = await db.Reservations
            .Where(r => r.TitleId == titleId
                     && (r.Status == ReservationStatus.WAITING || r.Status == ReservationStatus.READY))
            .MaxAsync(r => (int?)r.QueuePosition) ?? 0;

        var reservation = new Reservation
        {
            TitleId = titleId,
            MemberId = memberId,
            ReservedOn = DateTime.Now,
            QueuePosition = last + 1,
            Status = ReservationStatus.WAITING,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        db.Reservations.Add(reservation);

        await db.SaveAndForgetAsync();

        return reservation;
    }

    /// <summary>
    /// Whether anybody is waiting on this title. This is what stops the current
    /// holder renewing indefinitely while somebody else waits their turn.
    /// </summary>
    public async Task<bool> AnyoneWaitingAsync(long titleId, DateOnly today)
    {
        await ExpireStaleAsync(titleId, today);

        return await db.Reservations.AnyAsync(r =>
            r.TitleId == titleId
            && (r.Status == ReservationStatus.WAITING || r.Status == ReservationStatus.READY));
    }

    /// <summary>
    /// A copy has just come back. Clear out anything stale, then — if the copy
    /// is free and somebody is waiting — put it aside for the front of the queue.
    /// </summary>
    public async Task<Reservation?> OfferOnReturnAsync(Copy copy, DateOnly today)
    {
        await ExpireStaleAsync(copy.TitleId, today);

        // Read the state back, do not trust the copy handed in.
        //
        // Expiring a stale hold releases the copy it was holding — which may be
        // this very copy. The object passed in was read before that happened,
        // so it still says reserved, and the test below then refused to offer
        // the book to anybody. Three people waited while it sat on the shelf,
        // and nothing anywhere said so.
        var status = await db.Copies
            .Where(c => c.CopyId == copy.CopyId)
            .Select(c => c.Status)
            .FirstOrDefaultAsync();

        copy.Status = status;

        if (status != CopyStatus.AVAILABLE)
        {
            return null;
        }

        var next = await db.Reservations
            .Where(r => r.TitleId == copy.TitleId && r.Status == ReservationStatus.WAITING)
            .OrderBy(r => r.QueuePosition)
            .FirstOrDefaultAsync();

        if (next is null)
        {
            return null;
        }

        next.Status = ReservationStatus.READY;
        next.ReadyOn = DateTime.Now;
        next.ExpiresOn = today.AddDays(HoldDays);
        next.FulfilledCopyId = copy.CopyId;
        next.UpdatedAt = DateTime.Now;

        db.Reservations.Update(next);

        copy.Status = CopyStatus.RESERVED;
        db.Copies.Update(copy);

        await db.SaveAndForgetAsync();

        return next;
    }

    /// <summary>The member who was waiting has taken it.</summary>
    public async Task CollectedAsync(long memberId, long titleId)
    {
        await db.Reservations
            .Where(r => r.TitleId == titleId
                     && r.MemberId == memberId
                     && r.Status == ReservationStatus.READY)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, ReservationStatus.COLLECTED));
    }

    public async Task CancelAsync(Reservation reservation)
    {
        var wasReady = reservation.Status == ReservationStatus.READY;
        var held = reservation.FulfilledCopyId;

        reservation.Status = ReservationStatus.CANCELLED;
        reservation.UpdatedAt = DateTime.Now;

        db.Reservations.Update(reservation);

        await db.SaveAndForgetAsync();

        if (wasReady && held is not null)
        {
            await ReleaseAsync(held.Value);
        }
    }

    private async Task ExpireStaleAsync(long titleId, DateOnly today)
    {
        var stale = await db.Reservations
            .Where(r => r.TitleId == titleId
                     && r.Status == ReservationStatus.READY
                     && r.ExpiresOn != null
                     && r.ExpiresOn < today)
            .ToListAsync();

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var reservation in stale)
        {
            reservation.Status = ReservationStatus.EXPIRED;
            reservation.UpdatedAt = DateTime.Now;

            db.Reservations.Update(reservation);
        }

        await db.SaveAndForgetAsync();

        foreach (var reservation in stale.Where(r => r.FulfilledCopyId is not null))
        {
            await ReleaseAsync(reservation.FulfilledCopyId!.Value);
        }
    }

    /// <summary>
    /// Back on the shelf. Only a copy still being held is released — one that
    /// has since been issued to somebody must not be quietly marked available
    /// while they are holding it.
    /// </summary>
    private async Task ReleaseAsync(long copyId) =>
        await db.Copies
            .Where(c => c.CopyId == copyId && c.Status == CopyStatus.RESERVED)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, CopyStatus.AVAILABLE));
}
