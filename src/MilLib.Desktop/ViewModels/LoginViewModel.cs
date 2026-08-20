using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    /// <summary>
    /// Fill the username in next time.
    ///
    /// The username only, never the password. Closing this application signs
    /// you out — that is written down in the README and people rely on it — so
    /// a tick that quietly kept somebody signed in would contradict what the
    /// product says about itself. Saving the four or five characters somebody
    /// types every morning is the honest half of the idea, and it is the half
    /// that is actually worth having.
    /// </summary>
    [ObservableProperty] private bool _remember;

    // ------------------------------------------------------------- licence --
    [ObservableProperty] private string _licence = "";
    [ObservableProperty] private bool _licenceIsWarning;
    [ObservableProperty] private bool _licenceIsGrave;
    [ObservableProperty] private bool _mayActivate;

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

    public bool HasProblem => Problem.Length > 0;

    public bool HasMotto => Motto.Length > 0;

    public bool HasOrganisation => Organisation.Length > 0;

    public bool HasLicence => Licence.Length > 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnMottoChanged(string value) => OnPropertyChanged(nameof(HasMotto));

    partial void OnOrganisationChanged(string value) => OnPropertyChanged(nameof(HasOrganisation));

    partial void OnLicenceChanged(string value) => OnPropertyChanged(nameof(HasLicence));

    partial void OnRememberChanged(bool value) => Remembered.Keep(value ? Username : "");

    public LoginViewModel()
    {
        DataFile = Workspace.DatabasePath;
        DataFileMissing = !Workspace.DatabaseExists;

        Username = Remembered.Username;
        Remember = Username.Length > 0;

        _ = LoadBrandingAsync();
    }

    /// <summary>Raised once, when somebody is actually let in.</summary>
    public event Action? Allowed;

    private async Task LoadBrandingAsync()
    {
        try
        {
            await using var db = Workspace.Open();

            var preferences = await Preferences.ReadAsync(db);

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

            // Only once somebody has actually got in. A username kept from a
            // failed attempt is a username somebody mistyped.
            Remembered.Keep(Remember ? Username : "");

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
