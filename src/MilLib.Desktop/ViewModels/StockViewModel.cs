using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Counting the shelves.
///
/// A count runs over days: somebody walks the shelves with a scanner, stops for
/// lunch, comes back, finishes on Thursday. So every scan is written down as it
/// is made and the session is picked up again where it was left — there is no
/// "in progress" state held in memory that a power cut can lose.
///
/// The screen is the list of counts on the left and, on the right, either the
/// scanning panel for the one still open or the reconciliation for one that is
/// finished.
/// </summary>
public partial class StockViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private CheckRow? _selected;

    // ------------------------------------------------------------- scanning
    [ObservableProperty] private string _scanned = "";
    [ObservableProperty] private int _expected;
    [ObservableProperty] private int _found;
    [ObservableProperty] private int _strangers;
    [ObservableProperty] private int _twice;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _lastScan = "";
    [ObservableProperty] private bool _lastWasGood = true;

    // ---------------------------------------------------------- the closing
    [ObservableProperty] private string _boardReference = "";
    [ObservableProperty] private bool _closing;

    // ---------------------------------------------------------- the outcome
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private int _missingCount;

    // ------------------------------------------------------------- starting
    [ObservableProperty] private bool _starting;
    [ObservableProperty] private string _newName = "";

    private StockVerification? _open;
    private Reconciliation? _result;

    public StockViewModel()
    {
        NewName = $"Stock Verification {DateTime.Today:yyyy}";

        _ = LoadAsync();
    }

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<ScanRow> Recent { get; } = [];

    public ObservableCollection<MissingRow> Missing { get; } = [];

    public ObservableCollection<string> Strays { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayVerify => Session.Can(Ability.StockVerify);

    public bool HasSelection => Selected is not null;

    /// <summary>Whether the selected count is the one still being worked on.</summary>
    public bool IsOpen => Selected?.Check.Status == VerificationStatus.IN_PROGRESS;

    public bool IsFinished => HasSelection && !IsOpen;

    public bool HasStrays => Strays.Count > 0;

    public bool NothingMissing => IsFinished && Missing.Count == 0;

    public string Tally => $"{Found:N0} of {Expected:N0} found";

    public string Outstanding => (Expected - Found) switch
    {
        <= 0 => "Every copy expected on the shelf has been scanned.",
        1 => "1 still to find.",
        var n => $"{n:N0} still to find.",
    };

    /// <summary>Raised to write the shortage statement out. The view answers it.</summary>
    public event Func<StockVerification, string, Reconciliation, Task>? PrintShortage;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnSelectedChanged(CheckRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsFinished));

        Said = "";
        Closing = false;

        _ = ShowAsync(value);
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var keep = Selected?.Check.VerificationId;

            Checks.Clear();

            foreach (var (check, by) in await new StockCheck(db).AllAsync())
            {
                Checks.Add(new CheckRow(check, by));
            }

            Selected = Checks.FirstOrDefault(c => c.Check.VerificationId == keep)
                ?? Checks.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Faults.Record("reading the stock checks", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ShowAsync(CheckRow? row)
    {
        Recent.Clear();
        Missing.Clear();
        Strays.Clear();

        _open = null;
        _result = null;

        if (row is null)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var stock = new StockCheck(db);

            var check = await db.StockVerifications
                .FirstAsync(v => v.VerificationId == row.Check.VerificationId);

            if (check.Status == VerificationStatus.IN_PROGRESS)
            {
                _open = check;

                await RefreshCountAsync(stock, check);

                return;
            }

            // A closed count is read back from the figures written when it was
            // closed, not recalculated — that row is what a board minute
            // quotes, and the library has moved on since.
            var found = await stock.ReconcileAsync(check);

            _result = found;

            Expected = check.TotalExpected;
            Found = check.TotalFound;
            MissingCount = check.TotalMissing;
            Progress = check.TotalExpected == 0 ? 0 : (double)check.TotalFound / check.TotalExpected;

            foreach (var (copy, title) in found.Missing)
            {
                Missing.Add(new MissingRow(
                    Session.Preferences.Accession(copy.AccessionNo),
                    title.Name,
                    Words.Of(copy.Status),
                    copy.Cost));
            }

            foreach (var stray in found.NotInRegister)
            {
                Strays.Add(stray);
            }

            Verdict = check.Status == VerificationStatus.ABANDONED
                ? "This count was abandoned. Its scans are kept, but it was never reconciled."
                : found.Missing.Count == 0
                    ? "Every copy expected on the shelf was found."
                    : $"{found.Missing.Count:N0} of {check.TotalExpected:N0} were not found.";

            OnPropertyChanged(nameof(HasStrays));
            OnPropertyChanged(nameof(NothingMissing));
        }
        catch (Exception ex)
        {
            Faults.Record("reading a stock check", ex);

            Problem = Faults.Explain(ex);
        }
    }

    private async Task RefreshCountAsync(StockCheck stock, StockVerification check)
    {
        var tally = await stock.CountAsync(check);

        Expected = tally.Expected;
        Found = tally.Found;
        Strangers = tally.NotInRegister;
        Twice = tally.ScannedTwice;
        Progress = tally.Progress;

        Recent.Clear();

        foreach (var (scan, what) in await stock.RecentAsync(check))
        {
            Recent.Add(new ScanRow(scan.Result, what, scan.BarcodeScanned, scan.ScannedAt));
        }

        OnPropertyChanged(nameof(Tally));
        OnPropertyChanged(nameof(Outstanding));
    }

    // ================================================================ starting

    [RelayCommand]
    private void Begin()
    {
        Starting = true;
        NewName = $"Stock Verification {DateTime.Today:yyyy}";
    }

    [RelayCommand]
    private void Never() => Starting = false;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (NewName.Trim().Length == 0)
        {
            Said = "Give the count a name — it is what the board minute will refer to.";
            SaidIsGood = false;

            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var check = await new StockCheck(db).StartAsync(
                NewName, Session.User!.UserId, null, DateOnly.FromDateTime(DateTime.Today));

            Starting = false;

            await LoadAsync();

            Selected = Checks.FirstOrDefault(c => c.Check.VerificationId == check.VerificationId);

            Said = "Started. Scan the shelves — every scan is written down as you make it, "
                + "so you can stop and pick this up again tomorrow.";

            SaidIsGood = true;
        }
        catch (Exception ex)
        {
            Faults.Record("starting a stock check", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    // ================================================================ scanning

    [RelayCommand]
    private async Task ScanAsync()
    {
        var barcode = Scanned.Trim();

        Scanned = "";

        if (barcode.Length == 0 || _open is null)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var stock = new StockCheck(db);

            var outcome = await stock.ScanAsync(_open, barcode, Session.User!.UserId);

            LastScan = outcome.Result switch
            {
                ScanResult.FOUND => $"{outcome.Title?.Name} · "
                    + Session.Preferences.Accession(outcome.Copy!.AccessionNo),

                ScanResult.DUPLICATE_SCAN => $"Already scanned — {outcome.Title?.Name}",

                _ => $"Not in the register — {barcode}",
            };

            LastWasGood = outcome.Result == ScanResult.FOUND;

            await RefreshCountAsync(stock, _open);
        }
        catch (Exception ex)
        {
            Faults.Record("scanning during a stock check", ex);

            LastScan = Faults.Explain(ex);
            LastWasGood = false;
        }
    }

    // ================================================================= closing

    [RelayCommand]
    private void AskToClose() => Closing = true;

    [RelayCommand]
    private void KeepGoing() => Closing = false;

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (_open is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var check = await db.StockVerifications
                .FirstAsync(v => v.VerificationId == _open.VerificationId);

            var found = await new StockCheck(db).CloseAsync(
                check, BoardReference, Session.User!.UserId, DateOnly.FromDateTime(DateTime.Today));

            Closing = false;
            BoardReference = "";

            Said = found.Missing.Count == 0
                ? "Closed. Every copy expected on the shelf was found."
                : $"Closed. {found.Missing.Count:N0} were not found — the shortage statement is ready to print.";

            SaidIsGood = found.Missing.Count == 0;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("closing a stock check", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task AbandonAsync()
    {
        if (_open is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var check = await db.StockVerifications
                .FirstAsync(v => v.VerificationId == _open.VerificationId);

            await new StockCheck(db).AbandonAsync(check, Session.User!.UserId,
                DateOnly.FromDateTime(DateTime.Today));

            Said = "Abandoned. The scans already made are kept.";
            SaidIsGood = true;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("abandoning a stock check", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task ShortageAsync()
    {
        if (Selected is null || _result is null || PrintShortage is null)
        {
            return;
        }

        await PrintShortage(Selected.Check, Selected.By, _result);
    }
}

/// <summary>One count in the list.</summary>
public record CheckRow(StockVerification Check, string By)
{
    public string Name => Check.Name;

    public string When => Check.CompletedOn is null
        ? $"started {Check.StartedOn:dd MMM yyyy}"
        : $"{Check.StartedOn:dd MMM} to {Check.CompletedOn:dd MMM yyyy}";

    public string State => Words.Of(Check.Status);

    public bool IsOpen => Check.Status == VerificationStatus.IN_PROGRESS;

    public bool HasShortage => Check.Status == VerificationStatus.COMPLETED && Check.TotalMissing > 0;

    public string Outcome => Check.Status switch
    {
        VerificationStatus.IN_PROGRESS => "still counting",
        VerificationStatus.ABANDONED => "abandoned",
        _ => Check.TotalMissing == 0
            ? $"all {Check.TotalExpected:N0} found"
            : $"{Check.TotalMissing:N0} not found",
    };
}

/// <summary>One scan, as the shelf sees it go by.</summary>
public record ScanRow(ScanResult Result, string What, string Barcode, DateTime When)
{
    public bool WasFound => Result == ScanResult.FOUND;

    public bool WasStrange => Result == ScanResult.NOT_IN_REGISTER;

    public string Verdict => Words.Of(Result);

    public string At => When.ToString("HH:mm");
}

/// <summary>One book that was not on the shelf.</summary>
public record MissingRow(string Accession, string Title, string State, decimal? Cost)
{
    public string CostText => Cost is null ? "" : Session.Preferences.Money(Cost.Value);
}
