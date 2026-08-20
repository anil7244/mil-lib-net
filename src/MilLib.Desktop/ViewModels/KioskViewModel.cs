using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The reading-room terminal.
///
/// A member standing in front of this has had no training, is not signed in to
/// anything, and cannot be helped by whoever set it up. So it does exactly two
/// things — find a book, and show you your own loans — and it says what it is
/// doing at every point in words a person would use.
///
/// It is also the only screen in the application a stranger can reach. That
/// shapes everything about it:
///
/// Nothing classified appears until somebody scans their own pass, and then
/// only up to their own clearance. There is no box to type a clearance into.
///
/// Nothing about anybody else appears at all.
///
/// It forgets whoever scanned in after a few minutes, because a member who
/// walks away must not leave their loans on a screen in a public room.
///
/// And leaving it needs a member of staff's password, or it is not a kiosk —
/// it is a full library system with a search box in front of it.
/// </summary>
public partial class KioskViewModel : ViewModelBase
{
    /// <summary>
    /// How long somebody stays signed in with nothing happening.
    ///
    /// Short, because the failure it guards against is somebody wandering off
    /// mid-search and the next person seeing their fines. Two minutes is long
    /// enough to read a screen and short enough that nobody gets far.
    /// </summary>
    public static readonly TimeSpan Forgets = TimeSpan.FromMinutes(2);

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _scan = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private OnTheShelf? _chosen;
    [ObservableProperty] private bool _asking;
    [ObservableProperty] private string _leaveWord = "";
    [ObservableProperty] private string _leaveProblem = "";

    private WhoIsThere? _who;
    private DateTime _lastTouched = DateTime.Now;

    public KioskViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<OnTheShelf> Found { get; } = [];

    public ObservableCollection<MyBook> MyBooks { get; } = [];

    public ObservableCollection<string> MyHolds { get; } = [];

    public string LibraryName => Session.Preferences.LibraryName;

    public string Organisation => Session.Preferences.OrganisationName;

    public bool HasSaid => Said.Length > 0;

    public bool SignedIn => _who is not null;

    public string Who => _who?.Name ?? "";

    public bool AnythingLate => _who?.AnythingLate ?? false;

    public bool HasBooks => MyBooks.Count > 0;

    public bool HasHolds => MyHolds.Count > 0;

    public bool Owes => (_who?.Owed ?? 0) > 0;

    public string Owed => Session.Preferences.Money(_who?.Owed ?? 0);

    public bool Nothing => !Busy && Found.Count == 0 && Search.Trim().Length > 0;

    /// <summary>
    /// What the person is allowed to see, said plainly. It appears only once
    /// somebody has scanned in, because until then the answer is "the ordinary
    /// catalogue" and saying so would only raise the question.
    /// </summary>
    public string ClearedTo => _who is null
        ? ""
        : _who.Cleared == SecurityClass.UNCLASSIFIED
            ? "You are seeing the ordinary catalogue."
            : $"You are cleared to {Words.Of(_who.Cleared)}, so restricted titles are included.";

    public string Allowance => _who is null
        ? ""
        : _who.MayStillTake == 0
            ? $"You have {_who.Category.MaxBooks} books out, which is your limit."
            : $"You may take {_who.MayStillTake} more book{(_who.MayStillTake == 1 ? "" : "s")}.";

    /// <summary>Whether the chosen book can be put on hold by whoever is signed in.</summary>
    [ObservableProperty] private string _holdRefusal = "";

    public bool MayHold => SignedIn && Chosen is not null && HoldRefusal.Length == 0
        && Session.Has(Feature.Reservations);

    public bool RefusedHold => SignedIn && Chosen is not null && HoldRefusal.Length > 0;

    /// <summary>Raised when the kiosk should close and give the machine back.</summary>
    public event Action? Leaving;

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnSearchChanged(string value)
    {
        Touch();

        _ = LoadAsync();
    }

    partial void OnChosenChanged(OnTheShelf? value)
    {
        Touch();

        _ = AskAboutHoldAsync();
    }

    partial void OnHoldRefusalChanged(string value)
    {
        OnPropertyChanged(nameof(MayHold));
        OnPropertyChanged(nameof(RefusedHold));
    }

    /// <summary>Somebody is using it. Restarts the clock that forgets them.</summary>
    public void Touch() => _lastTouched = DateTime.Now;

    /// <summary>
    /// Called on a timer by the window. Forgets whoever scanned in once they
    /// have stopped touching it — the one thing this screen must do on its own.
    /// </summary>
    public void ForgetIfIdle()
    {
        if (_who is not null && DateTime.Now - _lastTouched > Forgets)
        {
            Clear("The screen was cleared because nobody was using it.");
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var found = await new ReadingRoom(db).SearchAsync(
                Search, _who?.Cleared ?? SecurityClass.UNCLASSIFIED);

            Found.Clear();

            foreach (var book in found)
            {
                Found.Add(book);
            }
        }
        catch (Exception ex)
        {
            Faults.Record("searching from the reading-room terminal", ex);

            // Said in the words of somebody who cannot fix it and should not be
            // shown a stack trace.
            Said = "The catalogue could not be searched just now. Please ask at the counter.";
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Nothing));
        }
    }

    /// <summary>
    /// Identify yourself by scanning your pass, or by typing the number on it.
    ///
    /// A scanner types the token and presses Enter, which is why this is bound
    /// to Enter on the box and not to a button somebody has to find.
    /// </summary>
    [RelayCommand]
    private async Task IdentifyAsync()
    {
        Touch();

        var scanned = Scan.Trim();

        Scan = "";

        if (scanned.Length == 0)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var who = await new ReadingRoom(db)
                .WhoAsync(scanned, DateOnly.FromDateTime(DateTime.Now));

            if (who is null)
            {
                // Deliberately the same answer whether the pass is unknown or
                // the membership has lapsed. A public terminal should not tell
                // a stranger which membership numbers exist.
                Said = "That pass was not recognised. Please ask at the counter.";
                SaidIsGood = false;

                return;
            }

            _who = who;

            MyBooks.Clear();

            foreach (var book in who.Out)
            {
                MyBooks.Add(book);
            }

            MyHolds.Clear();

            foreach (var hold in who.Held)
            {
                MyHolds.Add(hold);
            }

            Said = $"Hello, {who.Name}.";
            SaidIsGood = true;

            Announce();

            // Searched again, because a clearance may now let more through.
            await LoadAsync();

            await AskAboutHoldAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("identifying somebody at the reading-room terminal", ex);

            Said = "That could not be checked just now. Please ask at the counter.";
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Done() => Clear("");

    private void Clear(string said)
    {
        _who = null;

        MyBooks.Clear();
        MyHolds.Clear();

        Chosen = null;
        HoldRefusal = "";
        Said = said;
        SaidIsGood = true;

        Announce();

        // Back to the ordinary catalogue, which means searching again: what is
        // on screen was found at somebody's clearance and must not stay there.
        _ = LoadAsync();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(SignedIn));
        OnPropertyChanged(nameof(Who));
        OnPropertyChanged(nameof(ClearedTo));
        OnPropertyChanged(nameof(Allowance));
        OnPropertyChanged(nameof(AnythingLate));
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(HasHolds));
        OnPropertyChanged(nameof(Owes));
        OnPropertyChanged(nameof(Owed));
        OnPropertyChanged(nameof(MayHold));
        OnPropertyChanged(nameof(RefusedHold));
    }

    private async Task AskAboutHoldAsync()
    {
        HoldRefusal = "";

        if (_who is null || Chosen is null || !Session.Has(Feature.Reservations))
        {
            OnPropertyChanged(nameof(MayHold));

            return;
        }

        try
        {
            await using var db = Workspace.Open();

            HoldRefusal = await new ReadingRoom(db).WhyNotHoldAsync(
                Chosen.TitleId, _who, DateOnly.FromDateTime(DateTime.Now)) ?? "";
        }
        catch (Exception ex)
        {
            Faults.Record("checking whether a hold can be placed", ex);

            HoldRefusal = "This could not be checked. Please ask at the counter.";
        }
    }

    [RelayCommand]
    private async Task HoldAsync()
    {
        Touch();

        if (_who is null || Chosen is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var room = new ReadingRoom(db);

            Said = await room.HoldAsync(Chosen.TitleId, _who, DateOnly.FromDateTime(DateTime.Now));
            SaidIsGood = true;

            // Read back, so the list of holds on screen is what was just done
            // rather than what was true when they scanned in.
            var again = await room.WhoAsync(_who.Member.QrToken, DateOnly.FromDateTime(DateTime.Now));

            if (again is not null)
            {
                _who = again;

                MyHolds.Clear();

                foreach (var hold in again.Held)
                {
                    MyHolds.Add(hold);
                }

                Announce();
            }

            await AskAboutHoldAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("placing a hold from the reading-room terminal", ex);

            Said = "That could not be done just now. Please ask at the counter.";
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    // ------------------------------------------------------------ leaving --

    /// <summary>
    /// Closing the kiosk asks for a staff password.
    ///
    /// Without it this is not a kiosk: anybody could close the window and find
    /// the whole library system behind it. The password is checked against the
    /// staff accounts, so no separate secret has to be kept anywhere.
    /// </summary>
    [RelayCommand]
    private void AskToLeave()
    {
        Touch();

        Asking = true;
        LeaveWord = "";
        LeaveProblem = "";
    }

    [RelayCommand]
    private void StayHere()
    {
        Asking = false;
        LeaveWord = "";
        LeaveProblem = "";
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        LeaveProblem = "";

        try
        {
            await using var db = Workspace.Open();

            var result = await new SignIn(db)
                .AttemptAsync(Session.Name.Length > 0 ? Session.User!.Username : "", LeaveWord,
                    Session.Machine);

            if (!result.Ok)
            {
                LeaveProblem = "That is not the password this terminal was opened with.";

                return;
            }

            Leaving?.Invoke();
        }
        catch (Exception ex)
        {
            Faults.Record("closing the reading-room terminal", ex);

            LeaveProblem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
            LeaveWord = "";
        }
    }
}
