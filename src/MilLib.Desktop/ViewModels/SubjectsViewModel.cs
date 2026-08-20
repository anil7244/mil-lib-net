using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The subject headings — what this library files its books under.
///
/// A small screen and an unusual one: what it edits is not records of anything,
/// it is the vocabulary the catalogue uses to describe itself. A unit library's
/// scheme is a page of headings its librarian invented, so this is deliberately
/// a page of headings and not a thesaurus.
///
/// This install has none at all — the catalogue came from a stock ledger, which
/// carried no subjects — so the empty state matters more here than anywhere
/// else in the application. It is the first thing anybody sees on this screen.
/// </summary>
public partial class SubjectsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private SubjectNode? _selected;

    // ----------------------------------------------------- the one being edited
    [ObservableProperty] private bool _editing;
    [ObservableProperty] private string _editHeading = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ParentChoice _under = ParentChoice.Top;
    [ObservableProperty] private int _position;
    [ObservableProperty] private string _filedHere = "";

    private Category _working = new();

    public SubjectsViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<SubjectNode> Headings { get; } = [];

    public ObservableCollection<ParentChoice> Parents { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> Books { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.CatalogueManage);

    public bool Nothing => !Busy && Headings.Count == 0;

    public bool HasBooks => Books.Count > 0;

    /// <summary>
    /// Whether there is anything to say about what is filed here. There is not,
    /// for a heading that does not exist yet — and a section heading with
    /// nothing under it reads as something that failed to load.
    /// </summary>
    public bool HasFiling => FiledHere.Length > 0;

    public bool IsNew => _working.CategoryId == 0;

    /// <summary>Only an existing heading can be removed, and only when nothing is under it.</summary>
    public bool MayRemove => MayManage && Editing && !IsNew;

    public string Tally
    {
        get
        {
            if (Busy)
            {
                return "Reading the headings…";
            }

            if (Headings.Count == 0)
            {
                return "No subject headings yet";
            }

            var filed = Headings.Sum(h => h.Titles);

            return $"{Headings.Count:N0} heading{(Headings.Count == 1 ? "" : "s")} · "
                + $"{filed:N0} book{(filed == 1 ? "" : "s")} filed";
        }
    }

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnEditingChanged(bool value) => OnPropertyChanged(nameof(MayRemove));

    partial void OnFiledHereChanged(string value) => OnPropertyChanged(nameof(HasFiling));

    partial void OnSelectedChanged(SubjectNode? value)
    {
        if (value is not null)
        {
            _ = OpenAsync(value);
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var subjects = new Subjects(db);

            var tree = await subjects.TreeAsync();

            Headings.Clear();

            foreach (var node in tree)
            {
                Headings.Add(node);
            }

            // A heading the walk could not reach is one caught in a ring, and
            // it would otherwise simply not be on the screen with nothing said.
            var total = await subjects.CountAsync();

            Problem = total > tree.Count
                ? $"{total - tree.Count} heading{(total - tree.Count == 1 ? " is" : "s are")} filed under "
                  + "each other in a ring and cannot be reached from the top. They are not shown here. "
                  + "This needs putting right in the database."
                : "";
        }
        catch (Exception ex)
        {
            Faults.Record("reading the subject headings", ex);

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
    private async Task RefreshAsync()
    {
        var keep = Selected?.Id;

        await LoadAsync();

        Selected = Headings.FirstOrDefault(h => h.Id == keep);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        // Under whatever is picked, which is nearly always what somebody wants:
        // headings are added while looking at the one they belong beneath.
        var under = Selected?.Id;

        Selected = null;

        _working = new Category { ParentId = under };

        Name = "";
        Position = 0;
        Problems.Clear();
        Books.Clear();
        EditHeading = "A new heading";
        FiledHere = "";

        await OfferParentsAsync(0, under);

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(IsNew));

        // Raised here rather than left to Editing, which is very often already
        // true — somebody adds a heading while looking at the one it belongs
        // under — so setting it again raises nothing and "Remove" stays on the
        // form from the heading that was showing a moment ago.
        OnPropertyChanged(nameof(MayRemove));

        Editing = true;
    }

    private async Task OpenAsync(SubjectNode node)
    {
        _working = node.Heading;

        Name = node.Name;
        Position = node.Heading.SortOrder;
        EditHeading = node.Name;

        Problems.Clear();
        Books.Clear();

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(MayRemove));

        Editing = true;

        try
        {
            await using var db = Workspace.Open();

            var subjects = new Subjects(db);

            await OfferParentsAsync(node.Id, node.Heading.ParentId);

            foreach (var (_, title) in await subjects.FiledUnderAsync(node.Id))
            {
                Books.Add(title);
            }

            FiledHere = node.Titles switch
            {
                0 when node.Below == 0 => "Nothing is filed under this heading.",
                0 => $"Nothing directly, but {node.Below:N0} book{(node.Below == 1 ? "" : "s")} "
                     + "below it.",
                var n when node.Below == 0 => $"{n:N0} book{(n == 1 ? "" : "s")} filed here.",
                var n => $"{n:N0} book{(n == 1 ? "" : "s")} here, {node.Below:N0} below.",
            };
        }
        catch (Exception ex)
        {
            Faults.Record("reading what is under a heading", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            OnPropertyChanged(nameof(HasBooks));
            OnPropertyChanged(nameof(MayRemove));
        }
    }

    /// <summary>
    /// The headings this one may be filed under.
    ///
    /// Asked of the tree rather than offered as "all of them", so a move that
    /// would put a heading beneath itself is not on the list to be chosen. The
    /// guard is in <see cref="Subjects"/> as well; this is so nobody meets it.
    /// </summary>
    private async Task OfferParentsAsync(long headingId, long? current)
    {
        await using var db = Workspace.Open();

        Parents.Clear();
        Parents.Add(ParentChoice.Top);

        foreach (var node in await new Subjects(db).MayLiveUnderAsync(headingId))
        {
            Parents.Add(new ParentChoice(node.Id, new string(' ', node.Depth * 4) + node.Name));
        }

        // Cleared first, deliberately. The wanted value is very often the one
        // the property already holds — "a top-level heading" for nearly every
        // new one — and assigning a value equal to the one already there
        // raises nothing, so the box that was just emptied by rebuilding the
        // list would stay empty.
        Under = null!;
        Under = Parents.FirstOrDefault(p => p.Id == current) ?? ParentChoice.Top;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Busy || !MayManage)
        {
            return;
        }

        Busy = true;
        Problems.Clear();

        try
        {
            await using var db = Workspace.Open();

            var subjects = new Subjects(db);

            _working.Name = Name.Trim();
            _working.ParentId = Under.Id;
            _working.SortOrder = Math.Max(0, Position);

            var problems = await subjects.ProblemsWithAsync(_working);

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    Problems.Add(problem);
                }

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            var made = IsNew;

            await subjects.SaveAsync(_working, Session.User!.UserId);

            Announce(made
                ? $"{_working.Name} added. It can be ticked on any book's record from now on."
                : $"{_working.Name} saved.", true);

            Editing = false;
        }
        catch (Exception ex)
        {
            Faults.Record("saving a subject heading", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;

            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Busy || IsNew || !MayManage)
        {
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var heading = await db.Categories.FirstAsync(c => c.CategoryId == _working.CategoryId);

            var subjects = new Subjects(db);

            var why = await subjects.WhyNotRemovableAsync(heading.CategoryId);

            if (why is not null)
            {
                Announce(why, false);

                return;
            }

            await subjects.RemoveAsync(heading, Session.User!.UserId);

            Announce($"{heading.Name} removed. No book lost anything — nothing was filed under it.", true);

            Editing = false;
            Selected = null;
        }
        catch (Exception ex)
        {
            Faults.Record("removing a subject heading", ex);

            Announce(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;

            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Editing = false;
        Problems.Clear();

        OnPropertyChanged(nameof(HasProblems));
    }

    private void Announce(string said, bool good)
    {
        Said = said;
        SaidIsGood = good;
    }
}

/// <summary>
/// A heading offered as somewhere to file another one under, indented so the
/// shape of the tree survives being put in a flat dropdown. "Nothing" is a row
/// rather than a null so the list never shows an empty first entry.
/// </summary>
public record ParentChoice(long? Id, string Name)
{
    public static ParentChoice Top { get; } = new(null, "— a top-level heading —");

    public override string ToString() => Name;
}
