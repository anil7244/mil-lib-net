using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Cataloguing a book — describing the work, before any copy of it exists.
///
/// The order of this form is the order somebody works in with the book open in
/// front of them: the title page first, then the imprint, then where it will
/// stand on the shelf, then what it is about. Almost every field is optional,
/// because a real catalogue is messy and a form that insists on an ISBN cannot
/// catalogue a 1961 précis.
///
/// Nothing here creates a copy. A title saved from this window has no accession
/// number and is on no register: it becomes physical on the book's own screen,
/// where copies are accessioned. That separation is the whole data model, and
/// the form says so rather than leaving it to be discovered.
/// </summary>
public partial class TitleEditViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _heading = "A new book";

    // --------------------------------------------------------- title page --
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _statementOfResp = "";
    [ObservableProperty] private string _edition = "";

    // ------------------------------------------------------------ imprint --
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _pubPlace = "";
    [ObservableProperty] private string _pubYear = "";
    [ObservableProperty] private string _isbn = "";
    [ObservableProperty] private string _pages = "";
    [ObservableProperty] private string _language = "English";

    // ------------------------------------------------------- on the shelf --
    [ObservableProperty] private string _classificationNo = "";
    [ObservableProperty] private ClassificationScheme _scheme = ClassificationScheme.DDC;
    [ObservableProperty] private string _callNumber = "";
    [ObservableProperty] private MaterialType _material = MaterialType.BOOK;
    [ObservableProperty] private SecurityClass _clearance = SecurityClass.UNCLASSIFIED;
    [ObservableProperty] private bool _isUnitPublication;

    // ---------------------------------------------------------- what it is --
    [ObservableProperty] private string _subjectHeadings = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _amendmentNo = "";
    // DateTime, not DateTimeOffset — CalendarDatePicker.SelectedDate is
    // DateTime?, and binding anything else to it throws where the field should be.
    [ObservableProperty] private DateTime? _amendmentDate;

    // --------------------------------------------------------------- cover --
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private string _coverNote = "";

    /// <summary>Null until somebody picks one, so a save leaves the old cover alone.</summary>
    private string? _chosenCover;

    private readonly long? _titleId;
    private Title _working = Cataloguing.Fresh();

    public TitleEditViewModel(long? titleId = null)
    {
        _titleId = titleId;

        _ = LoadAsync();
    }

    public ObservableCollection<AuthorRow> Authors { get; } = [];

    public ObservableCollection<SubjectRow> Subjects { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> KnownPublishers { get; } = [];

    public IReadOnlyList<string> KnownLanguages => Cataloguing.Languages;

    public ClassificationScheme[] Schemes { get; } = Enum.GetValues<ClassificationScheme>();

    public MaterialType[] Materials { get; } = Enum.GetValues<MaterialType>();

    public SecurityClass[] Clearances { get; } = Enum.GetValues<SecurityClass>();

    public AuthorRole[] Roles { get; } = Enum.GetValues<AuthorRole>();

    public bool HasProblems => Problems.Count > 0;

    public bool IsNew => _titleId is null;

    /// <summary>
    /// Whether this catalogue has any subject headings at all.
    ///
    /// This library's were imported from a stock ledger, which carried titles
    /// and copies and nothing else — so the list is empty on a real install,
    /// and an empty box with no explanation reads as a fault. The free-text
    /// line beneath it works regardless.
    /// </summary>
    public bool HasSubjects => Subjects.Count > 0;

    /// <summary>
    /// Only somebody whose own clearance reaches it may put a book at that
    /// classification. Otherwise a book could be marked Secret by somebody who
    /// would then be refused sight of the record they had just made.
    /// </summary>
    public IReadOnlyList<SecurityClass> MayClassifyAs =>
        [.. Clearances.Where(c => c <= (Session.User?.ClearanceLevel ?? SecurityClass.UNCLASSIFIED))];

    /// <summary>
    /// The record read back as a catalogue card would print it, under the
    /// fields that make it. Half a dozen boxes are much easier to check as one
    /// line than as half a dozen boxes.
    /// </summary>
    public string AsACard
    {
        get
        {
            var who = string.Join("; ", Authors
                .Where(a => a.Name.Trim().Length > 0)
                .Select(a => a.Spoken));

            var what = Name.Trim().Length == 0 ? "Untitled" : Name.Trim();

            if (Subtitle.Trim().Length > 0)
            {
                what += ": " + Subtitle.Trim();
            }

            var imprint = string.Join(", ", new[]
            {
                PubPlace.Trim(),
                Publisher.Trim(),
                PubYear.Trim(),
            }.Where(part => part.Length > 0));

            var card = who.Length > 0 ? $"{who}. {what}" : what;

            if (Edition.Trim().Length > 0)
            {
                card += $". {Edition.Trim()}";
            }

            if (imprint.Length > 0)
            {
                card += $". {imprint}";
            }

            if (Pages.Trim().Length > 0)
            {
                card += $". {Pages.Trim()}";
            }

            return card + ".";
        }
    }

    /// <summary>Raised when the record is saved. The list reloads and the window closes.</summary>
    public event Action<long>? Saved;

    /// <summary>Raised when the person backs out.</summary>
    public event Action? Abandoned;

    /// <summary>Raised when a cover needs choosing — the view answers it.</summary>
    public event Func<Task<string?>>? PickCover;

    partial void OnNameChanged(string value) => Card();

    partial void OnSubtitleChanged(string value) => Card();

    partial void OnEditionChanged(string value) => Card();

    partial void OnPublisherChanged(string value) => Card();

    partial void OnPubPlaceChanged(string value) => Card();

    partial void OnPubYearChanged(string value) => Card();

    partial void OnPagesChanged(string value) => Card();

    private void Card() => OnPropertyChanged(nameof(AsACard));

    private async Task LoadAsync()
    {
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var cataloguing = new Cataloguing(db);

            KnownPublishers.Clear();

            foreach (var name in await cataloguing.PublishersAsync())
            {
                KnownPublishers.Add(name);
            }

            var chosen = new HashSet<long>();

            if (_titleId is { } id)
            {
                _working = await db.Titles.FirstAsync(t => t.TitleId == id);

                Show(_working);

                Publisher = _working.PublisherId is null
                    ? ""
                    : await db.Publishers
                        .Where(p => p.PublisherId == _working.PublisherId)
                        .Select(p => p.Name)
                        .FirstOrDefaultAsync() ?? "";

                Authors.Clear();

                foreach (var entry in await cataloguing.AuthorsOnAsync(id))
                {
                    Authors.Add(Watched(new AuthorRow(entry)));
                }

                chosen = [.. await cataloguing.SubjectsOnAsync(id)];

                Heading = _working.Name;
            }
            else
            {
                _working = Cataloguing.Fresh();

                Heading = "A new book";
            }

            Subjects.Clear();

            // Taken from the tree rather than as a flat list, and indented by
            // its depth, so the list of headings to tick reads the same way as
            // the Subjects screen it came from. A narrow heading is much
            // easier to pick correctly when the broad one above it is visible.
            foreach (var node in await new Subjects(db).TreeAsync())
            {
                Subjects.Add(new SubjectRow(node.Heading, chosen.Contains(node.Id), node.Depth));
            }

            OnPropertyChanged(nameof(HasSubjects));

            // Always one empty line to type into. A form that makes you press
            // "add" before you can type a single author is a form that asks a
            // question before letting you answer it.
            if (Authors.Count == 0)
            {
                Authors.Add(Watched(new AuthorRow(new AuthorEntry("", null, AuthorRole.AUTHOR))));
            }

            ShowCover(Workspace.CoverPath(_working.CoverPath), saved: true);
        }
        catch (Exception ex)
        {
            Faults.Record("opening the cataloguing form", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;

            Card();
        }
    }

    private void Show(Title title)
    {
        Name = title.Name;
        Subtitle = title.Subtitle ?? "";
        StatementOfResp = title.StatementOfResp ?? "";
        Edition = title.Edition ?? "";
        PubPlace = title.PubPlace ?? "";
        PubYear = title.PubYear?.ToString() ?? "";
        Isbn = title.Isbn ?? "";
        Pages = title.Pages ?? "";
        Language = title.Language;
        ClassificationNo = title.ClassificationNo ?? "";
        Scheme = title.ClassificationSch;
        CallNumber = title.CallNumber ?? "";
        Material = title.MaterialType;
        Clearance = title.SecurityClass;
        IsUnitPublication = title.IsUnitPublication;
        SubjectHeadings = title.SubjectHeadings ?? "";
        Notes = title.Notes ?? "";
        AmendmentNo = title.AmendmentNo ?? "";
        AmendmentDate = title.AmendmentDate?.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>
    /// A row that tells the card line above it when it changes, so the reading
    /// back is of what is on screen rather than of what was on screen when the
    /// form opened.
    /// </summary>
    private AuthorRow Watched(AuthorRow row)
    {
        row.PropertyChanged += (_, _) => Card();

        return row;
    }

    [RelayCommand]
    private void AddAuthor() =>
        Authors.Add(Watched(new AuthorRow(new AuthorEntry("", null, AuthorRole.AUTHOR))));

    [RelayCommand]
    private void RemoveAuthor(AuthorRow? row)
    {
        if (row is not null)
        {
            Authors.Remove(row);
        }

        // Never none. With the last line gone there is nowhere to type, and the
        // only way back is to find the button that adds one.
        if (Authors.Count == 0)
        {
            AddAuthor();
        }

        Card();
    }

    [RelayCommand]
    private async Task ChooseCoverAsync()
    {
        if (PickCover is null)
        {
            return;
        }

        var chosen = await PickCover();

        if (chosen is null)
        {
            return;
        }

        try
        {
            // Copied in beside the data file rather than pointed at where it
            // was found. A cover referred to on somebody's desktop disappears
            // the moment that folder is tidied.
            var folder = Path.Combine(Workspace.Pictures, "covers");

            Directory.CreateDirectory(folder);

            var name = $"cover-{DateTime.Now:yyyyMMdd-HHmmssfff}{Path.GetExtension(chosen)}";
            var destination = Path.Combine(folder, name);

            File.Copy(chosen, destination, overwrite: true);

            _chosenCover = "covers/" + name;

            ShowCover(destination, saved: false);
        }
        catch (Exception ex)
        {
            Faults.Record("copying in the cover", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
    }

    [RelayCommand]
    private void RemoveCover()
    {
        _chosenCover = "";

        Cover = null;
        CoverNote = "The cover will be removed when this is saved.";
    }

    private void ShowCover(string? path, bool saved)
    {
        Cover = Pictures.Load(path);

        CoverNote = Cover is null
            ? "No cover. The catalogue shows the title alone, which is what most of it does."
            : Path.GetFileName(path) + (saved ? "" : " — not saved yet.");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Problems.Clear();

        try
        {
            await using var db = Workspace.Open();

            var cataloguing = new Cataloguing(db);

            var year = int.TryParse(PubYear.Trim(), out var parsed) ? parsed : (int?)null;

            if (PubYear.Trim().Length > 0 && year is null)
            {
                Problems.Add($"\"{PubYear.Trim()}\" is not a year. Leave it empty if it is not known.");

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            _working.Name = Name.Trim();
            _working.Subtitle = Or(Subtitle);
            _working.StatementOfResp = Or(StatementOfResp);
            _working.Edition = Or(Edition);
            _working.PubPlace = Or(PubPlace);
            _working.PubYear = year;
            _working.Isbn = Or(Isbn);
            _working.Pages = Or(Pages);
            _working.Language = Language.Trim().Length == 0 ? "English" : Language.Trim();
            _working.ClassificationNo = Or(ClassificationNo);
            _working.ClassificationSch = Scheme;
            _working.CallNumber = Or(CallNumber);
            _working.MaterialType = Material;
            _working.SecurityClass = Clearance;
            _working.IsUnitPublication = IsUnitPublication;
            _working.SubjectHeadings = Or(SubjectHeadings);
            _working.Notes = Or(Notes);
            _working.AmendmentNo = Or(AmendmentNo);
            _working.AmendmentDate = AmendmentDate is { } on ? DateOnly.FromDateTime(on) : null;

            if (_chosenCover is not null)
            {
                _working.CoverPath = _chosenCover.Length == 0 ? null : _chosenCover;
            }

            var problems = await cataloguing.ProblemsWithAsync(_working, year);

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    Problems.Add(problem);
                }

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            var authors = Authors
                .Where(a => a.Name.Trim().Length > 0)
                .Select(a => new AuthorEntry(a.Name, a.Rank, a.Role))
                .ToList();

            var subjects = Subjects.Where(s => s.Chosen).Select(s => s.Id).ToList();

            var id = await cataloguing.SaveAsync(
                _working, Publisher, authors, subjects, Session.User!.UserId);

            Saved?.Invoke(id);
        }
        catch (Exception ex)
        {
            Faults.Record("cataloguing a book", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Abandoned?.Invoke();

    private static string? Or(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One person on the title, while the form is open.</summary>
public partial class AuthorRow : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _rank;
    [ObservableProperty] private AuthorRole _role;

    public AuthorRow(AuthorEntry entry)
    {
        _name = entry.Name;
        _rank = entry.Rank ?? "";
        _role = entry.Role;
    }

    public AuthorRole[] Roles { get; } = Enum.GetValues<AuthorRole>();

    /// <summary>Rank and name together, as the catalogue card prints them.</summary>
    public string Spoken
    {
        get
        {
            var who = Rank.Trim().Length > 0 ? $"{Rank.Trim()} {Name.Trim()}" : Name.Trim();

            return Role == AuthorRole.AUTHOR ? who : $"{who} ({Words.Of(Role).ToLowerInvariant()})";
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Spoken));

    partial void OnRankChanged(string value) => OnPropertyChanged(nameof(Spoken));

    partial void OnRoleChanged(AuthorRole value) => OnPropertyChanged(nameof(Spoken));
}

/// <summary>One subject heading, and whether this book is under it.</summary>
public partial class SubjectRow(Category category, bool chosen, int depth = 0) : ObservableObject
{
    [ObservableProperty] private bool _chosen = chosen;

    public long Id { get; } = category.CategoryId;

    public string Name { get; } = category.Name;

    /// <summary>How far in it is drawn, so the tick list keeps the tree's shape.</summary>
    public Avalonia.Thickness Inset { get; } = new(depth * 22, 3, 0, 3);
}
