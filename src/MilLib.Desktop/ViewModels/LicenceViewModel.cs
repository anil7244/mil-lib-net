using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Licensing;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The licence: what this installation is entitled to, and how to put it right.
///
/// Written for the situation it is actually read in — a trial that has run out
/// on a Monday morning with a queue at the counter. So the two things somebody
/// needs are the largest things on it: the hardware ID to send, and the box to
/// type the key into. Everything else is underneath.
///
/// The one thing this screen must never do is imply that records have been
/// lost. A licence that has lapsed is a bill, not a disaster, and the words on
/// this screen are chosen to say so.
/// </summary>
public partial class LicenceViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private bool _copied;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _what = "";
    [ObservableProperty] private bool _grave;
    [ObservableProperty] private string _hardwareId = "";
    [ObservableProperty] private string _machineSource = "";
    [ObservableProperty] private string _installed = "";
    [ObservableProperty] private string _currentKey = "";
    [ObservableProperty] private bool _licensed;
    [ObservableProperty] private string _stateBadge = "";

    // ------------------------------------------------------- the term bar --

    /// <summary>
    /// The trial or the licence drawn as a bar filling from a start date to an
    /// end date, so how much time is left is seen before it is read. A trial
    /// counts down its fourteen days; a dated licence counts down its term; a
    /// perpetual one is simply full.
    /// </summary>
    [ObservableProperty] private bool _showTerm;
    [ObservableProperty] private double _termFraction;
    [ObservableProperty] private string _termLabel = "";
    [ObservableProperty] private string _termValue = "";
    [ObservableProperty] private string _termStart = "";
    [ObservableProperty] private string _termEnd = "";

    public LicenceViewModel()
    {
        _ = LoadAsync();
    }

    public bool HasSaid => Said.Length > 0;

    public bool MayActivate => Session.Can(Ability.SettingsManage);

    public string Product => $"{Vendor.Product} {Vendor.Version}";

    public string Company => Vendor.Company;

    public string Website => Vendor.Website;

    public string Phone => Vendor.Phone;

    public string Email => Vendor.Email;

    public string Address => Vendor.Address;

    /// <summary>
    /// What to send when asking for a key. One block, so it can be copied and
    /// pasted into a message in one go rather than transcribed field by field
    /// — a hardware ID read out over a telephone and typed back in wrong is
    /// the commonest way this goes wrong.
    /// </summary>
    public string ToSend =>
        $"{Vendor.Product} {Vendor.Version}{Environment.NewLine}"
        + $"Hardware ID: {HardwareId}{Environment.NewLine}"
        + $"Library: {Session.Preferences.LibraryName}{Environment.NewLine}"
        + $"Unit: {Session.Preferences.OrganisationName}";

    /// <summary>Raised when the block above should go to the clipboard.</summary>
    public event Func<string, Task>? Copy;

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnHardwareIdChanged(string value) => OnPropertyChanged(nameof(ToSend));

    private async Task LoadAsync()
    {
        Busy = true;

        try
        {
            var standing = await Licensing.RefreshAsync();

            Show(standing);

            await using var db = Workspace.Open();

            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.LicenseInfo, l => l.HardwareId == standing.HardwareId);

            Installed = row?.ActivatedAt is { } when
                ? $"Activated {when:dd MMM yyyy 'at' HH:mm}."
                : row?.TrialStartedAt is { } began
                    ? $"This copy was first opened {began:dd MMM yyyy}."
                    : "";

            BuildTerm(standing, row?.TrialStartedAt, row?.ActivatedAt);
        }
        catch (Exception ex)
        {
            Faults.Record("reading the licence", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Show(LicenceStanding standing)
    {
        Headline = standing.Headline;
        What = standing.What;
        Grave = standing.Grave;
        HardwareId = standing.HardwareId;
        MachineSource = Machine.Source.Length > 0
            ? $"Read from {Machine.Source}. It is worked out fresh each time and never written down."
            : "";
        CurrentKey = standing.Key ?? "";
        Licensed = standing.State == LicenceState.Licensed;

        StateBadge = standing.State switch
        {
            LicenceState.Trial => "TRIAL",
            LicenceState.TrialOver => "TRIAL ENDED",
            LicenceState.Expired => "EXPIRED",
            _ when standing.Perpetual => "LICENSED",
            _ => "LICENSED",
        };
    }

    /// <summary>
    /// Work out the term bar: what to call it, how full it is, and the two dates
    /// at its ends. A cleared installation with no term to show hides the bar
    /// rather than drawing an empty one.
    /// </summary>
    private void BuildTerm(LicenceStanding standing, DateTime? trialStarted, DateTime? activated)
    {
        static double Clamp(double v) => Math.Clamp(v, 0, 1);

        switch (standing.State)
        {
            case LicenceState.Trial:
            {
                var days = Licence.TrialDays;

                TermLabel = "TRIAL PERIOD";
                TermFraction = Clamp((double)standing.DaysLeft / days);
                TermValue = standing.DaysLeft <= 1
                    ? "Ends today"
                    : $"{standing.DaysLeft} of {days} days left";
                TermStart = trialStarted is { } s ? $"Opened {s:dd MMM yyyy}" : "Trial";
                TermEnd = trialStarted is { } e ? $"Ends {e.AddDays(days):dd MMM yyyy}" : "";
                ShowTerm = true;
                break;
            }

            case LicenceState.TrialOver:
            {
                TermLabel = "TRIAL PERIOD";
                TermFraction = 0;
                TermValue = "The trial has ended";
                TermStart = trialStarted is { } s ? $"Opened {s:dd MMM yyyy}" : "Trial";
                TermEnd = "A key is needed";
                ShowTerm = true;
                break;
            }

            case LicenceState.Licensed when standing.Perpetual:
            {
                TermLabel = "LICENCE";
                TermFraction = 1;
                TermValue = "Perpetual — never expires";
                TermStart = activated is { } a ? $"Activated {a:dd MMM yyyy}" : "Activated";
                TermEnd = "No expiry";
                ShowTerm = true;
                break;
            }

            case LicenceState.Licensed:
            {
                var expires = standing.Expires;
                var from = activated is { } a ? DateOnly.FromDateTime(a) : (DateOnly?)null;

                double fraction = standing.DaysLeft > 0 ? 1 : 0;

                if (from is { } f && expires is { } exp)
                {
                    var term = exp.DayNumber - f.DayNumber;

                    if (term > 0)
                    {
                        fraction = Clamp((double)standing.DaysLeft / term);
                    }
                }

                TermLabel = "LICENCE VALIDITY";
                TermFraction = fraction;
                TermValue = standing.DaysLeft <= 0
                    ? "Expires today"
                    : $"{standing.DaysLeft} days left";
                TermStart = from is { } fr ? $"Activated {fr:dd MMM yyyy}" : "Activated";
                TermEnd = expires is { } ex ? $"Until {ex:dd MMM yyyy}" : "";
                ShowTerm = true;
                break;
            }

            case LicenceState.Expired:
            {
                TermLabel = "LICENCE VALIDITY";
                TermFraction = 0;
                TermValue = "The licence has expired";
                TermStart = activated is { } a ? $"Activated {a:dd MMM yyyy}" : "";
                TermEnd = standing.Expires is { } ex ? $"Expired {ex:dd MMM yyyy}" : "Expired";
                ShowTerm = true;
                break;
            }

            default:
                ShowTerm = false;
                break;
        }
    }


    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (Busy || !MayActivate)
        {
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var (ok, said) = await Licensing.For(db)
                .ActivateAsync(Key, DateOnly.FromDateTime(DateTime.Now));

            Said = said;
            SaidIsGood = ok;

            if (ok)
            {
                Key = "";

                Show(await Licensing.RefreshAsync());
            }
        }
        catch (Exception ex)
        {
            Faults.Record("entering a licence key", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task CopyDetailsAsync()
    {
        if (Copy is null)
        {
            return;
        }

        await Copy(ToSend);

        Copied = true;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Copied = false;

        await LoadAsync();
    }
}
