using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The waiting list.
///
/// Two lists, because they are two different jobs. The ready ones are books
/// already on the hold shelf with somebody's name on them — that list is worked
/// through daily, and the ones about to expire are the ones to chase. The
/// waiting ones are queues on titles that are all out, and nothing can be done
/// about them until a copy comes back.
///
/// A hold is placed against a title, never a copy: the member wants the book,
/// not one particular object.
/// </summary>
public partial class ReservationsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // ------------------------------------------------------------ placing one
    [ObservableProperty] private bool _placing;
    [ObservableProperty] private string _book = "";
    [ObservableProperty] private string _member = "";

    public ReservationsViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<HoldRow> Ready { get; } = [];

    public ObservableCollection<HoldRow> Waiting { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.ReservationsManage);

    public bool NothingReady => !Busy && Ready.Count == 0;

    public bool NothingWaiting => !Busy && Waiting.Count == 0;

    public string ReadyTally => Ready.Count switch
    {
        0 => "Nothing is waiting to be collected.",
        1 => "1 book is on the hold shelf.",
        var n => $"{n:N0} books are on the hold shelf.",
    };

    public string WaitingTally => Waiting.Count switch
    {
        0 => "Nobody is queuing for a book that is out.",
        1 => "1 person is queuing for a book that is out.",
        var n => $"{n:N0} people are queuing for books that are out.",
    };

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var (ready, waiting) = await new Holds(db)
                .QueueAsync(DateOnly.FromDateTime(DateTime.Today));

            Ready.Clear();
            Waiting.Clear();

            foreach (var held in ready)
            {
                Ready.Add(new HoldRow(held));
            }

            foreach (var held in waiting)
            {
                Waiting.Add(new HoldRow(held));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the waiting list", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(NothingReady));
            OnPropertyChanged(nameof(NothingWaiting));
            OnPropertyChanged(nameof(ReadyTally));
            OnPropertyChanged(nameof(WaitingTally));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void Place()
    {
        Placing = true;
        Said = "";
        Book = "";
        Member = "";
    }

    [RelayCommand]
    private void Never() => Placing = false;

    /// <summary>
    /// Put somebody in the queue.
    ///
    /// Both ends are named the way they are named everywhere else — a book by
    /// its accession number or part of its title, a member by their number or
    /// their name — so nobody has to learn a different way of saying it here.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var desk = new Desk(db, Session.Preferences);
            var holds = new Holds(db);

            // The book. A copy's number identifies its title, which is what a
            // hold is actually against.
            var copy = await desk.FindCopyAsync(Book);

            var title = copy?.Title;

            if (title is null)
            {
                var like = $"%{Book.Trim()}%";

                var matches = await db.Titles
                    .Where(t => EF.Functions.Like(t.Name, like))
                    .OrderBy(t => t.Name)
                    .Take(2)
                    .ToListAsync();

                if (matches.Count == 1)
                {
                    title = matches[0];
                }
                else
                {
                    Said = matches.Count == 0
                        ? $"No book matches “{Book}”."
                        : $"More than one book matches “{Book}”. Use its accession number.";

                    SaidIsGood = false;

                    return;
                }
            }

            var who = await desk.ResolveAsync(Member);

            if (who is not Scan.Person person)
            {
                Said = who is Scan.Several several
                    ? $"{several.Matches.Count} people match “{Member}”. Use their membership number."
                    : $"No member matches “{Member}”.";

                SaidIsGood = false;

                return;
            }

            var why = await holds.WhyNotAsync(title, person.Member.Member, person.Member.Category);

            if (why is not null)
            {
                Said = why;
                SaidIsGood = false;

                return;
            }

            var hold = await holds.PlaceAsync(title.TitleId, person.Member.Member.MemberId);

            Placing = false;

            Said = hold.QueuePosition == 1
                ? $"{person.Member.Member.Display} is first in the queue for “{title.Name}”."
                : $"{person.Member.Member.Display} is number {hold.QueuePosition} in the queue "
                  + $"for “{title.Name}”.";

            SaidIsGood = true;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("placing a hold", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Taking somebody out of the queue.
    ///
    /// A ready hold puts its copy back on the shelf as it goes, so a cancelled
    /// hold does not leave a book set aside for nobody.
    /// </summary>
    [RelayCommand]
    private async Task CancelAsync(HoldRow row)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var hold = await db.Reservations
                .FirstOrDefaultAsync(r => r.ReservationId == row.Held.Reservation.ReservationId);

            if (hold is null)
            {
                Said = "That hold has already gone.";
                SaidIsGood = false;

                return;
            }

            await new Holds(db).CancelAsync(hold);

            Said = row.IsReady
                ? $"Cancelled. “{row.Title}” goes back on the shelf, or to the next in the queue."
                : "Cancelled.";

            SaidIsGood = true;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("cancelling a hold", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>One hold, as the counter reads it.</summary>
public record HoldRow(HeldFor Held)
{
    public string Title => Held.Title.Name;

    public string Member => Held.Member.Display;

    public string Number => Held.Member.MembershipNo;

    public bool IsReady => Held.IsReady;

    public string Position => Held.Reservation.QueuePosition == 1
        ? "next"
        : $"number {Held.Reservation.QueuePosition}";

    public string Since => $"since {Held.Reservation.ReservedOn:dd MMM yyyy}";

    /// <summary>
    /// How long the book stays on the hold shelf. Said as days rather than a
    /// date, because "goes back tomorrow" is what the counter acts on.
    /// </summary>
    public string Keeping => Held.DaysLeft switch
    {
        null => "",
        <= 0 => "goes to the next in the queue today",
        1 => "kept until tomorrow",
        var n => $"kept for {n} more days",
    };

    public bool IsUrgent => Held.ExpiringToday;
}
