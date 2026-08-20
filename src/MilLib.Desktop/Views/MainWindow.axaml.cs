using Avalonia.Controls;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();
    }

    private void Watch()
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        model.OpenKiosk -= OpenKiosk;
        model.OpenKiosk += OpenKiosk;
    }

    /// <summary>
    /// Hand the machine to the reading room.
    ///
    /// Shown as a dialog over this window, which is what makes it a kiosk: the
    /// library system behind it cannot be clicked on while the terminal is up,
    /// and the terminal will not close without a staff password.
    /// </summary>
    private async void OpenKiosk()
    {
        var kiosk = new KioskWindow();

        await kiosk.ShowDialog(this);
    }
}
