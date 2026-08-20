using Avalonia.Controls;
using Avalonia.Input.Platform;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class LicenceView : UserControl
{
    public LicenceView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not LicenceViewModel model)
        {
            return;
        }

        model.Copy -= CopyAsync;
        model.Copy += CopyAsync;
    }

    /// <summary>
    /// The clipboard belongs to the window, so the view does this. Copying the
    /// details is the whole point of the block it copies — a hardware ID
    /// transcribed by hand is a hardware ID typed back wrong.
    /// </summary>
    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
