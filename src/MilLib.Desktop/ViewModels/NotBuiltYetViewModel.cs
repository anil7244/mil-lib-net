using CommunityToolkit.Mvvm.ComponentModel;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// A screen that is on the menu but has not been written yet.
///
/// It exists so the shape of the finished application can be walked through
/// while it is being built, and so nobody has to guess whether a missing screen
/// is missing or broken. It says which one it is and where the work stands.
///
/// Every one of these is a phase of the port that has not landed. When the last
/// of them goes, so does this class.
/// </summary>
public partial class NotBuiltYetViewModel(string section) : ViewModelBase
{
    [ObservableProperty] private string _section = section;

    public string Explanation =>
        $"The {Section} screen has not been built in the desktop application yet. "
        + "It is there in the web application, and the records behind it are already "
        + "in this data file — only this screen is outstanding.";
}
