namespace MilLib.Desktop.Services;

/// <summary>
/// A stand-in licence salt, used when no real one has been supplied.
///
/// The real salt is the shared secret of the licensing scheme, so it is not
/// kept in source that anybody can read. The vendor drops a git-ignored
/// <c>LicenceSecret.cs</c> beside this file with the real value; the build then
/// leaves this stand-in out (see the <c>.csproj</c>). A build without it — a
/// public clone — compiles and runs, but the keys it mints and checks are
/// worthless, which is the point: only the vendor's own build produces a
/// licensable executable.
///
/// To build a working, licensable copy, create <c>LicenceSecret.cs</c> in this
/// folder declaring the same class with the real <c>Salt</c>.
/// </summary>
internal static class LicenceSecret
{
    public const string Salt = "PLACEHOLDER-not-the-real-salt-build-your-own";
}
