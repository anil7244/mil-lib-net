using MilLib.Core.Documents;

namespace MilLib.Desktop.Services;

/// <summary>
/// The unit's name and crest, assembled for whatever is about to be printed.
///
/// It comes from two places at once — the names and the accent colour are
/// settings in the database, the crest is a file on this machine — so it is put
/// together here rather than in each document. One heading, whichever document
/// is being produced.
/// </summary>
public static class Letterheads
{
    public static Letterhead Current()
    {
        var preferences = Session.Preferences;

        // A library that has not been branded still prints a heading. A page
        // that comes out with a blank where the unit's name should be looks
        // like a fault; one that says "Library" looks like a library.
        var organisation = preferences.OrganisationName.Length > 0
            ? preferences.OrganisationName
            : preferences.LibraryName;

        return new Letterhead(
            organisation,
            preferences.LibraryName,
            preferences.Motto,
            Workspace.CrestPath(preferences.CrestPath),
            preferences.AccentColour);
    }
}
