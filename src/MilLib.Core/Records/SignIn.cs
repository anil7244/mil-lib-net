using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// Letting somebody in, and writing down that it happened.
///
/// The accounts are the PHP application's, so the check has to be the same
/// check it makes: the same hash, and the same rule that a retired account is
/// refused as though the password were wrong. A sign-in that works here but
/// not there would make the two disagree about who works in this library.
///
/// What this is not: it is not protection of the records. They sit in a SQLite
/// file that anyone holding the folder can open with other tools. This gates
/// the application, not the file, and it should be described that way.
/// </summary>
public class SignIn(MilLibDbContext db)
{
    public async Task<SignInResult> AttemptAsync(string username, string password, string machine)
    {
        username = username.Trim();

        if (username.Length == 0 || password.Length == 0)
        {
            return SignInResult.Refused("Type a username and a password.");
        }

        // Matched without regard to case, and in memory rather than in SQL.
        //
        // SQLite compares text case-sensitively unless a column says otherwise;
        // the MySQL database this came from did not. So "SUPERADMIN" signed in
        // perfectly well in the web application and would be refused here — the
        // same person, the same password, a different answer. There is a
        // handful of accounts, so reading them and comparing properly costs
        // nothing and removes the difference.
        var user = (await db.Users.ToListAsync())
            .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        // Deliberately the same words whether the name is unknown or the
        // password is wrong. Telling someone which half they got right is
        // telling them half of it.
        const string refusal = "That username and password do not match an account.";

        if (user is null)
        {
            await RefuseAsync(null, $"No account named \"{username}\"", machine);

            return SignInResult.Refused(refusal);
        }

        if (!Verify(password, user.PasswordHash))
        {
            await RefuseAsync(user.UserId, "Wrong password", machine);

            return SignInResult.Refused(refusal);
        }

        if (!user.IsActive)
        {
            await RefuseAsync(user.UserId, "The account is suspended", machine);

            return SignInResult.Refused(
                "That account has been suspended and can no longer sign in. "
                + "Ask an administrator to reinstate it.");
        }

        var now = DateTime.Now;

        // Written straight to the row rather than through the change tracker,
        // which this connection does not keep.
        await db.Users
            .Where(u => u.UserId == user.UserId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.LastLoginAt, now));

        user.LastLoginAt = now;

        await Journal.NoteAloneAsync(db, user.UserId, "user_login", "User", user.UserId,
            new { at = "the desktop application", machine });

        return SignInResult.Allowed(user);
    }

    public async Task SignOutAsync(long userId, string machine) =>
        await Journal.NoteAloneAsync(db, userId, "user_logout", "User", userId,
            new { at = "the desktop application", machine });

    private async Task RefuseAsync(long? userId, string why, string machine) =>
        await Journal.NoteAloneAsync(db, userId, "failed_login", "User", userId,
            new { at = "the desktop application", why, machine });

    /// <summary>
    /// A stored hash that is not bcrypt at all — an empty column, or something
    /// left by an older scheme — must be a refusal, not an exception.
    /// </summary>
    private static bool Verify(string password, string hash)
    {
        try
        {
            return hash.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hashing a new password. Cost 12, which is what the PHP application uses
    /// — a password set here works there at the very next sign-in, and one set
    /// there works here.
    /// </summary>
    public static string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, 12);
}

public record SignInResult(bool Ok, User? User, string Problem)
{
    public static SignInResult Allowed(User user) => new(true, user, "");

    public static SignInResult Refused(string problem) => new(false, null, problem);
}
