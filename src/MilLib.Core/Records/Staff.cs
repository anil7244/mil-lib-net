using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// The accounts that may sign in.
///
/// Four guards run through everything here, and all four exist for one reason:
/// a unit that locks itself out of its own library has no way back in without
/// somebody editing the database by hand.
///
///   1. Nobody may suspend their own account.
///   2. Nobody may suspend the last superadmin who can still sign in.
///   3. Nobody may demote that last superadmin either.
///   4. Nobody may change their own role.
///
/// The third and fourth are the subtle ones. Without them the last
/// administrator can quietly demote themselves to Counter and the library has
/// nobody who can appoint anybody.
/// </summary>
public class Staff(MilLibDbContext db)
{
    public async Task<IReadOnlyList<User>> AllAsync() =>
        await db.Users
            .OrderByDescending(u => u.IsActive)
            .ThenBy(u => u.Username)
            .ToListAsync();

    /// <summary>
    /// What is wrong with this account, in words, or nothing.
    ///
    /// <paramref name="actingAs"/> is the person making the change, which is
    /// what the self-guards turn on.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProblemsWithAsync(
        User user, User actingAs, string? password, bool isNew)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            problems.Add("A username is needed — it is what they sign in with.");
        }
        else if (await db.Users.AnyAsync(u =>
            u.Username == user.Username && u.UserId != user.UserId))
        {
            problems.Add($"The username {user.Username} is already taken.");
        }

        if (string.IsNullOrWhiteSpace(user.FullName))
        {
            problems.Add("A name is needed — it is what appears against everything they do.");
        }

        if (isNew && (password is null || password.Length < 8))
        {
            problems.Add("A password of at least eight characters is needed.");
        }

        if (!isNew)
        {
            var existing = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserId == user.UserId);

            if (existing is not null && existing.Role != user.Role)
            {
                if (existing.UserId == actingAs.UserId)
                {
                    problems.Add("You cannot change your own role. Ask another administrator.");
                }
                else if (await IsLastSuperadminAsync(existing))
                {
                    problems.Add("That is the last Super Administrator who can still sign in. "
                        + "Changing their role would lock the unit out of its own library.");
                }
            }
        }

        return problems;
    }

    public async Task<User> CreateAsync(User user, string password, long byUserId)
    {
        user.PasswordHash = SignIn.Hash(password);
        user.CreatedAt = DateTime.Now;
        user.UpdatedAt = DateTime.Now;

        db.Users.Add(user);

        Journal.Note(db, byUserId, "USER_CREATE", "user", null, new
        {
            user.Username,
            role = user.Role.ToString(),
            clearance = user.ClearanceLevel.ToString(),
            active = user.IsActive,
        });

        await db.SaveAndForgetAsync();

        return user;
    }

    public async Task ReviseAsync(User user, long byUserId)
    {
        user.UpdatedAt = DateTime.Now;

        db.Users.Update(user);

        Journal.Note(db, byUserId, "USER_UPDATE", "user", user.UserId, new
        {
            user.Username,
            role = user.Role.ToString(),
            clearance = user.ClearanceLevel.ToString(),
        });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Why this account cannot be suspended, or null.
    ///
    /// Reactivating is always safe — it only ever adds somebody who can sign
    /// in — so this is asked only of a suspension.
    /// </summary>
    public async Task<string?> WhyNotSuspendAsync(User user, User actingAs)
    {
        if (user.UserId == actingAs.UserId)
        {
            return "You cannot suspend your own account.";
        }

        if (await IsLastSuperadminAsync(user))
        {
            return "That is the last Super Administrator who can still sign in. "
                + "Suspending them would lock the unit out of its own library.";
        }

        return null;
    }

    public async Task SetActiveAsync(User user, bool active, long byUserId)
    {
        user.IsActive = active;
        user.UpdatedAt = DateTime.Now;

        db.Users.Update(user);

        // The same two words the PHP application writes, so one Activity list
        // reads the same whichever program did it.
        Journal.Note(db, byUserId, active ? "USER_REACTIVATE" : "USER_DEACTIVATE",
            "user", user.UserId, new { user.Username, is_active = active });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Setting somebody's password for them.
    ///
    /// The only way a forgotten password is recovered: the deployment is
    /// air-gapped and has no mail path, so there is no self-service reset and
    /// there should not appear to be one. Hashed at the same cost the PHP
    /// application uses, so the new password works there at the next sign-in.
    ///
    /// <paramref name="adminPassword"/> is the acting administrator's own, and
    /// it is asked for every time. A session somebody walked away from is
    /// otherwise a way into every account in the library, and this is a
    /// deliberate enough act to be worth typing a password for. Returns what
    /// went wrong, or nothing.
    /// </summary>
    public async Task<string?> SetPasswordAsync(
        User user, string password, User actingAs, string adminPassword)
    {
        if (password.Length < 8)
        {
            return "A password of at least eight characters is needed.";
        }

        if (!Confirms(actingAs, adminPassword))
        {
            return "That is not your password — nothing was changed.";
        }

        user.PasswordHash = SignIn.Hash(password);
        user.UpdatedAt = DateTime.Now;

        db.Users.Update(user);

        // What happened, never what it was set to.
        Journal.Note(db, actingAs.UserId, "USER_PASSWORD_RESET", "user", user.UserId,
            new { reset_for = user.Username });

        await db.SaveAndForgetAsync();

        return null;
    }

    /// <summary>Whether this really is that person, asked before an act that needs to be theirs.</summary>
    public static bool Confirms(User user, string password)
    {
        try
        {
            return user.PasswordHash.StartsWith("$2")
                && BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this is the last superadmin who can still sign in.
    ///
    /// Counted live rather than cached: the answer changes as accounts are
    /// suspended, and a stale answer here is the difference between a locked
    /// door and an open one.
    /// </summary>
    private async Task<bool> IsLastSuperadminAsync(User user)
    {
        if (user.Role != UserRole.SUPERADMIN || !user.IsActive)
        {
            return false;
        }

        var others = await db.Users.CountAsync(u =>
            u.Role == UserRole.SUPERADMIN && u.IsActive && u.UserId != user.UserId);

        return others == 0;
    }

    /// <summary>
    /// When somebody last signed in, and whether anybody has for a while.
    ///
    /// An account nobody has used for months is a way in that nobody is
    /// watching, which is why it is worth saying on the screen rather than
    /// leaving it to be noticed.
    /// </summary>
    public static string LastSeen(User user, DateOnly today)
    {
        if (user.LastLoginAt is not { } at)
        {
            return "has never signed in";
        }

        var day = DateOnly.FromDateTime(at);

        return day == today
            ? $"signed in today at {at:HH:mm}"
            : $"last signed in {day:dd MMM yyyy}";
    }

    public static bool Stale(User user, DateOnly today) =>
        user.IsActive
        && (user.LastLoginAt is null
            || today.DayNumber - DateOnly.FromDateTime(user.LastLoginAt.Value).DayNumber > 90);
}
