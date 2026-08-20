using MilLib.Core.Data;

namespace MilLib.Desktop.Services;

/// <summary>
/// Who is signed in, for as long as the application is open, and what they are
/// allowed to do.
///
/// Nothing is written to disk and nothing is remembered between runs: closing
/// the application signs you out, which is the behaviour anybody would expect
/// of something that asked for a password to open.
///
/// Every screen asks <see cref="Can"/> rather than testing the role itself.
/// Roles change; the question "may this person issue a book" does not.
/// </summary>
public static class Session
{
    public static User? User { get; private set; }

    /// <summary>
    /// How this library is set up. Read at sign-in, because the settings are in
    /// the database and there is no database to read until somebody is in.
    /// </summary>
    public static Preferences Preferences { get; private set; } = Preferences.Empty;

    public static bool SignedIn => User is not null;

    public static string Name => User?.Display ?? "";

    public static string Initials => User?.Initials ?? "";

    public static UserRole Role => User?.Role ?? UserRole.READ_ONLY;

    public static string RoleName => User is null ? "" : Abilities.Label(User.Role);

    /// <summary>The machine, recorded against each sign-in and sign-out.</summary>
    public static string Machine => Environment.MachineName;

    public static bool Can(Ability ability) =>
        User is not null && Abilities.Can(User.Role, ability);

    /// <summary>Whether this install has the screen at all.</summary>
    public static bool Has(Feature feature) => Preferences.Has(feature);

    public static void Begin(User user, Preferences preferences)
    {
        User = user;
        Preferences = preferences;
    }

    /// <summary>Settings were saved; take the new ones without ending the session.</summary>
    public static void Refresh(Preferences preferences) => Preferences = preferences;

    public static void End()
    {
        User = null;
        Preferences = Preferences.Empty;
    }
}
