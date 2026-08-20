using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Enrolling somebody, or putting their details right.
///
/// The category is the important field on this form and is deliberately not
/// buried among the others: it decides how many books this person may hold, for
/// how long, how often they may renew and what a late day costs them. What it
/// permits is spelled out beside it as it is chosen, so nobody has to remember
/// what "Officer" means.
/// </summary>
public partial class MemberEditViewModel : ViewModelBase
{
    [ObservableProperty] private string _membershipNo = "";
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _rank = "";
    [ObservableProperty] private string _personnelNo = "";
    [ObservableProperty] private string _unitCoy = "";
    [ObservableProperty] private string _appointment = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private CategoryChoice? _category;
    [ObservableProperty] private SecurityClass _clearanceLevel = SecurityClass.UNCLASSIFIED;
    [ObservableProperty] private MemberStatus _status = MemberStatus.ACTIVE;
    // DateTime, not DateTimeOffset — see the note in BookViewModel.
    [ObservableProperty] private DateTime? _enrolledOn = DateTime.Today;
    [ObservableProperty] private DateTime? _validUpto;
    [ObservableProperty] private string _securityDeposit = "";
    [ObservableProperty] private string _remarks = "";

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _heading = "New member";

    private readonly long? _memberId;
    private Member _member = new();

    public MemberEditViewModel(long? memberId = null)
    {
        _memberId = memberId;

        _ = LoadAsync();
    }

    public ObservableCollection<CategoryChoice> Categories { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public SecurityClass[] Clearances { get; } = Enum.GetValues<SecurityClass>();

    public MemberStatus[] Statuses { get; } = Enum.GetValues<MemberStatus>();

    public bool HasProblems => Problems.Count > 0;

    public bool IsNew => _memberId is null;

    /// <summary>
    /// What the chosen category permits, said in full beside the field. The
    /// clearance ceiling is included because it is the one that refuses an
    /// issue outright, and being told about it here is better than at the desk.
    /// </summary>
    public string CategoryMeans => Category is null
        ? ""
        : $"{Category.MaxBooks} books at a time · {Category.LoanDays} days a loan · "
        + (Category.MaxRenewals switch
        {
            0 => "no renewals",
            1 => "1 renewal",
            var n => $"{n} renewals",
        })
        + $" · {Session.Preferences.Money(Category.FinePerDay)} a day late"
        + (Category.GraceDays > 0 ? $" after {Category.GraceDays} days' grace" : "")
        + $" · cleared to {Words.Of(Category.MaxClearance)} at most";

    partial void OnCategoryChanged(CategoryChoice? value) => OnPropertyChanged(nameof(CategoryMeans));

    /// <summary>Raised when the record is saved, so the window can close.</summary>
    public event Action? Saved;

    /// <summary>Raised when the person backs out.</summary>
    public event Action? Abandoned;

    private async Task LoadAsync()
    {
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            foreach (var category in await db.MemberCategories.OrderBy(c => c.Name).ToListAsync())
            {
                // A retired category is still offered to somebody already in it,
                // so editing their phone number does not silently move them.
                if (category.IsActive || category.CategoryId == _member.CategoryId)
                {
                    Categories.Add(new CategoryChoice(category));
                }
            }

            if (_memberId is null)
            {
                MembershipNo = await new Roll(db).SuggestedNumberAsync();
                Category = Categories.FirstOrDefault();

                return;
            }

            _member = await db.Members.FirstAsync(m => m.MemberId == _memberId);

            Heading = _member.Display;

            MembershipNo = _member.MembershipNo;
            FullName = _member.FullName;
            Rank = _member.Rank ?? "";
            PersonnelNo = _member.PersonnelNo ?? "";
            UnitCoy = _member.UnitCoy ?? "";
            Appointment = _member.Appointment ?? "";
            Phone = _member.Phone ?? "";
            Email = _member.Email ?? "";
            ClearanceLevel = _member.ClearanceLevel;
            Status = _member.Status;
            EnrolledOn = _member.EnrolledOn.ToDateTime(TimeOnly.MinValue);
            ValidUpto = _member.ValidUpto?.ToDateTime(TimeOnly.MinValue);
            SecurityDeposit = _member.SecurityDeposit?.ToString("0.##") ?? "";
            Remarks = _member.Remarks ?? "";

            Category = Categories.FirstOrDefault(c => c.CategoryId == _member.CategoryId);
        }
        catch (Exception ex)
        {
            Faults.Record("opening a member", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Busy)
        {
            return;
        }

        Problems.Clear();

        if (Category is null)
        {
            Problems.Add("Choose a category — it decides how much they may borrow and for how long.");
            OnPropertyChanged(nameof(HasProblems));

            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            _member.MembershipNo = MembershipNo.Trim();
            _member.FullName = FullName.Trim();
            _member.Rank = Blank(Rank);
            _member.PersonnelNo = Blank(PersonnelNo);
            _member.UnitCoy = Blank(UnitCoy);
            _member.Appointment = Blank(Appointment);
            _member.Phone = Blank(Phone);
            _member.Email = Blank(Email);
            _member.CategoryId = Category.CategoryId;
            _member.ClearanceLevel = ClearanceLevel;
            _member.Status = Status;
            _member.EnrolledOn = DateOnly.FromDateTime((EnrolledOn ?? DateTime.Today).Date);
            _member.ValidUpto = ValidUpto is null ? null : DateOnly.FromDateTime(ValidUpto.Value.Date);
            _member.Remarks = Blank(Remarks);

            _member.SecurityDeposit = decimal.TryParse(SecurityDeposit, out var deposit) && deposit > 0
                ? deposit
                : null;

            var roll = new Roll(db);

            var problems = await roll.ProblemsWithAsync(_member, Category.Category);

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    Problems.Add(problem);
                }

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            if (IsNew)
            {
                await roll.EnrolAsync(_member, Session.User!.UserId);
            }
            else
            {
                await roll.ReviseAsync(_member, Session.User!.UserId);
            }

            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Faults.Record("saving a member", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Abandon() => Abandoned?.Invoke();

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>A category as the form offers it, carrying what it permits.</summary>
public record CategoryChoice(MemberCategory Category)
{
    public long CategoryId => Category.CategoryId;

    public int MaxBooks => Category.MaxBooks;

    public int LoanDays => Category.LoanDays;

    public int MaxRenewals => Category.MaxRenewals;

    public int GraceDays => Category.GraceDays;

    public decimal FinePerDay => Category.FinePerDay;

    public SecurityClass MaxClearance => Category.MaxClearance;

    public override string ToString() => Category.Name;
}
