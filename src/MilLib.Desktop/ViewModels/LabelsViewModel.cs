using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Labels for the books.
///
/// Two paths out of one screen: a sheet of labels for an ordinary printer, and
/// ZPL for a Zebra. The sheet is the normal case — most unit libraries have one
/// laser printer and a packet of blank sticky labels — and the thermal path is
/// there for the ones that bought the printer.
///
/// Picking is by ticking, because labelling is a batch job: a box of books
/// arrives, they are accessioned together, and the labels for that run are
/// printed on one sheet.
/// </summary>
public partial class LabelsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";

    [ObservableProperty] private bool _pocket = true;
    [ObservableProperty] private LabelCode _code = LabelCode.Barcode;
    [ObservableProperty] private string _stock = "";

    private int _keystroke;

    public LabelsViewModel()
    {
        _ = LoadAsync();
    }

    public ObservableCollection<LabelRow> Rows { get; } = [];

    public LabelCode[] Codes { get; } = Enum.GetValues<LabelCode>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasSaid => Said.Length > 0;

    public bool Nothing => !Busy && Rows.Count == 0;

    public int Chosen => Rows.Count(r => r.IsChosen);

    public bool AnyChosen => Chosen > 0;

    public string Tally => Busy
        ? "Looking…"
        : Rows.Count == 0
            ? "Nothing found"
            : Chosen == 0
                ? $"{Rows.Count:N0} copies — tick the ones to label"
                : $"{Chosen:N0} of {Rows.Count:N0} ticked";

    /// <summary>Raised to write a sheet. The view answers it — it needs a window to ask from.</summary>
    public event Func<IReadOnlyList<LabelFor>, LabelKind, LabelCode, float, float, Task>? PrintSheet;

    /// <summary>Raised to write the Zebra's own instructions out as a file.</summary>
    /// <summary>
    /// Raised to write the thermal-printer instructions. The stock goes with
    /// it — worked out here, where the settings are already open, rather than
    /// left for the view to find out on its own.
    /// </summary>
    public event Func<IReadOnlyList<LabelFor>, LabelKind, Stock, Task>? SaveZpl;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    partial void OnSearchChanged(string value) => _ = LookSoonAsync();

    partial void OnPocketChanged(bool value) => OnPropertyChanged(nameof(StockNow));

    /// <summary>The stock this kind of label is set to, said on the screen.</summary>
    public string StockNow => Stock;

    private async Task LookSoonAsync()
    {
        var mine = ++_keystroke;

        await Task.Delay(200);

        if (mine == _keystroke)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var labelling = new Labelling(db, Session.Preferences);

            Code = labelling.Code;

            Stock = $"Pocket {labelling.PocketWidthMm:0.#} × {labelling.PocketHeightMm:0.#} mm  ·  "
                + $"Spine {labelling.SpineWidthMm:0.#} × {labelling.SpineHeightMm:0.#} mm  "
                + "(set on the Settings screen)";

            Show(await labelling.FindAsync(Search));
        }
        catch (Exception ex)
        {
            Faults.Record("looking for copies to label", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Everything in a stretch of the register.
    ///
    /// This is how a new intake gets labelled: the copies were just accessioned
    /// as a block, so the numbers are known and searching for them one at a
    /// time would be silly.
    /// </summary>
    [RelayCommand]
    private async Task TakeRangeAsync()
    {
        if (!int.TryParse(From, out var from) || !int.TryParse(To, out var to))
        {
            Said = "Give both ends of the range — the plain numbers, without the prefix.";
            SaidIsGood = false;

            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var found = await new Labelling(db, Session.Preferences).InRangeAsync(from, to);

            Show(found);

            // A range is asked for because every one of them is wanted.
            foreach (var row in Rows)
            {
                row.IsChosen = true;
            }

            Changed();

            Said = found.Count == 0
                ? "Nothing in that range."
                : $"{found.Count} copies in that range, all ticked.";

            SaidIsGood = found.Count > 0;
        }
        catch (Exception ex)
        {
            Faults.Record("taking a range of copies", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void TickAll()
    {
        var turningOn = Chosen < Rows.Count;

        foreach (var row in Rows)
        {
            row.IsChosen = turningOn;
        }

        Changed();
    }

    /// <summary>Called by the view when one tick changes, so the count keeps up.</summary>
    public void Changed()
    {
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(AnyChosen));
        OnPropertyChanged(nameof(Tally));
    }

    [RelayCommand]
    private async Task SheetAsync()
    {
        if (PrintSheet is null || !AnyChosen)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var labelling = new Labelling(db, Session.Preferences);

            var kind = Pocket ? LabelKind.Pocket : LabelKind.Spine;

            var (width, height) = Pocket
                ? (labelling.PocketWidthMm, labelling.PocketHeightMm)
                : (labelling.SpineWidthMm, labelling.SpineHeightMm);

            await PrintSheet(Picked(), kind, Code, width, height);
        }
        catch (Exception ex)
        {
            Faults.Record("printing a sheet of labels", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    [RelayCommand]
    private async Task ZebraAsync()
    {
        if (SaveZpl is null || !AnyChosen)
        {
            return;
        }

        try
        {
            await using var db = Workspace.Open();

            var labelling = new Labelling(db, Session.Preferences);

            var kind = Pocket ? LabelKind.Pocket : LabelKind.Spine;

            await SaveZpl(Picked(), kind, labelling.StockFor(kind));
        }
        catch (Exception ex)
        {
            Faults.Record("writing the Zebra instructions", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
    }

    private IReadOnlyList<LabelFor> Picked() =>
        [.. Rows.Where(r => r.IsChosen).Select(r => r.Label)];

    private void Show(IReadOnlyList<(Copy Copy, Title Title)> found)
    {
        Rows.Clear();

        // Rebuilt from scratch on every search, so a tick does not survive into
        // a different set of results — printing labels for a book somebody
        // ticked two searches ago and forgot about is a wasted label and a
        // wrong one.
        foreach (var (copy, title) in found)
        {
            var row = new LabelRow(
                new LabelFor(
                    Session.Preferences.Accession(copy.AccessionNo),
                    copy.Barcode,
                    title.Name,
                    title.CallNumber ?? ""),
                copy.AccessionSeq);

            // Listened to here, as the row is made.
            //
            // The view used to do this by watching the collection, and it did
            // not work: the rows are loaded from the constructor, so they were
            // all in place before the view existed to hear about them. Ticking
            // three then left the count saying nothing was ticked and both
            // buttons greyed out.
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LabelRow.IsChosen))
                {
                    Changed();
                }
            };

            Rows.Add(row);
        }

        Changed();

        OnPropertyChanged(nameof(Nothing));
    }
}

/// <summary>One copy, and whether it is going on the sheet.</summary>
public partial class LabelRow(LabelFor label, int? seq) : ObservableObject
{
    [ObservableProperty] private bool _isChosen;

    public LabelFor Label { get; } = label;

    public string Accession => Label.Accession;

    public string Title => Label.Title;

    public string CallNumber => Label.CallNumber;

    public string Number => seq?.ToString() ?? "";

    public bool HasCallNumber => CallNumber.Length > 0;
}
