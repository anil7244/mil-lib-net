using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// One work, and every physical copy of it.
///
/// The two halves of the two-level model, on one screen: the bibliographic
/// record above, and beneath it the register of objects — each with its own
/// accession number, its own condition, and its own history. Nothing here shows
/// a quantity, because how many there are is a question answered by counting
/// the copies.
/// </summary>
public partial class BookViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // -------------------------------------------------------------- the work
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _authors = "";
    [ObservableProperty] private string _imprint = "";
    [ObservableProperty] private string _physical = "";
    [ObservableProperty] private string _shelfMark = "";
    [ObservableProperty] private string _subjects = "";
    [ObservableProperty] private string _about = "";
    [ObservableProperty] private string _classification = "";
    [ObservableProperty] private bool _isClassified;
    [ObservableProperty] private bool _isUnitPublication;

    /// <summary>
    /// Which amendment this unit publication is current to — "Amendment 3,
    /// dated 12 Mar 2026". A controlled publication that has been amended and a
    /// pristine one look identical on the shelf; the register is where the
    /// difference is kept, so it is said beside the mark that it is one.
    /// </summary>
    [ObservableProperty] private string _amendment = "";
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private string _holdings = "";

    // ------------------------------------------------------- accessioning
    [ObservableProperty] private bool _accessioning;
    [ObservableProperty] private int _quantity = 1;
    [ObservableProperty] private string _nextNumber = "";
    // DateTime, not DateTimeOffset. CalendarDatePicker.SelectedDate is
    // DateTime? and a two-way binding to anything else throws a cast at run
    // time — which Avalonia then prints, in full, where the field should be.
    [ObservableProperty] private DateTime? _accessionDate = DateTime.Today;
    [ObservableProperty] private CopySource _source = CopySource.PURCHASE;
    [ObservableProperty] private CopyCondition _condition = CopyCondition.NEW;
    [ObservableProperty] private string _supplier = "";
    [ObservableProperty] private string _billNo = "";
    [ObservableProperty] private string _cost = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private bool _circulating = true;

    // ------------------------------------------------------- one copy
    [ObservableProperty] private CopyRow? _selected;
    [ObservableProperty] private CopyStatus _copyStatus = CopyStatus.AVAILABLE;
    [ObservableProperty] private CopyCondition _copyCondition = CopyCondition.GOOD;
    [ObservableProperty] private string _copyLocation = "";
    [ObservableProperty] private bool _copyCirculating = true;
    [ObservableProperty] private string _newNote = "";

    private readonly long _titleId;

    public BookViewModel(long titleId)
    {
        _titleId = titleId;

        _ = LoadAsync();
    }

    public ObservableCollection<CopyRow> Copies { get; } = [];

    public ObservableCollection<NoteRow> Notes { get; } = [];

    public CopySource[] Sources { get; } = Enum.GetValues<CopySource>();

    public CopyCondition[] Conditions { get; } = Enum.GetValues<CopyCondition>();

    public CopyStatus[] Statuses { get; } = Enum.GetValues<CopyStatus>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool HasSubtitle => Subtitle.Length > 0;

    public bool HasSubjects => Subjects.Length > 0;

    /// <summary>Notes appended to the copy that is picked.</summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>The cataloguer's own note on the work.</summary>
    public bool HasAbout => About.Length > 0;

    public bool HasAmendment => Amendment.Length > 0;

    public bool MayManage => Session.Can(Ability.CatalogueManage);

    public bool HasSelection => Selected is not null;

    public bool NoCopies => !Busy && Copies.Count == 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));

    partial void OnSubjectsChanged(string value) => OnPropertyChanged(nameof(HasSubjects));

    partial void OnAboutChanged(string value) => OnPropertyChanged(nameof(HasAbout));

    partial void OnAmendmentChanged(string value) => OnPropertyChanged(nameof(HasAmendment));

    partial void OnSelectedChanged(CopyRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        if (value is null)
        {
            return;
        }

        CopyStatus = value.Status;
        CopyCondition = value.Condition;
        CopyLocation = value.Location;
        CopyCirculating = value.Circulating;
        NewNote = "";

        _ = LoadNotesAsync(value.CopyId);
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var book = await new Catalogue(db).ReadAsync(_titleId);

            if (book is null)
            {
                Problem = "That book is no longer in the catalogue.";
                return;
            }

            var title = book.Title;

            TitleText = title.Name;
            Subtitle = title.Subtitle ?? "";

            Authors = book.Authors.Count > 0
                ? string.Join(", ", book.Authors.Select(a => a.Display))
                : "";

            // The imprint the way a catalogue card writes it: who published it,
            // where, and when — omitting whichever of those is not known rather
            // than printing empty punctuation around it.
            Imprint = string.Join(", ", new[]
            {
                book.Publisher?.Name,
                title.PubPlace,
                title.PubYear?.ToString(),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            Physical = string.Join("  ·  ", new[]
            {
                Words.Of(title.MaterialType),
                title.Language,
                title.Edition,
                title.Pages,
                string.IsNullOrWhiteSpace(title.Isbn) ? null : "ISBN " + title.Isbn,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            ShelfMark = string.Join("  ·  ", new[]
            {
                string.IsNullOrWhiteSpace(title.CallNumber) ? null : "Call " + title.CallNumber,
                string.IsNullOrWhiteSpace(title.ClassificationNo)
                    ? null
                    : $"{title.ClassificationSch} {title.ClassificationNo}",
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            Subjects = book.Subjects.Count > 0
                ? string.Join(", ", book.Subjects.Select(s => s.Name))
                : title.SubjectHeadings ?? "";

            About = title.Notes ?? "";
            Classification = Words.Of(title.SecurityClass);
            IsClassified = title.SecurityClass.IsClassified();
            IsUnitPublication = title.IsUnitPublication;

            Amendment = title.IsUnitPublication && !string.IsNullOrWhiteSpace(title.AmendmentNo)
                ? $"Amendment {title.AmendmentNo}"
                    + (title.AmendmentDate is { } on ? $", dated {on:dd MMM yyyy}" : "")
                : "";

            Cover = Pictures.Load(Workspace.CoverPath(title.CoverPath));

            Copies.Clear();

            var today = DateOnly.FromDateTime(DateTime.Today);

            foreach (var (copy, branch, loan, holder) in book.Copies)
            {
                Copies.Add(new CopyRow(
                    copy.CopyId,
                    Session.Preferences.Accession(copy.AccessionNo),
                    copy.Barcode,
                    copy.Status,
                    copy.Condition,
                    copy.Location ?? "",
                    copy.IsCirculating,
                    copy.AccessionDate,
                    copy.Source,
                    branch?.Name ?? "",
                    holder?.Display ?? "",
                    loan?.DueOn,
                    loan is not null && loan.DueOn < today));
            }

            var available = Copies.Count(c => c.Status == CopyStatus.AVAILABLE);
            var out_ = Copies.Count(c => c.Status == CopyStatus.ISSUED);

            Holdings = Copies.Count switch
            {
                0 => "No copies accessioned yet",
                1 => $"1 copy · {available} on the shelf",
                _ => $"{Copies.Count} copies · {available} on the shelf"
                     + (out_ > 0 ? $" · {out_} out" : ""),
            };

            var accession = new Accession(db, Session.Preferences);

            NextNumber = accession.Display(await accession.PeekAsync());
        }
        catch (Exception ex)
        {
            Faults.Record("reading a book", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(NoCopies));
        }
    }

    private async Task LoadNotesAsync(long copyId)
    {
        Notes.Clear();

        try
        {
            await using var db = Workspace.Open();

            foreach (var (note, by) in await new Catalogue(db).NotesOnAsync(copyId))
            {
                Notes.Add(new NoteRow(note.Note, by, note.CreatedAt));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the notes on a copy", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasNotes));
        }
    }

    // ============================================================= the record

    /// <summary>
    /// Raised when the bibliographic record should be corrected. The window
    /// answers it, and this screen re-reads itself afterwards so the heading
    /// shows what was just changed.
    /// </summary>
    public event Func<long, Task>? EditRecord;

    [RelayCommand]
    private async Task EditRecordAsync()
    {
        if (EditRecord is null || Busy)
        {
            return;
        }

        await EditRecord(_titleId);

        await LoadAsync();
    }

    // ============================================================ accessioning

    [RelayCommand]
    private void StartAccessioning()
    {
        Accessioning = true;
        Said = "";
    }

    [RelayCommand]
    private void StopAccessioning() => Accessioning = false;

    /// <summary>
    /// Take copies onto the register.
    ///
    /// Every field here is shared by the whole batch, because a box of eight
    /// identical books arrives on one bill on one day — which is exactly when
    /// somebody wants to accession eight at once rather than eight times.
    /// </summary>
    [RelayCommand]
    private async Task AccessionAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var pattern = new Copy
            {
                AccessionDate = DateOnly.FromDateTime((AccessionDate ?? DateTime.Today).Date),
                Source = Source,
                Condition = Condition,
                Supplier = Blank(Supplier),
                BillNo = Blank(BillNo),
                Location = Blank(Location),
                IsCirculating = Circulating,
                Cost = decimal.TryParse(Cost, out var cost) && cost > 0 ? cost : null,
            };

            var made = await new Accession(db, Session.Preferences)
                .AccessionAsync(_titleId, Quantity, pattern, Session.User!.UserId);

            var first = Session.Preferences.Accession(made[0].AccessionNo);
            var last = Session.Preferences.Accession(made[^1].AccessionNo);

            Said = made.Count == 1
                ? $"Accessioned as {first}."
                : $"{made.Count} copies accessioned, {first} to {last}.";

            SaidIsGood = true;
            Accessioning = false;
            Quantity = 1;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("accessioning copies", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    // ================================================================ one copy

    [RelayCommand]
    private async Task SaveCopyAsync()
    {
        if (Selected is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var copy = await db.Copies.FirstAsync(c => c.CopyId == Selected.CopyId);

            var catalogue = new Catalogue(db);

            var why = await catalogue.WhyNotAsync(copy, CopyStatus);

            if (why is not null)
            {
                Said = why;
                SaidIsGood = false;

                return;
            }

            await catalogue.ReviseCopyAsync(copy, CopyStatus, CopyCondition,
                CopyLocation, CopyCirculating, Session.User!.UserId);

            Said = $"{Session.Preferences.Accession(copy.AccessionNo)} updated.";
            SaidIsGood = true;

            var keep = Selected.CopyId;

            await LoadAsync();

            Selected = Copies.FirstOrDefault(c => c.CopyId == keep);
        }
        catch (Exception ex)
        {
            Faults.Record("updating a copy", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (Selected is null || NewNote.Trim().Length == 0 || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var copy = await db.Copies.FirstAsync(c => c.CopyId == Selected.CopyId);

            await new Catalogue(db).AnnotateAsync(copy, NewNote, Session.User!.UserId);

            NewNote = "";

            Said = "Note added. It cannot be edited or removed — that is what makes it worth having.";
            SaidIsGood = true;

            await LoadNotesAsync(Selected.CopyId);
        }
        catch (Exception ex)
        {
            Faults.Record("annotating a copy", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>One physical copy, as the register lists it.</summary>
public record CopyRow(
    long CopyId, string Accession, string Barcode, CopyStatus Status, CopyCondition Condition,
    string Location, bool Circulating, DateOnly AccessionedOn, CopySource Source,
    string Branch, string HeldBy, DateOnly? Due, bool Overdue)
{
    public string StatusText => Words.Of(Status);

    public string ConditionText => Words.Of(Condition);

    public bool IsAvailable => Status == CopyStatus.AVAILABLE;

    public bool IsOut => HeldBy.Length > 0;

    public string Whereabouts => IsOut
        ? Overdue
            ? $"{HeldBy} — overdue since {Due:dd MMM yyyy}"
            : $"{HeldBy} — due {Due:dd MMM yyyy}"
        : string.Join("  ·  ", new[] { Location, Branch }.Where(s => s.Length > 0));

    public string Acquired => $"{Words.Of(Source)}, {AccessionedOn:dd MMM yyyy}";

    /// <summary>A reference copy is worth saying so on its row — it is why an issue will stop.</summary>
    public bool IsReference => !Circulating;
}

/// <summary>A note somebody appended against a copy.</summary>
public record NoteRow(string Note, string By, DateTime When)
{
    public string Attribution => $"{By} · {When:dd MMM yyyy}";
}
