using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MilLib.Desktop.ViewModels;
using MilLib.Desktop.Views;

namespace MilLib.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];

            // Sign-in comes first, whichever screen was asked for. The support
            // switches below open the application on one screen, but it is
            // still the unit's library and still goes through the front door.
            desktop.MainWindow = new LoginWindow(() => Requested(args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The screen the command line asked for.
    ///
    /// The switches exist for support: talking somebody through a problem is
    /// easier when they can be sent straight to the screen in question rather
    /// than told which four things to click.
    /// </summary>
    private static Window Requested(string[] args)
    {
        var sectionArg = Array.IndexOf(args, "--section");

        var section = sectionArg >= 0 && sectionArg + 1 < args.Length
            ? args[sectionArg + 1]
            : args.Contains("--counter") ? "Issue & Return"
            : args.Contains("--books") ? "Books in Library"
            : args.Contains("--members") ? "Members"
            : null;

        return Shell(section);
    }

    /// <summary>
    /// The application proper. Signing out closes it and puts the sign-in
    /// screen back, so the same window is never handed from one person at the
    /// counter to the next still signed in as the first.
    /// </summary>
    private static Window Shell(string? section)
    {
        var model = new MainViewModel(section);
        var window = new MainWindow { DataContext = model };

        model.SignedOut += () =>
        {
            var login = new LoginWindow(() => Shell(null));

            login.Show();
            window.Close();
        };

        return window;
    }
}
