using Avalonia.Controls;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class LoginWindow : Window
{
    /// <summary>
    /// What to open once somebody is let in. Supplied by the caller so this
    /// window does not need to know which of the application's screens was
    /// asked for on the command line.
    /// </summary>
    private readonly Func<Window> _next;

    /// <summary>
    /// For the XAML designer, which can only construct a window with no
    /// arguments. Nothing in the application uses this.
    /// </summary>
    public LoginWindow() : this(() => new MainWindow { DataContext = new MainViewModel() })
    {
    }

    public LoginWindow(Func<Window> next)
    {
        _next = next;

        InitializeComponent();

        var model = new LoginViewModel();

        model.Allowed += OnAllowed;
        model.EnterKeyAsked += OnEnterKeyAsked;

        DataContext = model;

        // The cursor starts where the typing starts.
        Opened += (_, _) => this.FindControl<TextBox>("UsernameBox")?.Focus();
    }

    /// <summary>
    /// Open the key-entry dialog from the front door, and re-read the licence
    /// when it closes — so a copy just unlocked stops saying it is locked. The
    /// dialog needs no account, because a key works only on this machine.
    /// </summary>
    private async void OnEnterKeyAsked()
    {
        var dialog = new KeyEntryWindow();

        await dialog.ShowDialog(this);

        if (DataContext is LoginViewModel model)
        {
            await model.RefreshLicenceAsync();

            if (dialog.Activated)
            {
                this.FindControl<TextBox>("UsernameBox")?.Focus();
            }
        }
    }

    private async void OnAllowed()
    {
        // Asked once, here, and remembered for the session. Every screen puts
        // a banner up from the answer, and none of them should be working out
        // a hardware fingerprint for itself to do it.
        //
        // After the password, not before: what the licence controls is the
        // application, and telling somebody who cannot get in that the licence
        // has lapsed tells a stranger about the unit's contract.
        try
        {
            await Services.Licensing.RefreshAsync();
        }
        catch (Exception ex)
        {
            // A licence that could not be checked is not evidence of anything.
            // Locking a unit out of its own library because a read failed
            // would be the worst possible way to be wrong.
            Services.Faults.Record("checking the licence", ex);
        }

        // Taken after somebody is in, not before: a copy of the records is
        // itself the records, and it should not be produced for anyone who
        // could not open them.
        try
        {
            await Services.Backups.TakeIfDueAsync();
        }
        catch (Exception ex)
        {
            // Never at the cost of signing in.
            Services.Faults.Record("automatic backup", ex);
        }

        var window = _next();

        // Shown before this one closes. The application shuts down when its
        // last window goes, so closing first would end the process on a
        // successful sign-in.
        window.Show();

        Close();
    }
}
