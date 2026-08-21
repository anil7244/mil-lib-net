using Avalonia.Controls;
using Avalonia.Input.Platform;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class KeyEntryWindow : Window
{
    /// <summary>Whether a key was accepted while this dialog was open.</summary>
    public bool Activated { get; private set; }

    public KeyEntryWindow()
    {
        InitializeComponent();

        var model = new KeyEntryViewModel();

        model.Copy += CopyAsync;
        model.Activated += () => Activated = true;

        DataContext = model;

        // Close is a plain button rather than a command, because closing a
        // window is the window's own business, not the view model's.
        if (this.FindControl<Button>("CloseButton") is { } close)
        {
            close.Click += (_, _) => Close();
        }
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
