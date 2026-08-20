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

    [ObservableProperty] private string _greeting = "";

    public DashboardViewModel(Action<string>? go = null)
    {
        _go = go ?? (_ => { });

        Greeting = Welcome();

        _ = LoadAsync();
    }

    public bool HasProblem => Problem.Length > 0;

    public List<OverdueRow> Overdues { get; } = [];

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

/// <summary>One line of the overdue list.</summary>
public record OverdueRow(string Member, string Book, string Accession, DateOnly Due, int Days)
{
    public string DueText => Due.ToString("dd MMM yyyy");

    public string DaysText => Days == 1 ? "1 day" : $"{Days} days";
}
