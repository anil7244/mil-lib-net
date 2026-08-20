using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Who the library lends to.
///
/// A list on the left and one person on the right, because the questions asked
/// of this screen are almost always about one member — what they hold, what
/// they owe, whether they can be signed off — and answering them should not
/// mean leaving the list and coming back.
/// </summary>
public partial class MembersViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private MemberRow? _selected;
    [ObservableProperty] private bool _showClosed;

    // ------------------------------------------------------------- the person
    [ObservableProperty] private string _who = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _rules = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _contact = "";
    [ObservableProperty] private string _enrolled = "";

    /// <summary>
    /// The face on the pass. A library that issues photo passes knows its
    /// members by sight as much as by number, and the person at the counter is
    /// the check the photo is for — so it belongs on the screen the counter has
    /// open, not only on the printed card.
    /// </summary>
    [ObservableProperty] private Bitmap? _photo;

    /// <summary>Money lodged against the pass, held until the member is signed off.</summary>
    [ObservableProperty] private string _deposit = "";

    /// <summary>Whatever was written on the member's record by hand.</summary>
    [ObservableProperty] private string _remarks = "";

    [ObservableProperty] private string _standing = "";
    [ObservableProperty] private bool _standingIsPlain;
    [ObservableProperty] private string _clearance = "";
    [ObservableProperty] private bool _isClassified;
    [ObservableProperty] private string _owed = "";
    [ObservableProperty] private bool _owesAnything;
    [ObservableProperty] private string _clearanceVerdict = "";
    [ObservableProperty] private bool _mayBeCleared;
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    private List<MemberRow> _all = [];
    private int _keystroke;

    public MembersViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<MemberRow> Shown { get; } = [];

    public ObservableCollection<HoldingRow> Holding { get; } = [];

    public ObservableCollection<OwedRow> Owing { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool Nothing => !Busy && Shown.Count == 0;

    public bool HasSelection => Selected is not null;

    public bool MayManage => Session.Can(Ability.MembersManage);

    public bool NothingHeld => HasSelection && Holding.Count == 0;

    public bool HasPhoto => Photo is not null;

    public bool HasDeposit => Deposit.Length > 0;

    public bool HasRemarks => Remarks.Length > 0;

    public string Tally => Busy
        ? "Reading the roll…"
        : Shown.Count == _all.Count
            ? $"{_all.Count:N0} member{(_all.Count == 1 ? "" : "s")}"
            : $"{Shown.Count:N0} of {_all.Count:N0} member{(_all.Count == 1 ? "" : "s")}";

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnPhotoChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPhoto));

    partial void OnDepositChanged(string value) => OnPropertyChanged(nameof(HasDeposit));

    partial void OnRemarksChanged(string value) => OnPropertyChanged(nameof(HasRemarks));

    partial void OnSearchChanged(string value) => _ = FilterSoonAsync();

    partial void OnShowClosedChanged(bool value) => Filter();

    partial void OnSelectedChanged(MemberRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        _ = ShowAsync(value);
    }

    // ================================================================ the list

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var keep = Selected?.MemberId;

        await LoadAsync();

        Selected = Shown.FirstOrDefault(m => m.MemberId == keep);
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var rows = await db.Members
                .OrderBy(m => m.FullName)
                .Join(db.MemberCategories, m => m.CategoryId, c => c.CategoryId, (m, c) => new { m, c })
                .Select(x => new
                {
                    x.m.MemberId,
                    x.m.MembershipNo,
                    x.m.FullName,
                    x.m.Rank,
                    x.m.PersonnelNo,
                    x.m.UnitCoy,
                    x.m.PhotoPath,
                    x.m.ClearanceLevel,
                    x.m.Status,
                    Category = x.c.Name,
                    Held = db.Loans.Count(l => l.MemberId == x.m.MemberId
                        && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE)),
                })
                .ToListAsync();

            _all =
            [
                .. rows.Select(r => new MemberRow(
                    r.MemberId,
                    string.IsNullOrWhiteSpace(r.Rank) ? r.FullName : $"{r.Rank} {r.FullName}",
                    r.MembershipNo,
                    r.PersonnelNo ?? "",
                    r.UnitCoy ?? "",
                    r.Category,
                    r.ClearanceLevel,
                    r.Status,
                    r.Held,
                    Workspace.PhotoPath(r.PhotoPath)))
            ];

            Filter();
        }
        catch (Exception ex)
        {
            Faults.Record("reading the roll", ex);

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

        if (mine == _keystroke)
        {
            Filter();
        }
    }

    /// <summary>
    /// Closed and posted-out members are hidden by default.
    ///
    /// They are never deleted — their loan history is the library's record of
    /// where its books have been — but somebody who left the unit two years ago
    /// should not be in the way of the person standing at the counter.
    /// </summary>
    private void Filter()
    {
        IEnumerable<MemberRow> rows = _all;

        if (!ShowClosed)
        {
            rows = rows.Where(m => m.Status is not (MemberStatus.CLOSED or MemberStatus.POSTED_OUT));
        }

        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > 0)
        {
            rows = rows.Where(m => words.All(m.Mentions));
        }

        Shown.Clear();

        foreach (var row in rows)
        {
            Shown.Add(row);
        }

        OnPropertyChanged(nameof(Tally));
        OnPropertyChanged(nameof(Nothing));
    }

    // ============================================================== one person

    private async Task ShowAsync(MemberRow? row)
    {
        Holding.Clear();
        Owing.Clear();
        Said = "";

        if (row is null)
        {
            Who = "";
            Photo = null;
            Deposit = "";
            Remarks = "";
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var member = await db.Members.FirstOrDefaultAsync(m => m.MemberId == row.MemberId);

            if (member is null)
            {
                return;
            }

            var category = await db.MemberCategories
                .FirstAsync(c => c.CategoryId == member.CategoryId);

            var today = DateOnly.FromDateTime(DateTime.Today);

            Who = member.Display;
            Number = member.MembershipNo;
            Category = category.Name;
            Rules = $"{category.MaxBooks} books, {category.LoanDays} days, "
                + (category.MaxRenewals == 0 ? "no renewals" : $"{category.MaxRenewals} renewals");

            Unit = string.Join("  ·  ", new[] { member.UnitCoy, member.Appointment, member.PersonnelNo }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            Contact = string.Join("  ·  ", new[] { member.Phone, member.Email }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            Enrolled = member.ValidUpto is null
                ? $"Enrolled {member.EnrolledOn:dd MMM yyyy}"
                : $"Enrolled {member.EnrolledOn:dd MMM yyyy}, valid to {member.ValidUpto:dd MMM yyyy}";

            Photo = Pictures.Load(Workspace.PhotoPath(member.PhotoPath));

            Deposit = member.SecurityDeposit is > 0
                ? $"{Session.Preferences.Money(member.SecurityDeposit.Value)} deposit held"
                : "";

            Remarks = member.Remarks ?? "";

            Standing = Words.Of(member.Status);
            StandingIsPlain = member.Status == MemberStatus.ACTIVE;

            // What they are actually cleared for, which is their own clearance
            // capped at what the category allows — not the column on its own.
            var effective = member.EffectiveClearance(category);

            Clearance = Words.Of(effective);
            IsClassified = effective.IsClassified();

            var outstanding = await new Roll(db).OutstandingForAsync(member, category, today);

            foreach (var (loan, copy, title, accruing) in outstanding.OpenLoans)
            {
                Holding.Add(new HoldingRow(
                    title.Name,
                    Session.Preferences.Accession(copy.AccessionNo),
                    loan.DueOn,
                    today.DayNumber - loan.DueOn.DayNumber,
                    accruing > 0 ? Session.Preferences.Money(accruing) : ""));
            }

            foreach (var fine in outstanding.PendingFines)
            {
                Owing.Add(new OwedRow(
                    Words.Of(fine.Type),
                    Session.Preferences.Money(fine.Amount),
                    fine.CalculatedOn));
            }

            OwesAnything = outstanding.Total > 0;
            Owed = Session.Preferences.Money(outstanding.Total);

            MayBeCleared = outstanding.Eligible && member.Status != MemberStatus.POSTED_OUT;

            ClearanceVerdict = member.Status == MemberStatus.POSTED_OUT
                ? $"Signed off {member.ClearedOn:dd MMM yyyy}."
                : outstanding.Eligible
                    ? "Nothing out, nothing owed — this member can be signed off."
                    : Owing.Count > 0 && Holding.Count > 0
                        ? $"{Holding.Count} book(s) still out and {Owed} owed."
                        : Holding.Count > 0
                            ? $"{Holding.Count} book(s) still out."
                            : $"{Owed} owed.";

            OnPropertyChanged(nameof(NothingHeld));
        }
        catch (Exception ex)
        {
            Faults.Record("reading a member", ex);

            Problem = Faults.Explain(ex);
        }
    }

    // ================================================================= actions

    /// <summary>Raised when a member needs adding or editing in its own window.</summary>
    public event Func<long?, Task>? Edit;

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Edit is not null)
        {
            await Edit(null);

            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ReviseAsync()
    {
        if (Selected is not null && Edit is not null)
        {
            await Edit(Selected.MemberId);

            await RefreshAsync();
        }
    }

    /// <summary>
    /// The no-dues chit. Only offered when there is genuinely nothing
    /// outstanding — a clearance button that then refuses is a button that
    /// teaches people to distrust the screen.
    /// </summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Selected is null || !MayBeCleared)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var member = await db.Members.FirstAsync(m => m.MemberId == Selected.MemberId);
            var category = await db.MemberCategories.FirstAsync(c => c.CategoryId == member.CategoryId);

            var roll = new Roll(db);

            // Asked again at the moment of signing off, not only when the panel
            // was drawn: a book may have gone out since.
            var outstanding = await roll.OutstandingForAsync(member, category,
                DateOnly.FromDateTime(DateTime.Today));

            if (!outstanding.Eligible)
            {
                Said = "That has changed — there is something outstanding now.";
                SaidIsGood = false;

                await ShowAsync(Selected);
                return;
            }

            await roll.ClearAsync(member, Session.User!.UserId, DateOnly.FromDateTime(DateTime.Today));

            Said = $"{member.Display} signed off. Nothing outstanding.";
            SaidIsGood = true;

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("signing a member off", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    /// <summary>
    /// Raised when passes should be printed. The view answers it, because
    /// choosing where a file goes needs a window to ask from.
    /// </summary>
    public event Func<IReadOnlyList<PassFor>, Task>? PrintPasses;

    /// <summary>
    /// The pass for whoever is picked.
    ///
    /// Read fresh rather than taken off the row: the QR token is what the
    /// scanner will read, and printing the one that was on screen before
    /// somebody reissued it would print a dead pass.
    /// </summary>
    [RelayCommand]
    private async Task PassAsync()
    {
        if (Selected is null || PrintPasses is null)
        {
            return;
        }

        await PrintAsync([Selected.MemberId]);
    }

    /// <summary>
    /// Passes for everybody the search is currently showing.
    ///
    /// Enrolment happens in intakes, so passes are printed in intakes: search
    /// for the company, print the sheet, cut it up. Printing one at a time
    /// would be forty trips to the printer.
    /// </summary>
    [RelayCommand]
    private async Task PassesAsync()
    {
        if (PrintPasses is null || Shown.Count == 0)
        {
            return;
        }

        await PrintAsync([.. Shown.Select(m => m.MemberId)]);
    }

    private async Task PrintAsync(IReadOnlyList<long> memberIds)
    {
        try
        {
            await using var db = Workspace.Open();

            var passes = await new Roll(db).PassesForAsync(memberIds);

            if (passes.Count == 0)
            {
                Said = "There is nobody to print a pass for.";
                SaidIsGood = false;

                return;
            }

            await PrintPasses!(passes);
        }
        catch (Exception ex)
        {
            Faults.Record("printing passes", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    [RelayCommand]
    private async Task ReissuePassAsync()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var member = await db.Members.FirstAsync(m => m.MemberId == Selected.MemberId);

            await new Roll(db).ReissuePassAsync(member, Session.User!.UserId);

            Said = "A new pass code was issued. Every printed copy of the old pass is now dead — reprint it.";
            SaidIsGood = true;
        }
        catch (Exception ex)
        {
            Faults.Record("reissuing a pass", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }
}

/// <summary>One line of the roll.</summary>
public record MemberRow(
    long MemberId, string Name, string Number, string PersonnelNo,
    string Unit, string Category, SecurityClass ClearanceLevel,
    MemberStatus Status, int Held, string? PhotoFile = null)
{
    /// <summary>Their own clearance, said plainly — the column the web roll shows.</summary>
    public string Clearance => Words.Of(ClearanceLevel);

    public bool IsClassified => ClearanceLevel != SecurityClass.UNCLASSIFIED;

    private Bitmap? _photo;
    private bool _photoLoaded;

    /// <summary>
    /// The face, loaded the first time a row is actually drawn — the list
    /// virtualises, so only the dozen on screen ever read a file. A member with
    /// no photo shows their initials instead of an empty square.
    /// </summary>
    public Bitmap? Photo
    {
        get
        {
            if (!_photoLoaded)
            {
                _photoLoaded = true;
                _photo = Pictures.Load(PhotoFile);
            }

            return _photo;
        }
    }

    public bool HasPhoto => Photo is not null;

    /// <summary>Two letters for the placeholder, when there is no photograph.</summary>
    public string Initials
    {
        get
        {
            var letters = string.Concat(Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetter(w[0]))
                .Take(2)
                .Select(w => char.ToUpperInvariant(w[0])));

            return letters.Length > 0 ? letters : "—";
        }
    }

    public string StatusText => Words.Of(Status);

    public bool IsActive => Status == MemberStatus.ACTIVE;

    /// <summary>
    /// Where they belong, under their name. The category is here rather than in
    /// a column of its own because the pane is narrow and the name is what the
    /// eye actually travels along.
    /// </summary>
    public string Belonging => string.Join("  ·  ",
        new[] { Category, Unit }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string HeldText => Held switch
    {
        0 => "",
        1 => "1 out",
        _ => $"{Held} out",
    };

    public bool HoldsAnything => Held > 0;

    public bool Mentions(string word) =>
        Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Number.Contains(word, StringComparison.OrdinalIgnoreCase)
        || PersonnelNo.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Unit.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A book this member has, and what it is quietly costing them.</summary>
public record HoldingRow(string Title, string Accession, DateOnly Due, int Late, string Accruing)
{
    public bool IsLate => Late > 0;

    public string DueText => IsLate
        ? Late == 1 ? "1 day late" : $"{Late} days late"
        : $"due {Due:dd MMM yyyy}";

    public bool HasAccrued => Accruing.Length > 0;
}

/// <summary>A fine already raised and not yet settled.</summary>
public record OwedRow(string Type, string Amount, DateOnly Raised)
{
    public string RaisedText => Raised.ToString("dd MMM yyyy");
}
