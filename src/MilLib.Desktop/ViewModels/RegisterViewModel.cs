using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The accession register.
///
/// The library's legal ledger, on screen exactly as it prints: every physical
/// book it has ever taken on, in the order the numbers were handed out. Nothing
/// on this screen edits anything — a correction is a note against the copy, made
/// on the book's own screen, and it shows up here in the remarks column without
/// disturbing the original entry.
/// </summary>
public partial class RegisterViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _extent = "";

    private int _first;
    private int _last;

    public RegisterViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<RegisterLine> Lines { get; } = [];

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool Nothing => !Busy && Lines.Count == 0;

    public string Tally => Busy
        ? "Reading the register…"
        : Lines.Count == 0
            ? "Nothing in that range"
            : $"{Lines.Count:N0} entries · {Session.Preferences.Money(Lines.Sum(l => l.Value))} of stock";

    /// <summary>
    /// Raised when the register should be written out. The view answers it,
    /// because choosing where a file goes needs a window to ask from.
    /// </summary>
    public event Func<IReadOnlyList<RegisterEntry>, string, Task>? Print;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    /// <summary>What the range boxes currently mean, said in words on the button.</summary>
    public string RangeText
    {
        get
        {
            var (from, to) = Range();

            // Written as a switch on the pair so the compiler can see that the
            // last arm has a value; the nested conditional it replaced was
            // correct but the compiler could not prove it.
            return (from, to) switch
            {
                (null, null) => "the whole register",
                (not null, not null) => $"{Number(from.Value)} to {Number(to.Value)}",
                (not null, null) => $"{Number(from.Value)} onwards",
                (null, not null) => $"up to {Number(to.Value)}",
            };
        }
    }

    partial void OnFromChanged(string value) => OnPropertyChanged(nameof(RangeText));

    partial void OnToChanged(string value) => OnPropertyChanged(nameof(RangeText));

    private (int? From, int? To) Range() =>
        (int.TryParse(From, out var from) ? from : null,
         int.TryParse(To, out var to) ? to : null);

    private static string Number(int seq) =>
        Session.Preferences.Accession(seq.ToString(
            new string('0', Math.Max(1, Session.Preferences.AccessionPadLength))));

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task WholeRegisterAsync()
    {
        From = "";
        To = "";

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var register = new Register(db, Session.Preferences);

            (_first, _last) = await register.ExtentAsync();

            // The plain numbers, not the printed form.
            //
            // This line is a hint about what to type in the two boxes, and the
            // boxes take the bare sequence. Offering "JAKLI/000001001" here
            // invited typing exactly that, which is not what the register was
            // imported as and not what the boxes accept.
            Extent = _last == 0
                ? "Nothing has been accessioned yet."
                : $"The register runs from {_first:N0} to {_last:N0}.";

            var (from, to) = Range();

            Lines.Clear();

            foreach (var entry in await register.ReadAsync(from, to))
            {
                Lines.Add(new RegisterLine(entry));
            }
        }
        catch (Exception ex)
        {
            Faults.Record("reading the accession register", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Tally));
            OnPropertyChanged(nameof(Nothing));
        }
    }

    /// <summary>
    /// Write it out as it stands.
    ///
    /// The rows are re-read rather than taken off the screen, so what is printed
    /// is the register at the moment somebody asked for it — not what the screen
    /// happened to be showing since it was last refreshed.
    /// </summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Print is null || Busy)
        {
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await using var db = Workspace.Open();

            var (from, to) = Range();

            var entries = await new Register(db, Session.Preferences).ReadAsync(from, to);

            if (entries.Count == 0)
            {
                Said = "There is nothing in that range to print.";
                SaidIsGood = false;

                return;
            }

            await Print(entries, RangeText);
        }
        catch (Exception ex)
        {
            Faults.Record("printing the accession register", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>One line of the register, as the screen shows it.</summary>
public record RegisterLine(RegisterEntry Entry)
{
    public string Accession => Entry.Accession;

    public string BookNo => Entry.BookNo ?? "";

    public string Date => Entry.AccessionedOn.ToString("dd/MM/yyyy");

    public string Author => Entry.Author;

    public string Title => Entry.Title;

    public string Publisher => Entry.Publisher ?? "";

    public string Source => Words.Of(Entry.Source);

    public string Cost => Entry.Cost is null ? "" : Entry.Cost.Value.ToString("N2");

    public decimal Value => Entry.Cost ?? 0m;

    /// <summary>
    /// Only a copy that is not simply on the shelf earns a word here. That is
    /// what makes the column worth reading down at all.
    /// </summary>
    public string Remarks => Entry.Status == CopyStatus.AVAILABLE ? "" : Words.Of(Entry.Status);

    public bool HasRemark => Remarks.Length > 0;
}
