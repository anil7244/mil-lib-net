using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Models;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The shell: which screen is showing, and the few things that belong to the
/// window rather than to any one screen.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private string _section = Home;
    [ObservableProperty] private string _dataFile = "";
    [ObservableProperty] private bool _dataFileMissing;

    // The crest and the names come from the settings, so renaming the library
    // reaches the application and its printed documents together rather than
    // leaving them disagreeing.
    [ObservableProperty] private string _organisation = "UNIT LIBRARY";
    [ObservableProperty] private string _libraryName = "LIBRARY";
    [ObservableProperty] private Bitmap? _crest;

    /// <summary>
    /// The disc behind the crest on the bar, or nothing when the unit has
    /// turned the circle off. The same setting the login screen and the web
    /// application read, so all three agree.
    /// </summary>
    [ObservableProperty] private IBrush _crestGround = Brushes.Transparent;

    [ObservableProperty] private string _who = "";
    [ObservableProperty] private string _role = "";

    public const string Home = "Home";

    private readonly Dictionary<string, ViewModelBase> _screens = [];

    public MainViewModel(string? startAt = null)
    {
        DataFile = Workspace.DatabasePath;
        DataFileMissing = !Workspace.DatabaseExists;

        Who = Session.Name;
        Role = Session.RoleName;

        WearTheUnitsColours();

        // The unit's own colour, over the whole application. Applied here
        // rather than at start-up because the settings are not readable until
        // somebody is in — this is the first moment the accent is known.
        Theming.Apply(Session.Preferences);

        // And again whenever it is changed on the Settings screen, so the
        // person choosing it is looking at the result while they choose.
        Theming.Changed += WearTheUnitsColours;

        Nodes = BuildMenu();

        _current = ScreenFor(Home);

        foreach (var node in Nodes)
        {
            node.Here = node.Holds(Home);
        }

        if (!string.IsNullOrWhiteSpace(startAt) && Nodes.Any(n => n.Holds(startAt)))
        {
            Section = startAt;
        }
    }

    /// <summary>The menu bar, as this person will see it.</summary>
    public IReadOnlyList<NavNode> Nodes { get; }

    /// <summary>
    /// The two lines beside the crest: what the software is called and whose
    /// library it is. Both out of the settings, so one build serves any unit.
    /// </summary>
    public string BarTitle => LibraryName;

    public string BarUnit => Organisation.Length > 0 ? Organisation : "Military Library";

    /// <summary>Whether this person may hand the machine to the reading room.</summary>
    public bool ShowManual => true;

    // ------------------------------------------------------------ licence --

    /// <summary>
    /// Whether to say anything about the licence above every screen.
    ///
    /// A trial always earns it — somebody has to know the application will
    /// stop. A licence with months left does not: a banner that is there every
    /// day becomes part of the furniture and stops being read, and then the
    /// one that matters is not read either.
    /// </summary>
    public bool LicenceWorthSaying => Licensing.Standing?.WorthSaying ?? false;

    public string LicenceHeadline => Licensing.Standing?.Headline ?? "";

    public string LicenceWhat => Licensing.Standing?.What ?? "";

    public bool LicenceGrave => Licensing.Standing?.Grave ?? false;

    /// <summary>
    /// Whether the licence screen is reachable from the banner — it is not,
    /// for somebody who could not open it from the menu either.
    /// </summary>
    public bool MayFixLicence => Session.Can(Ability.SettingsManage);

    [RelayCommand]
    private void GoToLicence() => Section = "Licence";

    // ---------------------------------------------------- the reading room --

    /// <summary>
    /// Whether this install has a public catalogue terminal at all, and whether
    /// this person may hand the machine over to it.
    /// </summary>
    public bool MayOpenKiosk =>
        Session.Has(Feature.MemberLogin) && Session.Can(Ability.CatalogueView);

    /// <summary>Raised when the machine should become a reading-room terminal.</summary>
    public event Action? OpenKiosk;

    [RelayCommand]
    private void ReadingRoom() => OpenKiosk?.Invoke();

    /// <summary>Raised when the person signs out, for the shell to act on.</summary>
    public event Action? SignedOut;

    [RelayCommand]
    private async Task SignOutAsync()
    {
        var user = Session.User;

        if (user is not null)
        {
            try
            {
                await using var db = Workspace.Open();

                await new SignIn(db).SignOutAsync(user.UserId, Session.Machine);
            }
            catch (Exception ex)
            {
                // Worth recording, but not worth keeping somebody signed in
                // over: the sign-out itself must always go through.
                Faults.Record("recording sign-out", ex);
            }
        }

        Session.End();

        SignedOut?.Invoke();
    }

    [RelayCommand]
    private void Go(string section) => Section = section;

    partial void OnSectionChanged(string value)
    {
        Current = ScreenFor(value);

        // The button carrying the screen lights up — including the group
        // button, when the screen came out of its list. Without it a person
        // three screens into Administration has nothing telling them where
        // they are.
        foreach (var node in Nodes)
        {
            node.Here = node.Holds(value);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SectionIcon));
    }

    /// <summary>
    /// The heading at the top of the working area. Kept here so every screen is
    /// introduced the same way.
    /// </summary>
    public string Title => Section;

    /// <summary>
    /// The same icon the rail is showing, repeated beside the heading, so the
    /// eye can confirm where it is without travelling back to the rail.
    /// </summary>
    public Geometry? SectionIcon => NavItem.Glyph(
        Nodes.SelectMany(n => n.Items).FirstOrDefault(i => i.Section == Section)?.IconKey ?? "IconDashboard");

    /// <summary>
    /// Screens are made once and kept.
    ///
    /// Somebody who searches the catalogue, steps over to the counter and comes
    /// back should find their search where they left it. Rebuilding the screen
    /// each time also means re-reading the whole table each time, which on a
    /// library of this size is a visible pause for no gain.
    /// </summary>
    private ViewModelBase ScreenFor(string section)
    {
        if (_screens.TryGetValue(section, out var made))
        {
            return made;
        }

        ViewModelBase screen = section switch
        {
            Home => new DashboardViewModel(Go),
            "Books in Library" => new BooksViewModel(),
            "Issue & Return" => new CounterViewModel(),
            "Members" => new MembersViewModel(),
            "Lending Rules" => new LendingRulesViewModel(),
            "Accession Register" => new RegisterViewModel(),
            "Labels" => new LabelsViewModel(),
            "Reports" => new ReportsViewModel(),
            "Stock Check" => new StockViewModel(),
            "Withdrawals" => new WithdrawalsViewModel(),
            "Reservations" => new ReservationsViewModel(),
            "Fines" => new FinesViewModel(),
            "Settings" => new SettingsViewModel(),
            "Staff Accounts" => new StaffViewModel(),
            "Activity" => new ActivityViewModel(),
            "Subjects" => new SubjectsViewModel(),
            "Database" => new DatabaseViewModel(),
            "Licence" => new LicenceViewModel(),
            _ => new NotBuiltYetViewModel(section),
        };

        _screens[section] = screen;

        return screen;
    }

    /// <summary>
    /// The menu bar, in the order the work is done.
    ///
    /// Grouped, and deliberately short. The screens somebody uses all day get
    /// their own button; everything else is gathered under the thing it belongs
    /// to — the counter's three screens under one "Issue &amp; Return", the whole
    /// life of a book under "Books in Library", and the settings a unit touches
    /// twice a year under one "Administration" at the far end.
    ///
    /// That is five buttons instead of eighteen rows. Nothing has moved further
    /// away — everything is still one click — but the top of the window can now
    /// be read at a glance instead of scanned.
    ///
    /// The words are the operator's, not the cataloguer's: "Books in Library",
    /// not "Bibliographic Records".
    /// </summary>
    /// <summary>
    /// Take the name, the crest and the crest's ground from the settings.
    ///
    /// Read rather than bound because these change on one screen, rarely, and
    /// a binding for each would be three more things to keep true. Called at
    /// the start and again whenever the branding is saved.
    /// </summary>
    private void WearTheUnitsColours()
    {
        Organisation = Session.Preferences.OrganisationName.ToUpperInvariant();
        LibraryName = Session.Preferences.LibraryName.ToUpperInvariant();
        Crest = Pictures.Load(Workspace.CrestPath(Session.Preferences.CrestPath));

        CrestGround = Session.Preferences.CrestInCircle
            ? new SolidColorBrush(Theming.Parse(
                Session.Preferences.CrestCircleColour.Trim().Length > 0
                    ? Session.Preferences.CrestCircleColour
                    : Session.Preferences.AccentColour) ?? Theming.Accent)
            : Brushes.Transparent;

        OnPropertyChanged(nameof(BarTitle));
        OnPropertyChanged(nameof(BarUnit));
    }

    private static IReadOnlyList<NavNode> BuildMenu()
    {
        NavNode[] all =
        [
            NavNode.One(new NavItem(Home, "Home", "IconDashboard")),

            // The counter loop. One button, because a clerk who is issuing is
            // also taking payment and handing over holds.
            new("Issue & Return", "IconCirculation",
            [
                new("Issue & Return", "Issue & Return", "IconCirculation", Ability.CirculationOperate),
                new("Reservations", "Reservations", "IconReservations", Ability.ReservationsManage, Feature.Reservations),
                new("Fines", "Fines", "IconFines", Ability.FinesManage, Feature.Fines),
            ]),

            // Everything about the books themselves, from cataloguing one to
            // taking it off the books.
            new("Books in Library", "IconBooks",
            [
                new("Books in Library", "All Books", "IconSearch", Ability.CatalogueView),
                new("Accession Register", "Book Register", "IconRegister", Ability.CatalogueView),
                new("Subjects", "Subjects", "IconLabels", Ability.CatalogueManage),
                new("Labels", "Labels & Barcodes", "IconBarcode", Ability.CatalogueManage, Feature.Barcode),
                new("Stock Check", "Stock Check", "IconStock", Ability.StockVerify, Feature.StockVerify),
                new("Withdrawals", "Removed Books", "IconWithdrawal", Ability.WithdrawalsManage, Feature.Withdrawal),
            ]),

            // Used constantly, so it stays a button of its own.
            NavNode.One(new NavItem("Members", "Members", "IconMembers", Ability.MembersView)),

            // The infrequent half: what a unit sets once and looks at rarely.
            // Pushed to the right, away from the day's work.
            new("Administration", "IconSettings",
            [
                new("Reports", "Reports", "IconReports", Ability.ReportsView),
                new("Lending Rules", "Member Types", "IconRules", Ability.SettingsManage),
                new("Staff Accounts", "Staff Accounts", "IconUsers", Ability.UsersManage),
                new("Activity", "Activity Log", "IconActivity", Ability.AuditView),
                new("Settings", "Settings", "IconSettings", Ability.SettingsManage),
                new("Database", "Database & Backups", "IconDatabase", Ability.SettingsManage),
                new("Licence", "Licence", "IconLicence", Ability.SettingsManage),
            ], trailing: true),
        ];

        // A node whose every screen was filtered out leaves no button behind.
        return [.. all.Select(n => n.Narrowed()).Where(n => n.AnythingHere)];
    }
}
