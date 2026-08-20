using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class MemberEditWindow : Window
{
    /// <summary>For the XAML designer, which can only construct with no arguments.</summary>
    public MemberEditWindow() : this(null)
    {
    }

    public MemberEditWindow(long? memberId)
    {
        InitializeComponent();

        var model = new MemberEditViewModel(memberId);

        // Saved and abandoned both close it. Which one happened is the list's
        // business — it reloads either way, and reloading after a cancel costs
        // nothing and cannot be wrong.
        model.Saved += Close;
        model.Abandoned += Close;
        model.PickPhoto += ChooseAsync;

        DataContext = model;
    }

    /// <summary>
    /// Choosing a photograph off the disk. The window does it because a file
    /// dialog needs one to hang off, and the view model has none.
    /// </summary>
    private async Task<string?> ChooseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the photograph",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Picture")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png"],
                },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
