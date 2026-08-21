using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Licensing;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Entering a licence key from the front door.
///
/// The activation screen inside the application sits behind the sign-in, and a
/// copy whose trial has run out cannot be signed into — so a unit standing at a
/// locked machine on a Monday morning could see that it needed a key and have
/// no way to type one. This is that way in: reached from the sign-in screen,
/// needing no account, because a key is useless on any machine but the one it
/// was cut for, and the person holding it is standing at that machine.
/// </summary>
public partial class KeyEntryViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private bool _copied;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _grave;
    [ObservableProperty] private string _hardwareId = "";

    /// <summary>True once a key has been accepted, so the caller can move on.</summary>
    [ObservableProperty] private bool _done;

    private string _library = "";
    private string _unit = "";

    public string Product => $"{Vendor.Product} {Vendor.Version}";

    public string Company => Vendor.Company;

    public string Phone => Vendor.Phone;

    public string Email => Vendor.Email;

    public bool HasSaid => Said.Length > 0;

    /// <summary>
    /// What to send when asking for a key — the whole block, copied in one go
    /// rather than transcribed field by field.
    /// </summary>
    public string ToSend
    {
        get
        {
            var lines = new List<string>
            {
                $"{Vendor.Product} {Vendor.Version}",
                $"Hardware ID: {HardwareId}",
            };

            if (_library.Length > 0)
            {
                lines.Add($"Library: {_library}");
            }

            if (_unit.Length > 0)
            {
                lines.Add($"Unit: {_unit}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Raised when the block above should go to the clipboard.</summary>
    public event Func<string, Task>? Copy;

    /// <summary>Raised once a key has been accepted.</summary>
    public event Action? Activated;

    public KeyEntryViewModel()
    {
        _ = LoadAsync();
    }

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnHardwareIdChanged(string value) => OnPropertyChanged(nameof(ToSend));

    private async Task LoadAsync()
    {
        Busy = true;

        try
        {
            var standing = await Licensing.RefreshAsync();

            HardwareId = standing.HardwareId;
            Headline = standing.Headline;
            Grave = standing.Grave;

            await using var db = Workspace.Open();

            var preferences = await Preferences.ReadAsync(db);

            _library = preferences.LibraryName;
            _unit = preferences.OrganisationName;

            OnPropertyChanged(nameof(ToSend));
        }
        catch (Exception ex)
        {
            Faults.Record("reading the licence", ex);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (Busy)
        {
            return;
        }

        if (Key.Trim().Length == 0)
        {
            Said = "Type the licence key first.";
            SaidIsGood = false;
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var (ok, said) = await Licensing.For(db)
                .ActivateAsync(Key, DateOnly.FromDateTime(DateTime.Now));

            Said = said;
            SaidIsGood = ok;

            if (ok)
            {
                Key = "";

                var standing = await Licensing.RefreshAsync();
                Headline = standing.Headline;
                Grave = standing.Grave;

                Done = true;
                Activated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Faults.Record("entering a licence key", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task CopyDetailsAsync()
    {
        if (Copy is null)
        {
            return;
        }

        await Copy(ToSend);

        Copied = true;
    }
}
