using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Licensing;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The sign-in screen.
///
/// It carries the unit's own crest, name and motto rather than the software's,
/// because this is the first thing anybody sees and the first thing they should
/// recognise. All three come out of the settings, so a library that changes its
/// name changes this screen with it.
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private string _organisation = "UNIT LIBRARY";
    [ObservableProperty] private string _motto = "";
    [ObservableProperty] private string _libraryName = "LIBRARY MANAGEMENT SYSTEM";
    [ObservableProperty] private Bitmap? _crest;

    [ObservableProperty] private string _dataFile = "";
    [ObservableProperty] private bool _dataFileMissing;

    // ---------------------------------------------------- clock and theme --

    /// <summary>
    /// The time and the date, ticking across the top strip, the way the terminal
    /// carries them once it is signed in. A guard machine is watched all day, and
    /// the front door showing the wrong time reads as a machine nobody tends.
    /// </summary>
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _clockDate = "";

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    // ------------------------------------------------------------- licence --
    [ObservableProperty] private string _licence = "";
    [ObservableProperty] private bool _licenceIsWarning;
    [ObservableProperty] private bool _licenceIsGrave;
    [ObservableProperty] private bool _mayActivate;

    /// <summary>
    /// True when this copy may not be used — the trial is over, or the licence
    /// has expired. Sign-in is refused until a key is entered, and the front
    /// door says so and offers the way to do it, because the screen where a key
    /// is normally typed is on the far side of a sign-in this copy will not
    /// allow.
    /// </summary>
    [ObservableProperty] private bool _locked;

    /// <summary>
    /// The unit's own accent, out of the settings, so this screen is the
    /// colour the rest of the unit's printed material is.
    /// </summary>
    [ObservableProperty] private IBrush _accent = new SolidColorBrush(Color.Parse("#C0392B"));

    /// <summary>
    /// What sits behind the crest: the chosen circle colour, or nothing at all.
    ///
    /// A setting, because a crest that already has its own ground looks wrong
    /// on a coloured disc and one drawn as bare artwork looks unfinished
    /// without it. This install has the circle off, which is why the crest sits
    /// straight on the panel — the same as the web application does it.
    /// </summary>
    [ObservableProperty] private IBrush _crestGround = Brushes.Transparent;

    [ObservableProperty] private IBrush _crestEdge = Brushes.Transparent;

    public string Version => $"Version {Vendor.Version} · Air-gapped";

    public string VendorLine => $"{Vendor.Company} · {Vendor.Website}";

    /// <summary>Just the address, for the foot of the sign-in screen.</summary>
    public string Website => Vendor.Website;

    public bool HasProblem => Problem.Length > 0;

    public bool HasMotto => Motto.Length > 0;

    public bool HasOrganisation => Organisation.Length > 0;

    public bool HasLicence => Licence.Length > 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnMottoChanged(string value) => OnPropertyChanged(nameof(HasMotto));

    partial void OnOrganisationChanged(string value) => OnPropertyChanged(nameof(HasOrganisation));

    partial void OnLicenceChanged(string value) => OnPropertyChanged(nameof(HasLicence));

    public LoginViewModel()
    {
        DataFile = Workspace.DatabasePath;
        DataFileMissing = !Workspace.DatabaseExists;

        // The username is not kept between sessions. A shared terminal left at
        // the login screen should give nothing away — not even whose account was
        // last used — so every sign-in is typed in full, and any name a previous
        // build stored is wiped the moment this screen opens.
        Remembered.Keep("");

        Tock();
        _tick.Tick += (_, _) => Tock();
        _tick.Start();

        _ = LoadBrandingAsync();
    }

    private void Tock()
    {
        var now = DateTime.Now;

        Clock = now.ToString("HH:mm:ss");
        ClockDate = now.ToString("dddd, dd MMMM yyyy").ToUpperInvariant();
    }

    /// <summary>Empty the fields, for whoever mistyped and wants a clean start.</summary>
    [RelayCommand]
    private void Reset()
    {
        Username = "";
        Password = "";
        Problem = "";
    }

    /// <summary>Raised once, when somebody is actually let in.</summary>
    public event Action? Allowed;

    private async Task LoadBrandingAsync()
    {
        try
        {
            await using var db = Workspace.Open();

            var preferences = await Preferences.ReadAsync(db);

            // The unit's colour and its chosen theme, over the sign-in screen as
            // well — so a unit that runs light does not meet a dark front door.
            // The theme is only ever chosen from inside the application; here it
            // is followed, not offered.
            Theming.Apply(preferences);

            Organisation = preferences.OrganisationName.ToUpperInvariant();
            Motto = preferences.Motto.ToUpperInvariant();
            LibraryName = preferences.LibraryName.ToUpperInvariant();
            Crest = Pictures.Load(Workspace.CrestPath(preferences.CrestPath));

            try
            {
                Accent = new SolidColorBrush(Color.Parse(preferences.AccentColour));

                if (preferences.CrestInCircle)
                {
                    var ground = preferences.CrestCircleColour.Trim().Length > 0
                        ? preferences.CrestCircleColour
                        : preferences.AccentColour;

                    CrestGround = new SolidColorBrush(Color.Parse(ground));
                    CrestEdge = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
                }
            }
            catch (Exception)
            {
                // A colour somebody typed wrong is not worth a blank screen.
            }

            // A key typed into the setup wizard is activated here, the first
            // time the application opens, then the file it was left in is gone.
            await Licensing.ConsumePendingKeyAsync();

            // Said here as well as inside, because the web application says it
            // here and this screen is meant to be the same screen. What it
            // gives away is that a unit's licence is running out, to somebody
            // already standing at the unit's own machine.
            ShowLicence(await Licensing.RefreshAsync());
        }
        catch
        {
            // The defaults stand, and the missing-file notice on this same
            // screen explains the rest. A sign-in screen that cannot draw
            // itself is still a sign-in screen.
        }
    }

    private void ShowLicence(LicenceStanding standing)
    {
        Licence = standing.State switch
        {
            LicenceState.Trial => $"Trial — {standing.DaysLeft} day"
                + (standing.DaysLeft == 1 ? "" : "s") + " left.",
            LicenceState.TrialOver => "Trial ended.",
            LicenceState.Expired => "Licence expired.",
            _ when standing.Perpetual => "Licensed",
            _ => $"Licensed · until {standing.Expires:dd MMM yyyy}",
        };

        LicenceIsGrave = standing.State is LicenceState.TrialOver or LicenceState.Expired;
        LicenceIsWarning = standing.State == LicenceState.Trial;
        MayActivate = !standing.Usable || standing.State == LicenceState.Trial;
        Locked = !standing.Usable;
    }

    /// <summary>Raised when the key-entry dialog should be opened.</summary>
    public event Action? EnterKeyAsked;

    /// <summary>
    /// Open the key-entry dialog. Reachable whether or not this copy is locked,
    /// so a trial can be turned into a licence early and a running licence
    /// renewed without waiting for it to lapse.
    /// </summary>
    [RelayCommand]
    private void EnterKey() => EnterKeyAsked?.Invoke();

    /// <summary>
    /// Read the licence again after the key-entry dialog closes, so a copy that
    /// was just unlocked stops saying it is locked and lets the person in.
    /// </summary>
    public async Task RefreshLicenceAsync()
    {
        try
        {
            ShowLicence(await Licensing.RefreshAsync());

            if (!Locked)
            {
                Problem = "";
            }
        }
        catch
        {
            // If it cannot be re-read, the state on screen simply stands.
        }
    }

    [RelayCommand]
    private async Task EnterAsync()
    {
        if (Busy)
        {
            return;
        }

        Problem = "";

        if (DataFileMissing)
        {
            Problem = "There is no data file to sign in to. " + DataFile;
            return;
        }

        // The one gate the licence actually is. A trial that has run out or a
        // licence that has lapsed refuses sign-in, and points at the key entry
        // rather than leaving somebody typing a password that will never work —
        // and it says the records are still there, because they are.
        if (Locked)
        {
            Problem = "This copy needs a valid licence key before it can be used. "
                + "Enter one below to unlock it — nothing has been lost, and the "
                + "records are all still here.";
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var result = await new SignIn(db).AttemptAsync(Username, Password, Session.Machine);

            // Cleared whether the attempt succeeded or failed. A rejected
            // password left sitting in the box is one shoulder-glance from
            // being read.
            Password = "";

            if (!result.Ok)
            {
                Problem = result.Problem;
                return;
            }

            Session.Begin(result.User!, await Preferences.ReadAsync(db));

            Allowed?.Invoke();
        }
        catch (Exception ex)
        {
            Faults.Record("signing in", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }
}
