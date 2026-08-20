using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// What has been done in this library, newest first.
///
/// One list for both programs: an issue made at the web screen sits beside one
/// made at this counter, because it is one library and one history. Nothing on
/// this screen writes, edits or deletes — a record of what happened is worth
/// having only if it cannot be tidied up afterwards.
///
/// It is read a page at a time. A library that has been running a few years has
/// tens of thousands of entries, and a screen that tried to show them all would
/// take a noticeable pause to show the same twenty rows anybody actually wanted.
/// </summary>
public partial class ActivityViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ActivityKind _kind = ActivityKind.Everything;
    [ObservableProperty] private WhoRow _who = WhoRow.Anybody;
    [ObservableProperty] private DateTime? _since;
    [ObservableProperty] private DateTime? _until;
    [ObservableProperty] private int _page;
    [ObservableProperty] private int _total;

    public ActivityViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<HappeningRow> Entries { get; } = [];

    /// <summary>Everybody who has ever done anything, with "anybody" at the top.</summary>
    public ObservableCollection<WhoRow> People { get; } = [WhoRow.Anybody];

    public ActivityKind[] Kinds { get; } = Enum.GetValues<ActivityKind>();

    public bool HasProblem => Problem.Length > 0;

    public bool Nothing => !Busy && Entries.Count == 0;

    public bool MayGoBack => Page > 0;

    /// <summary>
    /// Whether there is more. Known exactly, because the count beside the table
    /// is now of the same filtered set — so the button is grey when there is
    /// genuinely nothing older rather than whenever a page came back short.
    /// </summary>
    public bool MayGoOn => (Page * Activity.PageSize) + Entries.Count < Total;

    public string Tally
    {
        get
        {
            if (Busy)
            {
                return "Reading the history…";
            }

            if (Entries.Count == 0)
            {
                return "Nothing matches";
            }

            var first = Page * Activity.PageSize + 1;
            var last = first + Entries.Count - 1;

            // No range when the whole of it fits on one page — "1–11 of 11" is
            // a longer way of saying eleven.
            return last >= Total && Page == 0
                ? $"{Total:N0} {(Total == 1 ? "entry" : "entries")}"
                : $"{first:N0}–{last:N0} of {Total:N0} entries";
        }
    }

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSearchChanged(string value) => _ = ReloadAsync();

    partial void OnKindChanged(ActivityKind value) => _ = ReloadAsync();

    partial void OnWhoChanged(WhoRow value) => _ = ReloadAsync();

    partial void OnSinceChanged(DateTime? value) => _ = ReloadAsync();

    partial void OnUntilChanged(DateTime? value) => _ = ReloadAsync();

    /// <summary>
    /// Any change to the filters starts again at the first page.
    ///
    /// Without this, narrowing the list while on page four shows an empty
    /// screen and looks like the filter found nothing.
    /// </summary>
    private async Task ReloadAsync()
    {
        Page = 0;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var activity = new Activity(db);

            if (People.Count == 1)
            {
                foreach (var (id, name) in await activity.WhoHasActedAsync())
                {
                    People.Add(new WhoRow(id, name));
                }
            }

            var since = Since is { } from ? DateOnly.FromDateTime(from) : (DateOnly?)null;
            var until = Until is { } to ? DateOnly.FromDateTime(to) : (DateOnly?)null;

            // Counted with the same filters the page is read with, so the
            // figure above the table is about what is in the table.
            Total = await activity.CountAsync(Search, Kind, Who.Id, since, until);

            var entries = await activity.ReadAsync(Search, Kind, Who.Id, since, until, Page);

            Entries.Clear();

            var previous = "";

            foreach (var entry in entries)
            {
                // The date is written once, on the first entry of each day,
                // rather than repeated down a column of two hundred identical
                // dates. It makes the day boundaries the thing the eye finds.
                Entries.Add(new HappeningRow(entry, entry.Day != previous));

                previous = entry.Day;
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the activity", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Tally));
            OnPropertyChanged(nameof(Nothing));
            OnPropertyChanged(nameof(MayGoBack));
            OnPropertyChanged(nameof(MayGoOn));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task NewerAsync()
    {
        if (Page == 0)
        {
            return;
        }

        Page--;

        await LoadAsync();
    }

    [RelayCommand]
    private async Task OlderAsync()
    {
        if (!MayGoOn)
        {
            return;
        }

        Page++;

        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        Search = "";
        Kind = ActivityKind.Everything;
        Who = WhoRow.Anybody;
        Since = null;
        Until = null;

        await ReloadAsync();
    }
}

/// <summary>One entry, as the list shows it.</summary>
public record HappeningRow(Happening Entry, bool StartsADay)
{
    public string Day => Entry.Day;

    public string Time => Entry.Time;

    public string Who => Entry.Who;

    public string Said => Entry.Said;

    public bool Notable => Entry.Notable;
}

/// <summary>
/// One name in the "by whom" list. "Anybody" is a row rather than a null so the
/// dropdown never shows an empty first entry.
/// </summary>
public record WhoRow(long? Id, string Name)
{
    public static WhoRow Anybody { get; } = new(null, "Anybody");

    public override string ToString() => Name;
}
