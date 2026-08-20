using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Models;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// How this library is set up.
///
/// Four things, saved separately because they are four decisions taken at four
/// different times: what the unit is called, how it numbers its books, how big
/// its labels are, and which screens it has at all. Saving them together would
/// mean a person adjusting a label size has to be sure the branding fields are
/// still right, which is not a question they came here to answer.
///
/// One value on this screen cannot be taken back: the accession starting
/// number, which locks the moment the first book is accessioned. It is shown
/// locked rather than hidden, because somebody looking for it should find out
/// that it exists and why it will not move.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // ------------------------------------------------------------ branding --
    [ObservableProperty] private string _organisation = "";
    [ObservableProperty] private string _libraryName = "";
    [ObservableProperty] private string _motto = "";
    [ObservableProperty] private string _accent = "#c0392b";
    // Off, not on.
    //
    // A settings form starts as a set of claims about the unit that nobody has
    // made yet, and every one of them is a claim the next save writes down. Two
    // of these started true, so a form saved before it had finished reading the
    // settings — or after a read that failed — told the unit it wanted a dark
    // window and its crest in a circle, neither of which it had asked for.
    // The safe starting answer to "does this unit want X" is no.
    [ObservableProperty] private bool _darkByDefault;
    [ObservableProperty] private bool _crestInCircle;

    /// <summary>
    /// Whether the settings on screen are the settings in the database.
    ///
    /// False until the read finishes, and false again if it fails. Saving is
    /// refused while it is false, because a save is a write of every field at
    /// once: one that ran against a half-read form would not fail, it would
    /// succeed at storing the wrong thing.
    /// </summary>
    private bool _loaded;
    [ObservableProperty] private string _crestCircleColour = "#c0392b";
    [ObservableProperty] private Bitmap? _crest;
    [ObservableProperty] private string _crestNote = "";

    /// <summary>Null until somebody picks a new crest, so a save leaves the old one alone.</summary>
    private string? _chosenCrest;

    // ----------------------------------------------------------- accession --
    [ObservableProperty] private string _prefix = "";
    [ObservableProperty] private bool _showPrefix = true;
    [ObservableProperty] private int _padLength = 9;
    [ObservableProperty] private int _startFrom = 1;
    [ObservableProperty] private bool _startLocked;
    [ObservableProperty] private string _nextNumber = "";
    [ObservableProperty] private string _lockNote = "";

    // -------------------------------------------------------------- labels --
    [ObservableProperty] private double _pocketWidth = 51;
    [ObservableProperty] private double _pocketHeight = 25;
    [ObservableProperty] private double _spineWidth = 25;
    [ObservableProperty] private double _spineHeight = 38;
    [ObservableProperty] private bool _oneLabelPerPage;
    [ObservableProperty] private CodeChoice _chosenCode = CodeChoice.All[0];
    [ObservableProperty] private bool _finePrinter;

    public SettingsViewModel()
    {
        Palettes = Palette.All(colour => Accent = colour);

        _ = LoadAsync();
    }

    /// <summary>The screens this install can have, each with its switch.</summary>
    public ObservableCollection<FeatureRow> Features { get; } = [];

    public IReadOnlyList<CodeChoice> Codes => CodeChoice.All;

    /// <summary>
    /// The two colours, as brushes rather than as the text in the boxes beside
    /// them, so the swatch changes as it is typed and a half-typed colour shows
    /// as nothing rather than throwing.
    /// </summary>
    public IBrush AccentBrush => Paint(Accent);

    public IBrush CircleBrush => Paint(CrestCircleColour);

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.SettingsManage);

    public bool MayChangeStart => MayManage && !StartLocked;

    /// <summary>
    /// Raised when a crest needs choosing. The view answers it, because a file
    /// dialog needs a window to hang off.
    /// </summary>
    public event Func<Task<string?>>? PickCrest;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnStartLockedChanged(bool value) => OnPropertyChanged(nameof(MayChangeStart));

    partial void OnPrefixChanged(string value) => OnPropertyChanged(nameof(SampleNumber));

    partial void OnShowPrefixChanged(bool value) => OnPropertyChanged(nameof(SampleNumber));

    partial void OnPadLengthChanged(int value) => OnPropertyChanged(nameof(SampleNumber));

    partial void OnAccentChanged(string value) => OnPropertyChanged(nameof(AccentBrush));

    partial void OnCrestCircleColourChanged(string value) => OnPropertyChanged(nameof(CircleBrush));

    /// <summary>
    /// A colour typed into a box, or nothing at all while it is half typed.
    /// Transparent rather than a guess: a swatch showing the wrong colour is
    /// worse than a swatch showing none.
    /// </summary>
    private static IBrush Paint(string value)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(value.Trim()));
        }
        catch (Exception)
        {
            return Brushes.Transparent;
        }
    }

    /// <summary>
    /// What an accession number will look like with the settings as they stand
    /// on screen — not as they are saved.
    ///
    /// Three separate fields decide the shape of a number that goes on every
    /// label and into a statutory register. Showing the result as it is typed is
    /// the difference between getting it right and printing five hundred labels
    /// to find out.
    /// </summary>
    public string SampleNumber
    {
        get
        {
            var digits = Math.Clamp(PadLength, 1, 12);
            var body = 1234.ToString(new string('0', digits));

            return ShowPrefix && Prefix.Trim().Length > 0 ? Prefix.Trim() + body : body;
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var preferences = await Preferences.ReadAsync(db);

            Organisation = preferences.OrganisationName;
            LibraryName = preferences.LibraryName;
            Motto = preferences.Motto;
            Accent = preferences.AccentColour;
            DarkByDefault = preferences.DefaultTheme != "light";
            CrestInCircle = preferences.CrestInCircle;
            CrestCircleColour = preferences.CrestCircleColour;

            _chosenCrest = null;

            var crestPath = Workspace.CrestPath(preferences.CrestPath);

            Crest = Pictures.Load(crestPath);
            CrestNote = Crest is null
                ? "No crest. The heading of every printed page will show the unit's name alone."
                : Path.GetFileName(crestPath)!;

            Prefix = preferences.AccessionPrefix;
            ShowPrefix = preferences.ShowAccessionPrefix;
            PadLength = preferences.AccessionPadLength;
            StartFrom = preferences.AccessionStartFrom;

            PocketWidth = Millimetres(preferences.Text("barcode.pocket_width_mm"), 51);
            PocketHeight = Millimetres(preferences.Text("barcode.pocket_height_mm"), 25);
            SpineWidth = Millimetres(preferences.Text("barcode.spine_width_mm"), 25);
            SpineHeight = Millimetres(preferences.Text("barcode.spine_height_mm"), 38);
            OneLabelPerPage = preferences.Text("barcode.default_print_mode", "sheet") == "label";
            ChosenCode = CodeChoice.For(preferences.Text("barcode.label_code", "barcode"));
            FinePrinter = preferences.Number("barcode.printer_dpi", MilLib.Core.Documents.Zpl.CommonDpi)
                == MilLib.Core.Documents.Zpl.FineDpi;

            var setup = new Setup(db);
            var accession = new Accession(db, preferences);

            var next = await accession.PeekAsync();

            NextNumber = accession.Display(next);

            StartLocked = !await setup.RegisterIsEmptyAsync();

            LockNote = StartLocked
                ? "Locked. Books are already on the register, and moving the start would "
                  + "hand out numbers that are on a shelf."
                : "Set it before the first book is accessioned. After that it cannot be moved.";

            Features.Clear();

            foreach (var feature in Enum.GetValues<Feature>())
            {
                Features.Add(new FeatureRow(feature, preferences.Has(feature)));
            }

            // Last, and only if everything above it ran.
            _loaded = true;
        }
        catch (Exception ex)
        {
            _loaded = false;

            Faults.Record("reading the settings", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(SampleNumber));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    // ------------------------------------------------------------ branding --

    [RelayCommand]
    private async Task ChooseCrestAsync()
    {
        if (PickCrest is null)
        {
            return;
        }

        var chosen = await PickCrest();

        if (chosen is null)
        {
            return;
        }

        try
        {
            // Copied in beside the data file rather than pointed at where it
            // was found. A crest referred to on somebody's desktop disappears
            // the moment that folder is tidied, and every printed page loses
            // its heading with it.
            Directory.CreateDirectory(Workspace.Pictures);

            var destination = Path.Combine(Workspace.Pictures, "crest" + Path.GetExtension(chosen));

            if (!string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(chosen, destination, overwrite: true);
            }

            _chosenCrest = Path.GetFileName(destination);

            Crest = Pictures.Load(destination);
            CrestNote = Crest is null
                ? "That file could not be read as a picture."
                : Path.GetFileName(destination) + " — not saved yet.";
        }
        catch (Exception ex)
        {
            Faults.Record("copying in the crest", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    [RelayCommand]
    private void RemoveCrest()
    {
        _chosenCrest = "";

        Crest = null;
        CrestNote = "The crest will be removed when this is saved.";
    }

    /// <summary>
    /// The ready-made colours, beside the box for typing one.
    ///
    /// Picking one only fills the box in — it does not save. Somebody trying
    /// four colours to see which suits should be able to, and land on the
    /// fourth, without having written three of them into the database on the
    /// way.
    /// </summary>
    public IReadOnlyList<Palette> Palettes { get; }

    [RelayCommand]
    private async Task SaveBrandingAsync()
    {
        if (Busy || !MayManage)
        {
            return;
        }

        if (!_loaded)
        {
            Announce("The settings have not been read yet, so there is nothing here worth "
                + "saving. Give it a moment, or reopen this screen.", false);

            return;
        }

        if (LibraryName.Trim().Length == 0)
        {
            Announce("The library needs a name — it is the heading of every page it prints.", false);

            return;
        }

        if (Theming.Parse(Accent) is null)
        {
            Announce($"“{Accent}” is not a colour. Pick one of the squares, or type "
                + "a hash and six hex digits — #1f3a5f.", false);

            return;
        }

        await DoAsync("saving the branding", async (db, setup) =>
        {
            await setup.SaveBrandingAsync(new Branding(
                Organisation, LibraryName, Motto, Accent,
                DarkByDefault ? "dark" : "light",
                CrestInCircle, CrestCircleColour, _chosenCrest),
                Session.User!.UserId);

            return "Branding saved. The window has taken the new colour, and it "
                + "appears on every page this library prints.";
        });
    }

    // ----------------------------------------------------------- accession --

    [RelayCommand]
    private async Task SaveAccessionAsync()
    {
        if (Busy || !MayManage)
        {
            return;
        }

        await DoAsync("saving the accession settings", async (db, setup) =>
        {
            var refusal = await setup.SaveAccessionAsync(
                Prefix, ShowPrefix, PadLength, StartFrom, Session.User!.UserId);

            if (refusal is not null)
            {
                throw new Refused(refusal);
            }

            // Read back rather than worked out from the boxes: the counter is
            // the thing that actually decides, and after a save it is what the
            // next book will be given.
            var accession = new Accession(db, await Preferences.ReadAsync(db));

            NextNumber = accession.Display(await accession.PeekAsync());

            return $"Numbering saved. The next copy accessioned will be {NextNumber}.";
        });
    }

    // -------------------------------------------------------------- labels --

    [RelayCommand]
    private async Task SaveLabelsAsync()
    {
        if (Busy || !MayManage)
        {
            return;
        }

        await DoAsync("saving the label sizes", async (db, setup) =>
        {
            await setup.SaveLabelsAsync(
                PocketWidth, PocketHeight, SpineWidth, SpineHeight,
                OneLabelPerPage ? "label" : "sheet", ChosenCode.Value,
                FinePrinter ? MilLib.Core.Documents.Zpl.FineDpi : MilLib.Core.Documents.Zpl.CommonDpi,
                Session.User!.UserId);

            return $"Label sizes saved. Pocket labels are {PocketWidth:0.##} × {PocketHeight:0.##} mm.";
        });
    }

    // ----------------------------------------------------------- features --

    /// <summary>
    /// Turning a screen on or off, saved the moment the switch moves.
    ///
    /// No Save button, because a row of twelve switches with one button under
    /// them invites flicking several and then wondering which took. Each is one
    /// decision and is written as one.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFeatureAsync(FeatureRow? row)
    {
        if (row is null || Busy || !MayManage)
        {
            return;
        }

        if (Setup.NotBuiltYet(row.Feature))
        {
            row.Revert();

            Announce($"{row.Name} has no screen yet in either program — "
                + "turning it on would put an empty item in the menu.", false);

            return;
        }

        await DoAsync($"turning {row.Name} " + (row.On ? "on" : "off"), async (db, setup) =>
        {
            await setup.SetFeatureAsync(row.Feature, row.On, Session.User!.UserId);

            return row.On
                ? $"{row.Name} is on. It appears in the menu at the next sign-in."
                : $"{row.Name} is off. Nothing was deleted — the records it kept are still there, "
                  + "and turning it back on shows them again.";
        });
    }

    // ------------------------------------------------------------ plumbing --

    /// <summary>
    /// One save, done the same way every time: quiet the screen, do it, re-read
    /// the settings into the session so the rest of the application agrees with
    /// what was just written, and say what happened.
    /// </summary>
    private async Task DoAsync(string what, Func<MilLibDbContext, Setup, Task<string>> save)
    {
        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var said = await save(db, new Setup(db));

            // The window title, the crest, the money symbol and every printed
            // heading read from here. Left stale, the application would go on
            // showing the old name until it was restarted.
            Session.Refresh(await Preferences.ReadAsync(db));

            // Recoloured here and now rather than at the next sign-in.
            // Somebody choosing their unit's colour is choosing by eye, and a
            // colour they cannot see until they have signed out and back in is
            // chosen twice — once wrongly, and once after finding out.
            Theming.Apply(Session.Preferences);

            Announce(said, true);
        }
        catch (Refused refused)
        {
            Announce(refused.Message, false);
        }
        catch (Exception ex)
        {
            Faults.Record(what, ex);

            Announce(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;
        }
    }

    private void Announce(string said, bool good)
    {
        Said = said;
        SaidIsGood = good;
    }

    private static double Millimetres(string stored, double fallback) =>
        double.TryParse(stored, out var value) && value >= 5 ? value : fallback;

    /// <summary>A refusal by a rule, as opposed to something going wrong.</summary>
    private sealed class Refused(string why) : Exception(why);
}

/// <summary>
/// One screen this install may or may not have.
///
/// It carries its own switch rather than the view model carrying twelve
/// properties, so adding a feature to the enum adds a row here and nothing
/// else.
/// </summary>
public partial class FeatureRow(Feature feature, bool on) : ObservableObject
{
    [ObservableProperty] private bool _on = on;

    public Feature Feature { get; } = feature;

    public string Name { get; } = Setup.NameOf(feature);

    public string What { get; } = Setup.WhatItDoes(feature);

    public bool Unavailable { get; } = Setup.NotBuiltYet(feature);

    /// <summary>Put the switch back where it was, when the change was refused.</summary>
    public void Revert() => On = !On;
}

/// <summary>
/// What is printed on a pocket label. Stored as one of three words, offered as
/// three phrases — the stored word is not what anybody would pick from a list.
/// </summary>
public record CodeChoice(string Value, string Name)
{
    public static IReadOnlyList<CodeChoice> All { get; } =
    [
        new("barcode", "A barcode"),
        new("qr", "A QR code"),
        new("both", "Both, side by side"),
    ];

    public static CodeChoice For(string value) =>
        All.FirstOrDefault(c => c.Value == value) ?? All[0];

    public override string ToString() => Name;
}
