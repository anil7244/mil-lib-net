using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Where the records are, and the copies kept of them.
///
/// Two jobs on one screen because they are the same subject, and because a
/// person who has come here to worry about their data should find both answers
/// in one place.
///
/// The second job is the one that matters. A unit's whole library is one file:
/// fourteen hundred books, every copy on the shelves, and every loan any of
/// them was ever on. Everything else in this application can be typed in again.
/// That file cannot, and until this screen existed there was no way to copy it
/// safely from inside the application at all.
/// </summary>
public partial class DatabaseViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // ------------------------------------------------------- the connection --

    /// <summary>
    /// Which of the three the library is on. One list rather than a checkbox
    /// and then a second choice underneath it, because "file, or which server"
    /// is one question and a person answering it should be asked it once.
    /// </summary>
    [ObservableProperty] private int _kindChosen;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _port = "3306";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _tried = "";
    [ObservableProperty] private bool _triedWorked;

    // ---------------------------------------------------------- the copies --
    [ObservableProperty] private bool _backupsOn;
    [ObservableProperty] private int _everyHours = 24;
    [ObservableProperty] private int _keep = 14;
    [ObservableProperty] private BackupFile? _chosen;
    [ObservableProperty] private string _lastBackup = "";
    [ObservableProperty] private string _occupies = "";

    /// <summary>
    /// What a restore is about to do, shown and confirmed before it is done.
    /// Empty when nothing is being confirmed.
    /// </summary>
    [ObservableProperty] private string _confirming = "";

    public DatabaseViewModel()
    {
        Load();
    }

    public ObservableCollection<BackupFile> Backups { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool HasSaid => Said.Length > 0;

    public bool HasTried => Tried.Length > 0;

    public bool Confirm => Confirming.Length > 0;

    public bool MayManage => Session.Can(Ability.SettingsManage);

    /// <summary>
    /// Whether backups are this application's business at all. On a server they
    /// are not — see <see cref="Services.Backups"/> — and the screen says so
    /// rather than offering a button that would produce something that looks
    /// like a backup and is not one.
    /// </summary>
    public bool MayBackUp => Services.Backups.Possible;

    public bool NoBackups => Backups.Count == 0;

    /// <summary>What this copy is reading now, in one line.</summary>
    public string Reading => Workspace.Source.ToString();

    public string Origin => Workspace.UsesFile
        ? $"Found because it is {Workspace.Origin}."
        : "This copy reads a server, so several people share one set of books.";

    public string BackupFolder => Services.Backups.Folder;

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnTriedChanged(string value) => OnPropertyChanged(nameof(HasTried));

    partial void OnConfirmingChanged(string value) => OnPropertyChanged(nameof(Confirm));

    partial void OnKindChosenChanged(int value)
    {
        Tried = "";
        OnPropertyChanged(nameof(OnAServer));

        // The port follows the kind, because nobody remembers 5432 and a
        // stale 3306 left behind by the previous choice fails with a timeout
        // rather than with an explanation.
        var wanted = PointedAt().EffectivePort;

        if (wanted > 0 && int.TryParse(Port, out var showing) && showing != wanted
            && showing is 0 or 3306 or 5432)
        {
            Port = wanted.ToString();
        }
    }

    /// <summary>The kind of database the list is pointing at.</summary>
    private DatabaseSource PointedAt() => new() { Kind = KindOf(KindChosen) };

    private static DatabaseKind KindOf(int index) => index switch
    {
        1 => DatabaseKind.MySql,
        2 => DatabaseKind.Postgres,
        _ => DatabaseKind.Sqlite,
    };

    private static int IndexOf(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => 1,
        DatabaseKind.Postgres => 2,
        _ => 0,
    };

    /// <summary>Whether the boxes on show should be the server ones.</summary>
    public bool OnAServer => KindOf(KindChosen) != DatabaseKind.Sqlite;

    private void Load()
    {
        var settings = Installation.Current;

        KindChosen = IndexOf(settings.Kind);
        FilePath = settings.FilePath.Length > 0 ? settings.FilePath : Workspace.DatabasePath;
        Host = settings.Host;
        Port = (settings.Port > 0 ? settings.Port : PointedAt().EffectivePort).ToString();
        Database = settings.Database;
        Username = settings.Username;

        // Deliberately not filled in. The stored password is decryptable by
        // this Windows account, so putting it in the box would be honest — but
        // it would also put it on a screen somebody can walk past. It is only
        // asked for when the connection is being changed.
        Password = "";

        BackupsOn = settings.BackupsOn;
        EveryHours = Math.Max(1, settings.BackupEveryHours);
        Keep = Math.Max(1, settings.BackupsToKeep);

        LastBackup = DateTime.TryParse(settings.LastBackupAt, out var last)
            ? $"Last copy taken {last:dd MMM yyyy 'at' HH:mm}."
            : "No copy has been taken yet.";

        Refresh();
    }

    private void Refresh()
    {
        Backups.Clear();

        foreach (var backup in Services.Backups.List())
        {
            Backups.Add(backup);
        }

        Occupies = Backups.Count == 0
            ? "Nothing kept yet."
            : $"{Backups.Count} {(Backups.Count == 1 ? "copy" : "copies")} kept, "
              + Weight(Backups.Sum(b => b.Size)) + " in all.";

        OnPropertyChanged(nameof(NoBackups));
    }

    private static string Weight(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:N0} KB"
        : $"{bytes / 1024.0 / 1024.0:N1} MB";

    /// <summary>The connection as the boxes currently describe it.</summary>
    private DatabaseSource Described() => new()
    {
        Kind = KindOf(KindChosen),
        FilePath = FilePath.Trim(),
        Host = Host.Trim(),
        Port = int.TryParse(Port, out var port) ? port : 0,
        Database = Database.Trim(),
        Username = Username.Trim(),
        Password = Password.Length > 0 ? Password : Installation.Current.Password,
    };

    /// <summary>Raised when a data file needs choosing. The view answers it.</summary>
    public event Func<Task<string?>>? PickFile;

    /// <summary>Raised when the folder of copies should be opened.</summary>
    public event Action<string>? Reveal;

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (PickFile is null)
        {
            return;
        }

        var chosen = await PickFile();

        if (chosen is not null)
        {
            FilePath = chosen;
            Tried = "";
        }
    }

    /// <summary>
    /// Try it before saving it.
    ///
    /// A connection saved untested is one that fails at the next start, on a
    /// screen with no way to put it right — because putting it right needs the
    /// application, and the application will not open. So it is tried here,
    /// while there is still a working connection to come back to.
    /// </summary>
    [RelayCommand]
    private async Task TryItAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Problems.Clear();
        Tried = "";

        try
        {
            var source = Described();

            foreach (var problem in source.Problems())
            {
                Problems.Add(problem);
            }

            if (Problems.Count > 0)
            {
                OnPropertyChanged(nameof(HasProblems));

                return;
            }

            await using var db = Workspace.OpenOther(source);

            // Counting the books rather than merely connecting. A server that
            // answers but holds an empty schema is not this library, and
            // finding that out now is far better than finding it out from a
            // Home screen reporting no books.
            var titles = await db.Titles.CountAsync();
            var copies = await db.Copies.CountAsync();

            TriedWorked = true;
            Tried = $"That works — {titles:N0} books and {copies:N0} copies on the register.";
        }
        catch (Exception ex)
        {
            Faults.Record("trying a database connection", ex);

            TriedWorked = false;
            Tried = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (Busy || !MayManage)
        {
            return;
        }

        // Only after it has been tried, and only if it worked. This is the one
        // setting in the application that can stop the application opening.
        if (!TriedWorked)
        {
            Announce("Try the connection first. This is the one setting that can stop "
                + "the application opening, so it is not saved until it is known to work.", false);

            return;
        }

        try
        {
            var settings = Installation.Current;
            var source = Described();

            settings.Provider = Installation.NameOf(source.Kind);
            settings.FilePath = source.FilePath;
            settings.Host = source.Host;
            settings.Port = source.Port;
            settings.Database = source.Database;
            settings.Username = source.Username;

            if (Password.Length > 0)
            {
                settings.Password = Password;
            }

            settings.Save();

            Announce("Saved. It takes effect at the next sign-in — this session goes on "
                + "reading what it opened with.", true);

            OnPropertyChanged(nameof(Reading));
            OnPropertyChanged(nameof(Origin));
            OnPropertyChanged(nameof(MayBackUp));
        }
        catch (Exception ex)
        {
            Faults.Record("saving the database connection", ex);

            Announce(Faults.Explain(ex), false);
        }
    }

    // ---------------------------------------------------------- the copies --

    [RelayCommand]
    private async Task SaveBackupSettingsAsync()
    {
        if (!MayManage)
        {
            return;
        }

        var settings = Installation.Current;

        settings.BackupsOn = BackupsOn;
        settings.BackupEveryHours = Math.Max(1, EveryHours);
        settings.BackupsToKeep = Math.Max(1, Keep);
        settings.Save();

        Announce(BackupsOn
            ? $"A copy will be taken at sign-in when the last one is more than "
              + $"{EveryHours} hour{(EveryHours == 1 ? "" : "s")} old."
            : "Automatic copies are off. Nothing will be kept unless somebody takes it.", true);

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            var (path, problem) = await Services.Backups.TakeAsync(byHand: true);

            Announce(problem ?? $"Copy taken — {Path.GetFileName(path)}.", problem is null);
        }
        finally
        {
            Busy = false;

            Load();
        }
    }

    /// <summary>
    /// Ask before putting a backup back.
    ///
    /// Not a dialog somebody clicks through: the question names the copy, says
    /// what today's records become, and has to be answered on the screen the
    /// list is on.
    /// </summary>
    [RelayCommand]
    private void AskToRestore()
    {
        if (Chosen is null)
        {
            return;
        }

        Confirming = $"Put {Chosen.Name} back, as the library stood on "
            + $"{Chosen.When}? Everything recorded since then goes — "
            + "loans, returns, fines, books catalogued. Today's records are kept as a copy first, "
            + "so this can be undone, but only by restoring that one.";
    }

    [RelayCommand]
    private void KeepThingsAsTheyAre() => Confirming = "";

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Chosen is null || Busy)
        {
            return;
        }

        Busy = true;
        Confirming = "";

        try
        {
            var (aside, problem) = await Services.Backups.RestoreAsync(Chosen);

            if (problem is not null)
            {
                Announce(problem, false);

                return;
            }

            Announce($"{Chosen.Name} is now the library. What was here has been kept as "
                + $"{Path.GetFileName(aside)}. Sign out and back in — everything on screen is "
                + "still showing what was there a moment ago.", true);
        }
        catch (Exception ex)
        {
            Faults.Record("putting a backup back", ex);

            Announce(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;

            Load();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Services.Backups.Folder);

            Reveal?.Invoke(Services.Backups.Folder);
        }
        catch (Exception ex)
        {
            Faults.Record("opening the backup folder", ex);

            Announce(Faults.Explain(ex), false);
        }
    }

    [RelayCommand]
    private void RefreshList() => Load();

    private void Announce(string said, bool good)
    {
        Said = said;
        SaidIsGood = good;
    }
}
