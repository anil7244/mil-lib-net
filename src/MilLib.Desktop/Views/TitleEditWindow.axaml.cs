using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class TitleEditWindow : Window
{
    /// <summary>What was saved, for whoever opened this window to go and show it.</summary>
    public long SavedTitleId { get; private set; }

    /// <summary>For the XAML designer, which can only construct with no arguments.</summary>
    public TitleEditWindow() : this(null)
    {
    }

    public TitleEditWindow(long? titleId)
    {
        InitializeComponent();

        // Cataloguing a new work and correcting an existing one are the same
        // form doing two different jobs, and the title bar should say which.
        Title = titleId is null ? "Catalogue a book" : "The book's record";

        var model = new TitleEditViewModel(titleId);

        model.Saved += id =>
        {
            SavedTitleId = id;

            Close();
        };

        model.Abandoned += Close;
        model.PickCover += ChooseAsync;

        DataContext = model;
    }

    /// <summary>
    /// Choosing a cover off the disk. The window does it because a file dialog
    /// needs one to hang off, and the view model has none.
    /// </summary>
    private async Task<string?> ChooseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the cover",
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
