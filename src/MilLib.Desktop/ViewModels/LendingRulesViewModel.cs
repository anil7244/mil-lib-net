using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The lending policy.
///
/// The least-visited screen in the application and the most consequential:
/// every loan period, borrowing limit, renewal allowance and fine rate the
/// library uses is a number on this screen. Nothing about lending is written in
/// the code, which is the point — a library that lends to officers for a month
/// and to recruits for a fortnight is configured, not rebuilt.
///
/// Each row says how many members it governs, because changing a rule here
/// changes what happens to all of them at the counter tomorrow.
/// </summary>
public partial class LendingRulesViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private RuleRow? _selected;
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // -------------------------------------------------------- the one being edited
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private int _maxBooks = 2;
    [ObservableProperty] private int _loanDays = 14;
    [ObservableProperty] private int _maxRenewals = 1;
    [ObservableProperty] private string _finePerDay = "1";
    [ObservableProperty] private int _graceDays = 3;
    [ObservableProperty] private bool _canReserve = true;
    [ObservableProperty] private SecurityClass _maxClearance = SecurityClass.UNCLASSIFIED;
    [ObservableProperty] private bool _requiresDeposit;
    [ObservableProperty] private string _depositAmount = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _editing;
    [ObservableProperty] private string _editHeading = "";

    private MemberCategory _working = Policies.Fresh();

    public LendingRulesViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<RuleRow> Rules { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public SecurityClass[] Clearances { get; } = Enum.GetValues<SecurityClass>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.SettingsManage);

    /// <summary>
    /// The rule read back as a sentence, under the fields that make it. It is
    /// far easier to notice that "1 book for 365 days" is wrong when it is
    /// written out than when it is two numbers in two boxes.
    /// </summary>
    public string InPlainWords
    {
        get
        {
            var fine = decimal.TryParse(FinePerDay, out var rate) ? rate : 0m;

            var sentence = $"{Books(MaxBooks)} at a time, for {Days(LoanDays)}";

            sentence += MaxRenewals == 0
                ? ", with no renewals"
                : $", renewable {MaxRenewals} time{(MaxRenewals == 1 ? "" : "s")}";

            sentence += fine <= 0
                ? ". Nothing is charged for lateness"
                : $". {Session.Preferences.Money(fine)} for every late day"
                  + (GraceDays > 0 ? $", after {Days(GraceDays)} of grace" : "");

            sentence += MaxClearance == SecurityClass.UNCLASSIFIED
                ? ". Unclassified material only."
                : $". Cleared up to {Words.Of(MaxClearance)}.";

            return sentence;
        }
    }

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnMaxBooksChanged(int value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnLoanDaysChanged(int value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnMaxRenewalsChanged(int value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnFinePerDayChanged(string value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnGraceDaysChanged(int value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnMaxClearanceChanged(SecurityClass value) => OnPropertyChanged(nameof(InPlainWords));

    partial void OnSelectedChanged(RuleRow? value)
    {
        if (value is not null)
        {
            Open(value.Category);
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            Rules.Clear();

            foreach (var (category, members) in await new Policies(db).AllAsync())
            {
                Rules.Add(new RuleRow(category, members));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the lending rules", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var keep = Selected?.Category.CategoryId;

        await LoadAsync();

        Selected = Rules.FirstOrDefault(r => r.Category.CategoryId == keep);
    }

    [RelayCommand]
    private void Add()
    {
        Selected = null;

        Open(Policies.Fresh());

        EditHeading = "A new category";
    }

    private void Open(MemberCategory category)
    {
        _working = category;

        Name = category.Name;
        Code = category.Code;
        MaxBooks = category.MaxBooks;
        LoanDays = category.LoanDays;
        MaxRenewals = category.MaxRenewals;
        FinePerDay = category.FinePerDay.ToString("0.##");
        GraceDays = category.GraceDays;
        CanReserve = category.CanReserve;
        MaxClearance = category.MaxClearance;
        RequiresDeposit = category.RequiresDeposit;
        DepositAmount = category.DepositAmount?.ToString("0.##") ?? "";
        IsActive = category.IsActive;

        EditHeading = category.CategoryId == 0 ? "A new category" : category.Name;

        Problems.Clear();
        Said = "";

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(InPlainWords));

        Editing = true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Busy)
        {
            return;
        }

        Problems.Clear();
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            _working.Name = Name.Trim();
            _working.Code = Code.Trim().ToUpperInvariant();
            _working.MaxBooks = MaxBooks;
            _working.LoanDays = LoanDays;
            _working.MaxRenewals = MaxRenewals;
            _working.FinePerDay = decimal.TryParse(FinePerDay, out var fine) ? fine : 0m;
            _working.GraceDays = GraceDays;
            _working.CanReserve = CanReserve;
            _working.MaxClearance = MaxClearance;
            _working.RequiresDeposit = RequiresDeposit;
            _working.IsActive = IsActive;

            _working.DepositAmount = decimal.TryParse(DepositAmount, out var deposit) && deposit > 0
                ? deposit
                : null;

            var policies = new Policies(db);

            var problems = await policies.ProblemsWithAsync(_working);

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    Problems.Add(problem);
                }

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            var members = Selected?.Members ?? 0;

            await policies.SaveAsync(_working, Session.User!.UserId);

            Said = members == 0
                ? $"{_working.Name} saved."
                : $"{_working.Name} saved. This changes what {members} member"
                  + $"{(members == 1 ? "" : "s")} may borrow from now on.";

            SaidIsGood = true;

            Editing = false;
        }
        catch (Exception ex)
        {
            Faults.Record("saving a lending rule", ex);

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
        if (Selected is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var category = await db.MemberCategories
                .FirstAsync(c => c.CategoryId == Selected.Category.CategoryId);

            var policies = new Policies(db);

            var why = await policies.WhyNotRemovableAsync(category);

            if (why is not null)
            {
                Said = why;
                SaidIsGood = false;

                return;
            }

            await policies.RemoveAsync(category, Session.User!.UserId);

            Said = $"{category.Name} removed.";
            SaidIsGood = true;

            Editing = false;
            Selected = null;
        }
        catch (Exception ex)
        {
            Faults.Record("removing a lending rule", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
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

    private static string Books(int count) => count == 1 ? "1 book" : $"{count} books";

    private static string Days(int count) => count == 1 ? "1 day" : $"{count} days";
}

/// <summary>One category, and how many people it governs.</summary>
public record RuleRow(MemberCategory Category, int Members)
{
    public string Name => Category.Name;

    public string Code => Category.Code;

    public string Allowance => $"{Category.MaxBooks} books · {Category.LoanDays} days";

    public string Fine => Category.FinePerDay <= 0
        ? "no fine"
        : $"{Session.Preferences.Money(Category.FinePerDay)}/day";

    public string Clearance => Words.Of(Category.MaxClearance);

    /// <summary>
    /// The code, and how many people this rule governs — under the name, where
    /// there is room, rather than in a column that would squeeze the name.
    /// </summary>
    public string Belonging => $"{Code}  ·  {MembersText}";

    public bool IsClassified => Category.MaxClearance.IsClassified();

    public bool IsRetired => !Category.IsActive;

    public string MembersText => Members switch
    {
        0 => "nobody",
        1 => "1 member",
        _ => $"{Members} members",
    };
}
