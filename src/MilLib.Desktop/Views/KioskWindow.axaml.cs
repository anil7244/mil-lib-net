using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

/// <summary>
/// The reading-room terminal, as a window.
///
/// Everything here is about it being the only screen a stranger can reach: it
/// takes the whole display, it cannot be closed with the keyboard, and it
/// forgets whoever scanned in when they walk away.
/// </summary>
public partial class KioskWindow : Window
{
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(15) };

    private bool _mayClose;

    public KioskWindow()
    {
        InitializeComponent();

        var model = new KioskViewModel();

        model.Leaving += () =>
        {
            _mayClose = true;

            Close();
        };

        DataContext = model;

        // Alt+F4 and the rest are refused. Without this the kiosk is a window
        // with a search box in it, and behind it is a library system.
        Closing += (_, e) =>
        {
            if (!_mayClose)
            {
                e.Cancel = true;

                model.AskToLeaveCommand.Execute(null);
            }
        };

        // Any key or click counts as somebody being there. The clock that
        // clears the screen is only meaningful if using it restarts it.
        AddHandler(KeyDownEvent, (_, _) => model.Touch(), handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => model.Touch(), handledEventsToo: true);

        _watchdog.Tick += (_, _) => model.ForgetIfIdle();

        Opened += (_, _) =>
        {
            _watchdog.Start();

            this.FindControl<TextBox>("SearchBox")?.Focus();
        };

        Closed += (_, _) => _watchdog.Stop();
    }
}
