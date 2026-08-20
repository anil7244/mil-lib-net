using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class DatabaseView : UserControl
{
    public DatabaseView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not DatabaseViewModel model)
        {
            return;
        }

        model.PickFile -= ChooseAsync;
        model.PickFile += ChooseAsync;

        model.Reveal -= Open;
        model.Reveal += Open;
    }

    private async Task<string?> ChooseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the library's data file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite database")
                {
                    Patterns = ["*.sqlite", "*.db", "*.sqlite3"],
                },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>
    /// Show the folder in Explorer. Somebody who has come to look at their
    /// backups very often wants to copy one onto a stick, and this is the
    /// shortest route to doing that.
    /// </summary>
    private static void Open(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.Faults.Record("opening " + folder, ex);
        }
    }
}
