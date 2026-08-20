using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Fines.
///
/// A record of what is owed, not a cash book. Nothing here handles money: a
/// payment is somebody writing down the number of a receipt issued elsewhere,
/// and a waiver is a reason going on the audit log. The library does not hold
/// cash and this screen must not look as though it does.
///
/// Only a pending fine can be settled. A paid or waived one is history, and
/// history that can be edited is not history.
/// </summary>
public partial class FinesViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private FineStatus _showing = FineStatus.PENDING;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _outstanding = "";

    // ------------------------------------------------------------- settling
    [ObservableProperty] private FineRow? _chosen;
    [ObservableProperty] private bool _paying;
    [ObservableProperty] private bool _waiving;
    [ObservableProperty] private string _receipt = "";
    [ObservableProperty] private string _reason = "";

    private int _keystroke;

    public FinesViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<FineRow> Fines { get; } = [];

    public FineStatus[] States { get; } = Enum.GetValues<FineStatus>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.FinesManage);

    public bool Nothing => !Busy && Fines.Count == 0;

    public bool Settling => Paying || Waiving;

    public string Tally => Busy
        ? "Reading…"
        : Fines.Count == 0
            ? ""
            : $"{Fines.Count:N0} · {Session.Preferences.Money(Fines.Sum(f => f.Owing.Fine.Amount))}";

    public string NothingSays => Showing == FineStatus.PENDING
        ? "Nobody owes anything."
        : $"Nothing has been {Words.Of(Showing).ToLowerInvariant()}.";

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnShowingChanged(FineStatus value)
    {
        OnPropertyChanged(nameof(NothingSays));

        Stop();

        _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) => _ = LookSoonAsync();

    partial void OnPayingChanged(bool value) => OnPropertyChanged(nameof(Settling));

    partial void OnWaivingChanged(bool value) => OnPropertyChanged(nameof(Settling));

    private async Task LookSoonAsync()
    {
        var mine = ++_keystroke;

        await Task.Delay(200);

        if (mine == _keystroke)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var fines = new Fines(db);

            Fines.Clear();

            foreach (var owing in await fines.InStateAsync(Showing, Search))
            {
                Fines.Add(new FineRow(owing));
            }

            // The whole library's outstanding balance, said once. It is the
            // figure anybody asks for, and it does not change with the filter.
            var total = await fines.OutstandingAsync();

            Outstanding = total > 0
                ? $"{Session.Preferences.Money(total)} outstanding across the library"
                : "Nothing outstanding across the library";
        }
        catch (Exception ex)
        {
            Faults.Record("reading the fines", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Tally));
            OnPropertyChanged(nameof(Nothing));
        }
    }

    [RelayCommand]
    private void Pay(FineRow row)
    {
        Chosen = row;
        Paying = true;
        Waiving = false;
        Receipt = "";
        Said = "";
    }

    [RelayCommand]
    private void Waive(FineRow row)
    {
        Chosen = row;
        Waiving = true;
        Paying = false;
        Reason = "";
        Said = "";
    }

    [RelayCommand]
    private void Stop()
    {
        Paying = false;
        Waiving = false;
        Chosen = null;
    }

    [RelayCommand]
    private async Task ConfirmPaymentAsync()
    {
        if (Chosen is null || Busy)
        {
            return;
        }

        if (Receipt.Trim().Length == 0)
        {
            Said = "A receipt number is needed. The library records the payment; it does not take it.";
            SaidIsGood = false;

            return;
        }

        await SettleAsync(async (fines, fine) =>
        {
            await fines.PayAsync(fine, Receipt.Trim(), Session.User!.UserId,
                DateOnly.FromDateTime(DateTime.Today));

            return $"Recorded as paid against receipt {Receipt.Trim()}.";
        });
    }

    [RelayCommand]
    private async Task ConfirmWaiverAsync()
    {
        if (Chosen is null || Busy)
        {
            return;
        }

        if (Reason.Trim().Length == 0)
        {
            Said = "A waiver needs a reason. It goes on the record against your name.";
            SaidIsGood = false;

            return;
        }

        await SettleAsync(async (fines, fine) =>
        {
            await fines.WaiveAsync(fine, Reason.Trim(), Session.User!.UserId);

            return "Waived. The reason is on the record.";
        });
    }

    private async Task SettleAsync(Func<Fines, Fine, Task<string>> settle)
    {
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var fine = await db.Fines
                .FirstOrDefaultAsync(f => f.FineId == Chosen!.Owing.Fine.FineId);

            // Checked again here, not only when the panel opened. Two people at
            // two counters can be looking at the same fine.
            if (fine is null || fine.Status != FineStatus.PENDING)
            {
                Said = "That fine has already been settled.";
                SaidIsGood = false;

                Stop();

                await LoadAsync();

                return;
            }

            Said = await settle(new Fines(db), fine);
            SaidIsGood = true;

            Stop();

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("settling a fine", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>One charge on the list.</summary>
public record FineRow(Owing Owing)
{
    public string Member => Owing.Member.Display;

    public string Number => Owing.Member.MembershipNo;

    public string Amount => Session.Preferences.Money(Owing.Fine.Amount);

    public string Kind => Words.Of(Owing.Fine.Type);

    public string Raised => Owing.Fine.CalculatedOn.ToString("dd MMM yyyy");

    public string About => Owing.AboutABook
        ? $"{Owing.Title} · {Session.Preferences.Accession(Owing.Accession)}"
        : Owing.Fine.Remarks ?? "";

    public bool Settleable => Owing.Settleable && Session.Can(Ability.FinesManage);

    /// <summary>
    /// How it was settled, for the lists that are history rather than work: a
    /// receipt number, or the reason it was let go.
    /// </summary>
    public string Settled => Owing.Fine.Status switch
    {
        FineStatus.PAID => $"receipt {Owing.Fine.ReceiptNo} · {Owing.Fine.PaidOn:dd MMM yyyy}",
        FineStatus.WAIVED => Owing.Fine.WaiverReason ?? "waived",
        FineStatus.WRITTEN_OFF => "written off",
        _ => "",
    };

    public bool IsSettled => Settled.Length > 0;

    /// <summary>An overdue charge is routine; a loss or damage is not.</summary>
    public bool IsSerious => Owing.Fine.Type is FineType.LOSS or FineType.DAMAGE;
}
