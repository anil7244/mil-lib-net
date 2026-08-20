using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not SettingsViewModel model)
        {
            return;
        }

        model.PickCrest -= ChooseAsync;
        model.PickCrest += ChooseAsync;
    }

    /// <summary>
    /// Choosing the crest off the disk. The view does it because a file dialog
    /// needs a window to hang off, and the view model has none.
    /// </summary>
    private async Task<string?> ChooseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the unit crest",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Picture")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"],
                },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
