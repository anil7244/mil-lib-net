using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Everything the library holds, one line per work.
///
/// The counts of copies are read alongside the titles rather than per row: a
/// library of this size is fourteen hundred rows, and asking the database
/// "how many copies, and how many of them are in" once per row is fourteen
/// hundred round trips to draw one screen.
/// </summary>
public partial class BooksViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private BookRow? _selected;

    /// <summary>
    /// The catalogue as a wall of covers rather than a table of rows.
    ///
    /// The same books either way — a switch, not a different screen — because
    /// the two ways of looking answer two questions. The table is for finding a
    /// particular book and reading its numbers; the covers are for seeing the
    /// shelf, and they make the library look like the thing it is. The table is
    /// the one that opens first, since it is the one used at the counter.
    /// </summary>
    [ObservableProperty] private bool _coversView;

    public bool TableView => !CoversView;

    partial void OnCoversViewChanged(bool value) => OnPropertyChanged(nameof(TableView));

    [RelayCommand]
    private void UseTable() => CoversView = false;

    [RelayCommand]
    private void UseCovers() => CoversView = true;

    private List<BookRow> _all = [];

    /// <summary>
    /// Bumped on every keystroke; only the last one is honoured.
    ///
    /// Typing "military history" is sixteen searches if each letter starts one,
    /// and fifteen of them are answers nobody wanted. Waiting a beat after the
    /// typing stops turns it back into one.
    /// </summary>
    private int _keystroke;

    public BooksViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<BookRow> Shown { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool Nothing => !Busy && Shown.Count == 0;

    public string Tally => Busy
        ? "Reading the catalogue…"
        : Shown.Count == _all.Count
            ? $"{_all.Count:N0} books, {_all.Sum(b => b.Copies):N0} copies"
            : $"{Shown.Count:N0} of {_all.Count:N0} books";

    public bool HasSelection => Selected is not null;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSearchChanged(string value) => _ = FilterSoonAsync();

    partial void OnSelectedChanged(BookRow? value) => OnPropertyChanged(nameof(HasSelection));

    public bool MayCatalogue => Session.Can(Ability.CatalogueManage);

    /// <summary>
    /// Raised when a book should be opened in full. The view answers it — a
    /// view model that constructs windows cannot be reasoned about without a
    /// screen in front of it.
    /// </summary>
    public event Func<long, Task>? Open;

    /// <summary>
    /// Raised to catalogue a work: null for a new one, an id to correct an
    /// existing record. Answered with the id that was saved, or null if the
    /// person backed out.
    /// </summary>
    public event Func<long?, Task<long?>>? Catalogue;

    /// <summary>
    /// Catalogue a book that is not in the catalogue yet.
    ///
    /// It describes the work and nothing else — no copy, no accession number,
    /// nothing on the register. Those come afterwards, on the book's own
    /// screen, which is why this lands there once it has saved.
    /// </summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (Catalogue is null)
        {
            return;
        }

        var made = await Catalogue(null);

        if (made is null)
        {
            return;
        }

        await LoadAsync();

        Selected = Shown.FirstOrDefault(b => b.TitleId == made);

        // Straight into the book, because the next thing anybody does with a
        // work they have just catalogued is accession copies of it.
        if (Open is not null)
        {
            await Open(made.Value);

            await LoadAsync();

            Selected = Shown.FirstOrDefault(b => b.TitleId == made);
        }
    }

    /// <summary>Correct the record of a work already in the catalogue.</summary>
    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (Selected is null || Catalogue is null)
        {
            return;
        }

        var keep = Selected.TitleId;

        await Catalogue(keep);

        await LoadAsync();

        Selected = Shown.FirstOrDefault(b => b.TitleId == keep);
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (Selected is null || Open is null)
        {
            return;
        }

        await Open(Selected.TitleId);

        // The copies may have changed while it was open, and the count on this
        // row is one of the things somebody just went in to alter.
        var keep = Selected.TitleId;

        await LoadAsync();

        Selected = Shown.FirstOrDefault(b => b.TitleId == keep);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var titles = await db.Titles
                .OrderBy(t => t.Name)
                .Select(t => new
                {
                    t.TitleId,
                    t.Name,
                    t.Subtitle,
                    t.Language,
                    t.CallNumber,
                    t.MaterialType,
                    t.PubYear,
                    t.IsUnitPublication,
                    t.CoverPath,
                    t.SecurityClass,
                    Publisher = t.Publisher!.Name,
                    Authors = t.Authors.OrderBy(a => a.SortOrder).Select(a => a.Author!.Name).ToList(),
                    Copies = t.Copies.Count(),
                    Available = t.Copies.Count(c => c.Status == CopyStatus.AVAILABLE),
                    Accession = t.Copies.OrderBy(c => c.CopyId).Select(c => c.AccessionNo).FirstOrDefault(),
                })
                .ToListAsync();

            _all =
            [
                .. titles.Select(t => new BookRow(
                    t.TitleId,
                    t.Name,
                    t.Subtitle,
                    t.Authors.Count > 0 ? string.Join(", ", t.Authors) : "",
                    t.Publisher ?? "",
                    t.Language,
                    t.CallNumber ?? "",
                    // Nothing at all when nothing has been accessioned. Run
                    // through the prefix, an empty number came out as a bare
                    // "JAKLI/" — which looks like a number somebody has lost
                    // the rest of rather than like a work with no copies yet.
                    string.IsNullOrEmpty(t.Accession) ? "" : Session.Preferences.Accession(t.Accession),
                    Words.Of(t.MaterialType),
                    t.PubYear,
                    t.IsUnitPublication,
                    t.SecurityClass,
                    t.Copies,
                    t.Available,
                    Workspace.CoverPath(t.CoverPath)))
            ];

            Show(_all);
        }
        catch (Exception ex)
        {
            Faults.Record("reading the catalogue", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Tally));
            OnPropertyChanged(nameof(Nothing));
        }
    }

    private async Task FilterSoonAsync()
    {
        var mine = ++_keystroke;

        await Task.Delay(180);

        if (mine != _keystroke)
        {
            return;
        }

        Filter();
    }

    /// <summary>
    /// Every word has to match something, but not all in the same field.
    ///
    /// Somebody searching "gupta artillery" is remembering an author and a
    /// subject, not a phrase — and a search that only matches when both words
    /// land in the same column finds nothing and looks broken.
    /// </summary>
    private void Filter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            Show(_all);
            return;
        }

        Show([.. _all.Where(b => words.All(b.Mentions))]);
    }

    private void Show(IEnumerable<BookRow> rows)
    {
        Shown.Clear();

        foreach (var row in rows)
        {
            Shown.Add(row);
        }

        OnPropertyChanged(nameof(Tally));
        OnPropertyChanged(nameof(Nothing));
    }
}

/// <summary>One work, as the catalogue list shows it.</summary>
public record BookRow(
    long TitleId,
    string Title,
    string? Subtitle,
    string Authors,
    string Publisher,
    string Language,
    string CallNumber,
    string Accession,
    string Material,
    int? Year,
    bool UnitPublication,
    SecurityClass Classification,
    int Copies,
    int Available,
    string? CoverFile = null)
{
    private Bitmap? _cover;
    private bool _coverLoaded;

    /// <summary>
    /// The cover, loaded the first time the row is drawn — the list virtualises,
    /// so only the dozen on screen ever read a file. Null when the book has none.
    /// </summary>
    public Bitmap? Cover
    {
        get
        {
            if (!_coverLoaded)
            {
                _coverLoaded = true;
                _cover = Pictures.Load(CoverFile);
            }

            return _cover;
        }
    }

    public bool HasCover => Cover is not null;

    /// <summary>What kind of thing it is and when — the subline under the title.</summary>
    public string Kind => string.Join("  ·  ", new[]
    {
        Material,
        Year?.ToString(),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// What sits under the title: what kind of thing it is, who put it out, and
    /// when — the web application's material-and-imprint subline, said in one
    /// line and skipping whatever is not known rather than printing the gaps.
    /// </summary>
    public string Sub => string.Join("  ·  ", new[]
    {
        Material,
        Publisher,
        Year?.ToString(),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string CopiesText => Copies == 1 ? "1 copy" : $"{Copies} copies";

    /// <summary>
    /// How many are on the shelf right now. Said as a fraction rather than a
    /// number, because "2" means nothing without knowing whether it is two of
    /// two or two of eleven.
    /// </summary>
    public string AvailableText => $"{Available} of {Copies} in";

    public bool AllOut => Copies > 0 && Available == 0;

    public bool IsClassified => Classification != SecurityClass.UNCLASSIFIED;

    public string ClassificationText => Words.Of(Classification);

    public bool Mentions(string word) =>
        Title.Contains(word, StringComparison.OrdinalIgnoreCase)
        || (Subtitle?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
        || Authors.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Publisher.Contains(word, StringComparison.OrdinalIgnoreCase)
        || CallNumber.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Accession.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Language.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Material.Contains(word, StringComparison.OrdinalIgnoreCase);
}
