using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The condemnation register.
///
/// Books are never deleted. A copy that leaves the library is marked withdrawn
/// and stays in the register for ever, with its accession number retired — so
/// this screen is append-only, like the accession register it mirrors. Nothing
/// here edits a past withdrawal.
///
/// A board condemns a shelf at a time, so a withdrawal is a batch: one set of
/// proceedings, one sanction, many books.
/// </summary>
public partial class WithdrawalsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private WithdrawalRow? _selected;

    // ---------------------------------------------------------- a new board
    [ObservableProperty] private bool _drafting;
    [ObservableProperty] private WithdrawalReason _reason = WithdrawalReason.DAMAGED;
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private DateTime? _on = DateTime.Today;
    [ObservableProperty] private string _boardProceedings = "";
    [ObservableProperty] private string _sanctionAuthority = "";
    [ObservableProperty] private DateTime? _sanctionDate;
    [ObservableProperty] private string _lossAmount = "";
    [ObservableProperty] private string _remarks = "";
    [ObservableProperty] private string _identifiers = "";

    /// <summary>
    /// The book a superseded one is replaced by — named the way books are named
    /// everywhere else, by accession number or by title. Only asked for, and
    /// only required, when the reason is that the book was superseded.
    /// </summary>
    [ObservableProperty] private string _replacing = "";

    // ------------------------------------------------------------ what shows
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _particulars = "";
    [ObservableProperty] private string _value = "";

    private readonly List<Copy> _picked = [];

    public WithdrawalsViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<WithdrawalRow> Boards { get; } = [];

    public ObservableCollection<CondemnedRow> Under { get; } = [];

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public WithdrawalReason[] Reasons { get; } = Enum.GetValues<WithdrawalReason>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.WithdrawalsManage);

    public bool HasSelection => Selected is not null && !Drafting;

    public bool Picked => Candidates.Count > 0;

    public int PickedCount => Candidates.Count(c => c.IsChosen);

    public bool AnyPicked => PickedCount > 0;

    /// <summary>Only a loss is charged to anybody, so only a loss asks the amount.</summary>
    public bool IsLoss => Reason == WithdrawalReason.LOST;

    /// <summary>A superseded book is replaced by a newer one, which has to be named.</summary>
    public bool IsSuperseded => Reason == WithdrawalReason.SUPERSEDED;

    public string PickedText => Candidates.Count == 0
        ? "Scan or type the accession numbers below, or take the ones a stock check reported missing."
        : $"{PickedCount:N0} of {Candidates.Count:N0} ticked";

    /// <summary>Raised to write a certificate, or the whole register.</summary>
    public event Func<Withdrawal, string, IReadOnlyList<Condemned>, Task>? PrintCertificate;

    public event Func<Report, Task>? PrintRegister;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnReasonChanged(WithdrawalReason value)
    {
        OnPropertyChanged(nameof(IsLoss));
        OnPropertyChanged(nameof(IsSuperseded));
    }

    partial void OnDraftingChanged(bool value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnSelectedChanged(WithdrawalRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        _ = ShowAsync(value);
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var keep = Selected?.Withdrawal.WithdrawalId;

            Boards.Clear();

            foreach (var (withdrawal, copies, by) in await new Withdrawals(db, Session.Preferences).AllAsync())
            {
                Boards.Add(new WithdrawalRow(withdrawal, copies, by));
            }

            Selected = Boards.FirstOrDefault(b => b.Withdrawal.WithdrawalId == keep)
                ?? Boards.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Faults.Record("reading the condemnation register", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ShowAsync(WithdrawalRow? row)
    {
        Under.Clear();

        if (row is null)
        {
            Heading = "";
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var withdrawal = row.Withdrawal;

            Heading = $"{withdrawal.WithdrawalNo} — {Words.Of(withdrawal.Reason).ToLowerInvariant()}";

            Particulars = string.Join("  ·  ", new[]
            {
                withdrawal.WithdrawalDate.ToString("dd MMM yyyy"),
                string.IsNullOrWhiteSpace(withdrawal.BoardProceedings)
                    ? null
                    : "Board " + withdrawal.BoardProceedings,
                string.IsNullOrWhiteSpace(withdrawal.SanctionAuthority)
                    ? null
                    : "Sanctioned by " + withdrawal.SanctionAuthority,
                "by " + row.By,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            Value = Session.Preferences.Money(withdrawal.TotalValue) + " written off";

            foreach (var (copy, title) in await new Withdrawals(db, Session.Preferences)
                .UnderAsync(withdrawal.WithdrawalId))
            {
                Under.Add(new CondemnedRow(
                    Session.Preferences.Accession(copy.AccessionNo),
                    title.Name,
                    Words.Of(copy.Source),
                    copy.Cost));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading a withdrawal", ex);

            Problem = Faults.Explain(ex);
        }
    }

    // =============================================================== drafting

    [RelayCommand]
    private async Task DraftAsync()
    {
        Drafting = true;
        Said = "";
        Replacing = "";

        Problems.Clear();
        Candidates.Clear();
        _picked.Clear();

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Picked));
        OnPropertyChanged(nameof(PickedText));

        try
        {
            await using var db = Workspace.Open();

            Number = await new Withdrawals(db, Session.Preferences).NextNumberAsync();
        }
        catch (Exception ex)
        {
            Faults.Record("starting a withdrawal", ex);
        }
    }

    [RelayCommand]
    private void Abandon()
    {
        Drafting = false;
        Replacing = "";

        Candidates.Clear();

        OnPropertyChanged(nameof(Picked));
    }

    /// <summary>
    /// Whatever a stock check could not find. These are what a loss board
    /// actually sits on, so they are offered rather than looked up one by one.
    /// </summary>
    [RelayCommand]
    private async Task TakeMissingAsync()
    {
        try
        {
            await using var db = Workspace.Open();

            Offer(await new Withdrawals(db, Session.Preferences).MissingAsync(), tick: true);

            Reason = WithdrawalReason.LOST;

            Said = Candidates.Count == 0
                ? "No stock check has reported anything missing."
                : $"{Candidates.Count:N0} reported missing, all ticked.";

            SaidIsGood = Candidates.Count > 0;
        }
        catch (Exception ex)
        {
            Faults.Record("reading the missing copies", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (Identifiers.Trim().Length == 0)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var found = await new Withdrawals(db, Session.Preferences)
                .FindAsync([Identifiers]);

            Offer(found, tick: true);

            Said = found.Count == 0
                ? "None of those match a copy that is still on the books."
                : $"{found.Count:N0} found and ticked.";

            SaidIsGood = found.Count > 0;

            Identifiers = "";
        }
        catch (Exception ex)
        {
            Faults.Record("finding copies to withdraw", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    private void Offer(IReadOnlyList<(Copy Copy, Title Title)> found, bool tick)
    {
        foreach (var (copy, title) in found)
        {
            if (_picked.Any(c => c.CopyId == copy.CopyId))
            {
                continue;
            }

            _picked.Add(copy);

            var row = new CandidateRow(
                copy.CopyId,
                Session.Preferences.Accession(copy.AccessionNo),
                title.Name,
                Words.Of(copy.Status),
                copy.Cost)
            { IsChosen = tick };

            // Listened to here, where the row is made — the screen that counts
            // the ticks cannot subscribe to rows that already exist.
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CandidateRow.IsChosen))
                {
                    OnPropertyChanged(nameof(PickedCount));
                    OnPropertyChanged(nameof(AnyPicked));
                    OnPropertyChanged(nameof(PickedText));
                }
            };

            Candidates.Add(row);
        }

        OnPropertyChanged(nameof(Picked));
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(AnyPicked));
        OnPropertyChanged(nameof(PickedText));
    }

    /// <summary>
    /// The title a book identifier points at: a copy's number gives its title,
    /// and failing that a single title whose name matches. Null when it is
    /// nothing, or matches more than one — the caller says so.
    /// </summary>
    private static async Task<long?> ResolveTitleAsync(MilLibDbContext db, string what)
    {
        if (what.Length == 0)
        {
            return null;
        }

        var copy = await db.Copies
            .FirstOrDefaultAsync(c => c.Barcode == what || c.AccessionNo == what);

        if (copy is not null)
        {
            return copy.TitleId;
        }

        var like = $"%{what}%";

        var titles = await db.Titles
            .Where(t => EF.Functions.Like(t.Name, like))
            .Select(t => t.TitleId)
            .Take(2)
            .ToListAsync();

        return titles.Count == 1 ? titles[0] : null;
    }

    [RelayCommand]
    private async Task WithdrawAsync()
    {
        if (Busy || !AnyPicked)
        {
            return;
        }

        Problems.Clear();
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var withdrawals = new Withdrawals(db, Session.Preferences);

            var chosen = Candidates.Where(c => c.IsChosen).Select(c => c.CopyId).ToList();

            // Re-read at the moment of committing. Between drafting and signing,
            // one of these may have been issued to somebody.
            var copies = await db.Copies.Where(c => chosen.Contains(c.CopyId)).ToListAsync();

            // The replacement, when one is superseded. Resolved the same way a
            // book is resolved anywhere: its accession number, or its title.
            long? replacedBy = null;

            if (IsSuperseded)
            {
                replacedBy = await ResolveTitleAsync(db, Replacing.Trim());

                if (replacedBy is null)
                {
                    Problems.Add(Replacing.Trim().Length == 0
                        ? "A superseded book is replaced by another. Give the replacing book's accession number or title."
                        : $"No single book matches “{Replacing.Trim()}”. Use its accession number.");

                    OnPropertyChanged(nameof(HasProblems));

                    return;
                }
            }

            var board = new Condemnation(
                Reason,
                DateOnly.FromDateTime((On ?? DateTime.Today).Date),
                Number,
                BoardProceedings,
                SanctionAuthority,
                SanctionDate is null ? null : DateOnly.FromDateTime(SanctionDate.Value.Date),
                decimal.TryParse(LossAmount, out var loss) && loss > 0 ? loss : null,
                Remarks,
                replacedBy);

            var problems = await withdrawals.ProblemsWithAsync(board, copies);

            if (problems.Count > 0)
            {
                foreach (var line in problems)
                {
                    Problems.Add(line);
                }

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            var withdrawal = await withdrawals.WithdrawAsync(board, copies, Session.User!.UserId);

            Drafting = false;
            Candidates.Clear();
            _picked.Clear();

            Said = $"{copies.Count} {(copies.Count == 1 ? "copy" : "copies")} withdrawn under "
                + $"{withdrawal.WithdrawalNo}. The accession numbers are retired.";

            SaidIsGood = true;

            await LoadAsync();

            Selected = Boards.FirstOrDefault(b => b.Withdrawal.WithdrawalId == withdrawal.WithdrawalId);
        }
        catch (Exception ex)
        {
            Faults.Record("withdrawing copies", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;
        }
    }

    // ============================================================== on paper

    [RelayCommand]
    private async Task CertificateAsync()
    {
        if (Selected is null || PrintCertificate is null)
        {
            return;
        }

        await PrintCertificate(Selected.Withdrawal, Selected.By,
            [.. Under.Select(u => new Condemned(u.Accession, u.Title, u.Source, u.Cost, ""))]);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (PrintRegister is null)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var rows = await new Withdrawals(db, Session.Preferences).RegisterAsync();

            await PrintRegister(new Report(
                "Condemnation Register",
                ["Accession No", "Title", "Withdrawn", "Reason", "Under", "Cost"],
                [.. rows.Select(r => (IReadOnlyList<string>)
                [
                    Session.Preferences.Accession(r.Copy.AccessionNo),
                    r.Title.Name,
                    r.Copy.WithdrawnAt?.ToString("dd MMM yyyy") ?? "",
                    r.Under is null ? "" : Words.Of(r.Under.Reason),
                    r.Under?.WithdrawalNo ?? "",
                    r.Copy.Cost?.ToString("N2") ?? "",
                ])],
                "Every copy ever taken off this library's books. The accession numbers are retired and are never reissued.",
                [false, false, false, false, false, true]));
        }
        catch (Exception ex)
        {
            Faults.Record("printing the condemnation register", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }
}

/// <summary>One set of board proceedings.</summary>
public record WithdrawalRow(Withdrawal Withdrawal, int Copies, string By)
{
    public string Number => Withdrawal.WithdrawalNo;

    public string Reason => Words.Of(Withdrawal.Reason);

    public string When => Withdrawal.WithdrawalDate.ToString("dd MMM yyyy");

    public string Size => Copies == 1 ? "1 copy" : $"{Copies:N0} copies";

    public bool WasLoss => Withdrawal.Reason == WithdrawalReason.LOST;
}

/// <summary>One book already condemned.</summary>
public record CondemnedRow(string Accession, string Title, string Source, decimal? Cost)
{
    public string CostText => Cost is null ? "" : Session.Preferences.Money(Cost.Value);
}

/// <summary>One book being considered for condemnation.</summary>
public partial class CandidateRow(long copyId, string accession, string title, string state, decimal? cost)
    : ObservableObject
{
    [ObservableProperty] private bool _isChosen;

    public long CopyId { get; } = copyId;

    public string Accession { get; } = accession;

    public string Title { get; } = title;

    public string State { get; } = state;

    public decimal? Cost { get; } = cost;

    public string CostText => Cost is null ? "" : Session.Preferences.Money(Cost.Value);
}
