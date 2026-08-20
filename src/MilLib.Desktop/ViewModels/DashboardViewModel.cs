using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The operating picture for the day.
///
/// Six figures and the overdue list, because that is what somebody opening the
/// application at nine in the morning actually wants to know: what the library
/// holds, what is out, and what should have come back and hasn't.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly Action<string> _go;

    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";

    [ObservableProperty] private int _titles;
    [ObservableProperty] private int _copies;
    [ObservableProperty] private int _issued;
    [ObservableProperty] private int _overdue;
    [ObservableProperty] private int _members;
    [ObservableProperty] private int _issuedToday;
    [ObservableProperty] private int _dueToday;
    [ObservableProperty] private int _holdsReady;
    [ObservableProperty] private int _holdsWaiting;
    [ObservableProperty] private decimal _pendingFines;

    [ObservableProperty] private string _greeting = "";

    // ------------------------------------------------------------- the clock
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _clockDay = "";
    [ObservableProperty] private string _clockDate = "";
    [ObservableProperty] private string _monthTitle = "";

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Who is signed in and to what — the same line the web console carries.</summary>
    [ObservableProperty] private string _standing = "";

    public DashboardViewModel(Action<string>? go = null)
    {
        _go = go ?? (_ => { });

        Greeting = Welcome();
        Standing = WhoAndWhen();

        BuildCalendar();
        Tock();

        // A second hand. The dashboard is the screen left open on a counter all
        // day, so a clock on it that does not move is a clock nobody trusts.
        _tick.Tick += (_, _) => Tock();
        _tick.Start();

        _ = LoadAsync();
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>
    /// The figures, as cards. A list rather than a fixed grid because which
    /// figures appear depends on what this person may see and what the unit has
    /// turned on — a counter clerk with no reservations feature gets a shorter
    /// row, not a row with holes in it.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<StatCard> Stats { get; } = [];

    public List<OverdueRow> Overdues { get; } = [];

    /// <summary>The month, as a grid of days with today marked.</summary>
    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>The weekday headings over the calendar, starting Monday.</summary>
    public string[] Weekdays { get; } = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    public bool NothingOverdue => !Busy && Overdues.Count == 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand]
    private void Go(string section) => _go(section);

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var today = DateOnly.FromDateTime(DateTime.Today);

            Titles = await db.Titles.CountAsync();
            Copies = await db.Copies.CountAsync();
            Members = await db.Members.CountAsync(m => m.Status == MemberStatus.ACTIVE);

            // Read off the loans rather than off copy.status. The two agree
            // almost always, and when they don't it is the loan that is right:
            // a copy marked available with an open loan against it is a book
            // somebody is holding.
            var open = db.Loans.Where(l => l.Status != LoanStatus.RETURNED);

            Issued = await open.CountAsync();
            Overdue = await open.CountAsync(l => l.DueOn < today);

            IssuedToday = await db.Loans.CountAsync(l => l.IssuedOn >= DateTime.Today);
            DueToday = await open.CountAsync(l => l.DueOn == today);

            if (Session.Has(Feature.Reservations))
            {
                HoldsReady = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.READY);
                HoldsWaiting = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.WAITING);
            }

            if (Session.Has(Feature.Fines))
            {
                PendingFines = await db.Fines
                    .Where(f => f.Status == FineStatus.PENDING)
                    .SumAsync(f => (decimal?)f.Amount) ?? 0m;
            }

            BuildCards();

            Overdues.Clear();

            var rows = await open
                .Where(l => l.DueOn < today)
                .OrderBy(l => l.DueOn)
                .Take(12)
                .Select(l => new
                {
                    l.DueOn,
                    Member = l.Member!.FullName,
                    l.Member!.Rank,
                    Book = l.Copy!.Title!.Name,
                    l.Copy!.AccessionNo,
                })
                .ToListAsync();

            foreach (var row in rows)
            {
                Overdues.Add(new OverdueRow(
                    string.IsNullOrWhiteSpace(row.Rank) ? row.Member : $"{row.Rank} {row.Member}",
                    row.Book,
                    Session.Preferences.Accession(row.AccessionNo),
                    row.DueOn,
                    today.DayNumber - row.DueOn.DayNumber));
            }

            OnPropertyChanged(nameof(Overdues));
        }
        catch (Exception ex)
        {
            Faults.Record("reading the dashboard", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(NothingOverdue));
        }
    }

    /// <summary>
    /// The figures worth showing this person, in the order a librarian asks for
    /// them, each pointing at the screen where something can be done about it.
    /// </summary>
    private void BuildCards()
    {
        Stats.Clear();

        var money = Session.Preferences;

        if (Session.Can(Ability.CatalogueView))
        {
            Add("BOOKS IN LIBRARY", $"{Titles:N0}", "separate works in the catalogue", "cool", "Books in Library");
            Add("COPIES ON THE REGISTER", $"{Copies:N0}", "physical books, each with its own number", "cool", "Books in Library");
        }

        if (Session.Can(Ability.MembersView))
        {
            Add("MEMBERS", $"{Members:N0}", "currently enrolled and active", "info", "Members");
        }

        Add("OUT ON LOAN", $"{Issued:N0}", "books with somebody at the moment", "good", "");
        Add("ISSUED TODAY", $"{IssuedToday:N0}", "handed over the counter since midnight", "good", "");

        if (Session.Can(Ability.CirculationOperate))
        {
            Add("DUE BACK TODAY", $"{DueToday:N0}", "should come back over the counter today", "info", "Issue & Return");
        }

        if (Session.Has(Feature.Reservations) && Session.Can(Ability.ReservationsManage))
        {
            Add("HOLDS READY", $"{HoldsReady:N0}",
                HoldsWaiting == 1 ? "1 more waiting in queue" : $"{HoldsWaiting} more waiting in queue",
                HoldsReady > 0 ? "warn" : "cool", "Reservations");
        }

        if (Session.Has(Feature.Fines) && Session.Can(Ability.FinesManage))
        {
            Add("UNPAID FINES", money.Money(PendingFines), "settled at the counter",
                PendingFines > 0 ? "bad" : "cool", "Fines");
        }

        Add("OVERDUE", $"{Overdue:N0}", "past their due date and still out",
            Overdue > 0 ? "bad" : "cool", "");
    }

    private void Add(string label, string value, string note, string tone, string target) =>
        Stats.Add(new StatCard(label, value, note, tone, target, _go));

    /// <summary>Role, clearance and the date — the console's context line.</summary>
    private static string WhoAndWhen()
    {
        var role = Session.User is { } u ? Words.Any(u.Role) : "";
        var cleared = Words.Of(Session.User?.ClearanceLevel ?? SecurityClass.UNCLASSIFIED);

        return $"{role}  ·  cleared to {cleared}  ·  {DateTime.Now:dddd, dd MMMM yyyy}";
    }

    /// <summary>Set the clock to now — called every second, and once up front.</summary>
    private void Tock()
    {
        var now = DateTime.Now;

        Clock = now.ToString("HH:mm:ss");
        ClockDay = now.ToString("dddd");
        ClockDate = now.ToString("dd MMMM yyyy");
    }

    /// <summary>
    /// The current month as six weeks of days, Monday first, with the days that
    /// belong to the months either side dimmed and today marked. Built once —
    /// a calendar does not need a second hand.
    /// </summary>
    private void BuildCalendar()
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);

        MonthTitle = first.ToString("MMMM yyyy");

        // Monday is column zero; DayOfWeek has Sunday as zero, so it is shifted.
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-lead);

        Days.Clear();

        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);

            Days.Add(new CalendarDay(
                day.Day,
                day.Date == today,
                day.Month == today.Month));
        }
    }

    /// <summary>
    /// The time of day, said once. Not a personality — a small
    /// acknowledgement that a person opened this, at an hour, to do a job.
    /// </summary>
    private static string Welcome()
    {
        var hour = DateTime.Now.Hour;

        var part = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";

        return $"{part}, {Session.Name}";
    }
}

/// <summary>
/// One figure on the dashboard.
///
/// It carries where it goes as well as what it says, so the card is the button
/// — a number somebody reads and then walks into, rather than a number beside a
/// button that does the walking. A card with nowhere to go (a running total
/// that is not itself a screen) simply is not clickable.
/// </summary>
public partial class StatCard : ObservableObject
{
    private readonly Action<string> _go;

    public StatCard(string label, string value, string note, string tone, string target, Action<string> go)
    {
        Label = label;
        Value = value;
        Note = note;
        Tone = tone;
        Target = target;
        _go = go;
    }

    public string Label { get; }

    public string Value { get; }

    public string Note { get; }

    /// <summary>The card class — cool / info / good / warn / bad — for its tint.</summary>
    public string Tone { get; }

    public bool IsCool => Tone == "cool";

    public bool IsInfo => Tone == "info";

    public bool IsGood => Tone == "good";

    public bool IsWarn => Tone == "warn";

    public bool IsBad => Tone == "bad";

    public string Target { get; }

    public bool CanOpen => Target.Length > 0;

    [RelayCommand]
    private void Open()
    {
        if (CanOpen)
        {
            _go(Target);
        }
    }
}

/// <summary>One line of the overdue list.</summary>
public record OverdueRow(string Member, string Book, string Accession, DateOnly Due, int Days)
{
    public string DueText => Due.ToString("dd MMM yyyy");

    public string DaysText => Days == 1 ? "1 day" : $"{Days} days";
}

/// <summary>One square on the mini calendar.</summary>
public record CalendarDay(int Day, bool IsToday, bool InMonth)
{
    public string Text => Day.ToString();
}
