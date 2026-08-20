using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The accounts that may sign in.
///
/// Small and rarely visited, and the one screen where a mistake cannot be
/// corrected from inside the application: suspend the last administrator and
/// nobody can appoint another. So the guards are enforced in
/// <see cref="Staff"/>, and this screen also says which fields are locked and
/// why, before anybody types into them.
///
/// A password is set here and never shown here. There is no other way to
/// recover a forgotten one — the deployment is air-gapped and has no mail path
/// — so the reset asks the administrator for their own password first.
/// </summary>
public partial class StaffViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private PersonRow? _selected;

    // ----------------------------------------------------- the one being edited
    [ObservableProperty] private bool _editing;
    [ObservableProperty] private string _editHeading = "";
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private UserRole _role = UserRole.COUNTER;
    [ObservableProperty] private SecurityClass _clearance = SecurityClass.UNCLASSIFIED;
    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _adminPassword = "";
    [ObservableProperty] private string _roleLock = "";
    [ObservableProperty] private string _activeLock = "";

    private User _working = new();

    public StaffViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<PersonRow> People { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public UserRole[] Roles { get; } = Enum.GetValues<UserRole>();

    public SecurityClass[] Clearances { get; } = Enum.GetValues<SecurityClass>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool HasSaid => Said.Length > 0;

    public bool MayManage => Session.Can(Ability.UsersManage);

    public bool RoleIsLocked => RoleLock.Length > 0;

    public bool ActiveIsLocked => ActiveLock.Length > 0;

    public bool MayChangeRole => MayManage && !RoleIsLocked;

    public bool MayChangeActive => MayManage && !ActiveIsLocked;

    /// <summary>Only an existing account can have its password reset; a new one is given one.</summary>
    public bool Resetting => Editing && !IsNew;

    /// <summary>
    /// What this role may do, listed under the dropdown.
    ///
    /// "Assistant Librarian" tells nobody whether an assistant librarian may
    /// waive a fine. The list does, and it is read from the same matrix the
    /// application actually gates on rather than written out again here.
    /// </summary>
    public string RoleAllows => Role == UserRole.SUPERADMIN
        ? "Everything, including this screen. Appoint as few as the unit can manage with."
        : string.Join(" · ", Abilities.GrantedTo(Role).Select(Abilities.Label));

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnRoleChanged(UserRole value) => OnPropertyChanged(nameof(RoleAllows));

    partial void OnRoleLockChanged(string value)
    {
        OnPropertyChanged(nameof(RoleIsLocked));
        OnPropertyChanged(nameof(MayChangeRole));
    }

    partial void OnActiveLockChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveIsLocked));
        OnPropertyChanged(nameof(MayChangeActive));
    }

    partial void OnEditingChanged(bool value) => OnPropertyChanged(nameof(Resetting));

    partial void OnIsNewChanged(bool value) => OnPropertyChanged(nameof(Resetting));

    partial void OnSelectedChanged(PersonRow? value)
    {
        if (value is not null)
        {
            _ = OpenAsync(value.User);
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var today = DateOnly.FromDateTime(DateTime.Now);

            People.Clear();

            foreach (var user in await new Staff(db).AllAsync())
            {
                People.Add(new PersonRow(user, today, user.UserId == Session.User?.UserId));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the staff accounts", ex);

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
        var keep = Selected?.User.UserId;

        await LoadAsync();

        Selected = People.FirstOrDefault(p => p.User.UserId == keep);
    }

    [RelayCommand]
    private void Add()
    {
        Selected = null;

        _working = new User { Role = UserRole.COUNTER, IsActive = true };

        Username = "";
        FullName = "";
        Role = UserRole.COUNTER;
        Clearance = SecurityClass.UNCLASSIFIED;
        Active = true;
        NewPassword = "";
        AdminPassword = "";
        RoleLock = "";
        ActiveLock = "";
        IsNew = true;
        EditHeading = "A new account";

        Problems.Clear();
        Said = "";

        OnPropertyChanged(nameof(HasProblems));

        Editing = true;
    }

    /// <summary>
    /// Open one account, working out first what about it cannot be changed.
    ///
    /// Asked of the database rather than guessed from the row on screen: whether
    /// somebody is the last administrator depends on every other account, and
    /// the list may have been read minutes ago.
    /// </summary>
    private async Task OpenAsync(User user)
    {
        _working = user;

        Username = user.Username;
        FullName = user.FullName;
        Role = user.Role;
        Clearance = user.ClearanceLevel;
        Active = user.IsActive;
        NewPassword = "";
        AdminPassword = "";
        IsNew = false;
        EditHeading = user.Display;

        // Problems belong to the form and go with it. What was just done does
        // not: every action here ends by re-reading the list, which reselects
        // the account and lands back in here — and clearing the banner at this
        // point wiped the answer before anybody could read it. A refused
        // suspension appeared to do nothing at all.
        Problems.Clear();

        OnPropertyChanged(nameof(HasProblems));

        Editing = true;

        RoleLock = "";
        ActiveLock = "";

        try
        {
            await using var db = Workspace.Open();

            var staff = new Staff(db);

            RoleLock = user.UserId == Session.User?.UserId
                ? "Your own role is locked. Ask another administrator to change it."
                : await LastAdminAsync(db, user)
                    ? "The last Super Administrator who can sign in. The role is locked so the unit cannot be shut out of its own library."
                    : "";

            ActiveLock = await staff.WhyNotSuspendAsync(user, Session.User!) ?? "";
        }
        catch (Exception ex)
        {
            Faults.Record("checking what may be changed on an account", ex);

            // Locked rather than opened. If the guards could not be checked, the
            // safe answer is that nothing may be changed.
            RoleLock = ActiveLock = "This could not be checked just now, so it is locked.";
        }
    }

    private static async Task<bool> LastAdminAsync(MilLibDbContext db, User user) =>
        user.Role == UserRole.SUPERADMIN
        && user.IsActive
        && !await db.Users.AnyAsync(u =>
            u.Role == UserRole.SUPERADMIN && u.IsActive && u.UserId != user.UserId);

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

            var staff = new Staff(db);

            _working.Username = Username.Trim();
            _working.FullName = FullName.Trim();
            _working.ClearanceLevel = Clearance;

            // Left where it was when the field is locked, so a save cannot
            // quietly carry a value the screen was refusing to accept.
            if (MayChangeRole)
            {
                _working.Role = Role;
            }

            if (MayChangeActive)
            {
                _working.IsActive = Active;
            }

            var problems = await staff.ProblemsWithAsync(
                _working, Session.User!, IsNew ? NewPassword : null, IsNew);

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
                await staff.CreateAsync(_working, NewPassword, Session.User!.UserId);

                Announce($"{_working.Display} can now sign in as {Abilities.Label(_working.Role)}.", true);
            }
            else
            {
                await staff.ReviseAsync(_working, Session.User!.UserId);

                Announce($"{_working.Display} saved.", true);
            }

            NewPassword = "";
            Editing = false;
        }
        catch (Exception ex)
        {
            Faults.Record("saving a staff account", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;

            await RefreshAsync();
        }
    }

    /// <summary>
    /// Suspend or reinstate, straight from the list rather than through the
    /// editor: it is the thing most often wanted here — somebody has been posted
    /// out — and it should not require opening a form.
    /// </summary>
    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (Selected is null || Busy || !MayManage)
        {
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var user = await db.Users.FirstAsync(u => u.UserId == Selected.User.UserId);

            var staff = new Staff(db);

            if (user.IsActive)
            {
                var why = await staff.WhyNotSuspendAsync(user, Session.User!);

                if (why is not null)
                {
                    Announce(why, false);

                    return;
                }
            }

            var nowActive = !user.IsActive;

            await staff.SetActiveAsync(user, nowActive, Session.User!.UserId);

            Announce(nowActive
                ? $"{user.Display} can sign in again."
                : $"{user.Display} can no longer sign in. Nothing they did was removed — "
                  + "their name stays against every loan and every entry they made.",
                true);
        }
        catch (Exception ex)
        {
            Faults.Record("changing whether an account may sign in", ex);

            Announce(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;

            await RefreshAsync();
        }
    }

    /// <summary>
    /// Setting somebody's password for them. The acting administrator types
    /// their own first: a session left unattended is otherwise a way into every
    /// account in the library.
    /// </summary>
    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (Busy || IsNew || !MayManage)
        {
            return;
        }

        Busy = true;
        Problems.Clear();

        try
        {
            await using var db = Workspace.Open();

            var user = await db.Users.FirstAsync(u => u.UserId == _working.UserId);

            var refusal = await new Staff(db)
                .SetPasswordAsync(user, NewPassword, Session.User!, AdminPassword);

            if (refusal is not null)
            {
                Problems.Add(refusal);

                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            Announce($"The password for {user.Display} was reset. Tell them in person — "
                + "there is no mail on this network and no other way for them to be told.", true);

            NewPassword = "";
            AdminPassword = "";
        }
        catch (Exception ex)
        {
            Faults.Record("resetting a password", ex);

            Problems.Add(Faults.Explain(ex));

            OnPropertyChanged(nameof(HasProblems));
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Editing = false;
        NewPassword = "";
        AdminPassword = "";

        Problems.Clear();

        OnPropertyChanged(nameof(HasProblems));
    }

    private void Announce(string said, bool good)
    {
        Said = said;
        SaidIsGood = good;
    }
}

/// <summary>One account, as the list shows it.</summary>
public record PersonRow(User User, DateOnly Today, bool IsYou)
{
    public string Name => User.Display;

    public string Username => User.Username;

    public string Role => Abilities.Label(User.Role);

    public string Clearance => Words.Of(User.ClearanceLevel);

    public bool IsCleared => User.ClearanceLevel != SecurityClass.UNCLASSIFIED;

    public bool IsSuspended => !User.IsActive;

    /// <summary>
    /// An active account nobody has used for three months.
    ///
    /// Not an error — somebody may simply be on course — but it is a way into
    /// the library that nobody is watching, and it is worth putting in front of
    /// whoever runs this screen rather than leaving to be noticed.
    /// </summary>
    public bool IsStale => Staff.Stale(User, Today);

    public string Standing => $"{Username}  ·  {Staff.LastSeen(User, Today)}";

    public string ToggleWord => User.IsActive ? "Suspend" : "Reinstate";
}
