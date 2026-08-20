using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The reports.
///
/// Pick one on the left, see it on the right, and take it away as a PDF to file
/// or a spreadsheet to work on. The few reports that need telling something —
/// which member, which dates, grouped how — ask for it above the table rather
/// than on a separate screen first.
///
/// Every one of them is limited to what the person signed in is cleared to see.
/// That is not a setting and cannot be turned off.
/// </summary>
public partial class ReportsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private ReportChoice? _chosen;
    [ObservableProperty] private Report? _report;

    // ------------------------------------------------------- what to ask for
    [ObservableProperty] private string _member = "";
    [ObservableProperty] private HoldingsBy _by = HoldingsBy.MaterialType;
    [ObservableProperty] private DateTime? _from = DateTime.Today.AddMonths(-6);
    [ObservableProperty] private DateTime? _to = DateTime.Today;

    public ReportsViewModel()
    {
        // Grouped under the same two headings the web application uses, and in
        // that order — Circulation before Catalogue — rather than the order the
        // enum happens to declare them in. The first report under each heading
        // carries the heading, so the list reads as two short sections instead
        // of one flat six.
        var order = new[] { "Circulation", "Catalogue" };

        var allowed = Enum.GetValues<ReportKind>().Where(Allowed);

        var seen = new HashSet<string>();

        foreach (var kind in allowed
                     .OrderBy(k => Array.IndexOf(order, Reports.Section(k)))
                     .ThenBy(k => (int)k))
        {
            var section = Reports.Section(kind);

            Choices.Add(new ReportChoice(kind, seen.Add(section) ? section : ""));
        }

        Chosen = Choices.FirstOrDefault();
    }

    public ObservableCollection<ReportChoice> Choices { get; } = [];

    public ObservableCollection<ColumnHeading> Headings { get; } = [];

    public ObservableCollection<ReportLine> Lines { get; } = [];

    public HoldingsBy[] Groupings { get; } = Enum.GetValues<HoldingsBy>();

    public bool HasProblem => Problem.Length > 0;

    public bool HasReport => Report is not null && Lines.Count > 0;

    public bool Nothing => !Busy && Report is not null && Lines.Count == 0;

    public bool NeedsMember => Chosen?.Kind == ReportKind.MemberActivity;

    public bool NeedsGrouping => Chosen?.Kind == ReportKind.Holdings;

    public bool NeedsDates => Chosen?.Kind == ReportKind.Popular;

    public bool NeedsAnything => NeedsMember || NeedsGrouping || NeedsDates;

    public string Heading => Report?.Title ?? "";

    public string Note => Report?.Note ?? "";

    public bool HasNote => Note.Length > 0;

    public string Tally => Busy ? "Working…" : Report?.Tally ?? "";

    /// <summary>What this person is cleared to see, said plainly under the list.</summary>
    public string ClearedTo =>
        $"Limited to {Words.Of(Session.User?.ClearanceLevel ?? SecurityClass.UNCLASSIFIED)} "
        + "material and below — your clearance.";

    /// <summary>Raised to write the report out. The view answers it.</summary>
    public event Func<Report, bool, Task>? Save;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnChosenChanged(ReportChoice? value)
    {
        OnPropertyChanged(nameof(NeedsMember));
        OnPropertyChanged(nameof(NeedsGrouping));
        OnPropertyChanged(nameof(NeedsDates));
        OnPropertyChanged(nameof(NeedsAnything));

        _ = RunAsync();
    }

    partial void OnByChanged(HoldingsBy value) => _ = RunAsync();

    /// <summary>
    /// Which reports this person may ask for at all.
    ///
    /// The advanced ones and the classified one are separate permissions in the
    /// web application and stay separate here — and a report somebody cannot
    /// run is left off the list rather than shown greyed out, so nobody spends
    /// a year wondering what is behind it.
    /// </summary>
    private static bool Allowed(ReportKind kind) => kind switch
    {
        ReportKind.Popular => Session.Can(Ability.ReportsAdvanced) && Session.Has(Feature.ReportsAdvanced),
        ReportKind.Classified => Session.Has(Feature.Classified) && Session.Can(Ability.ReportsView),
        _ => Session.Can(Ability.ReportsView),
    };

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Chosen is null || Busy)
        {
            return;
        }

        Busy = true;
        Problem = "";

        OnPropertyChanged(nameof(Tally));

        try
        {
            await using var db = Workspace.Open();

            var reports = new Reports(db, Session.Preferences,
                Session.User?.ClearanceLevel ?? SecurityClass.UNCLASSIFIED);

            var ask = new ReportAsk(
                Chosen.Kind,
                Member,
                By,
                From is null ? null : DateOnly.FromDateTime(From.Value.Date),
                To is null ? null : DateOnly.FromDateTime(To.Value.Date));

            var ran = await reports.RunAsync(ask, Session.User!.UserId,
                DateOnly.FromDateTime(DateTime.Today));

            // Rows first, then the report.
            //
            // The screen redraws its table when Report changes, and the columns
            // it draws come from these collections — so setting Report first
            // drew the table of whichever report was showing before. Picking
            // Holdings left the overdue report's seven columns above two rows
            // of holdings.
            Show(ran);

            Report = ran;
        }
        catch (Exception ex)
        {
            Faults.Record("running a report", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(Tally));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(HasNote));
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(Nothing));
        }
    }

    [RelayCommand]
    private async Task PdfAsync()
    {
        if (Report is not null && Save is not null)
        {
            await Save(Report, true);
        }
    }

    [RelayCommand]
    private async Task CsvAsync()
    {
        if (Report is not null && Save is not null)
        {
            await Save(Report, false);
        }
    }

    private void Show(Report report)
    {
        Headings.Clear();
        Lines.Clear();

        for (var i = 0; i < report.Columns.Count; i++)
        {
            Headings.Add(new ColumnHeading(
                report.Columns[i],
                report.RightAligned is not null && i < report.RightAligned.Count && report.RightAligned[i]));
        }

        foreach (var row in report.Rows)
        {
            Lines.Add(new ReportLine([.. row]));
        }
    }
}

/// <summary>One report on the list, with what it is for.</summary>
public record ReportChoice(ReportKind Kind, string SectionHeader = "")
{
    public string Name => Reports.Name(Kind);

    public string About => Reports.Describe(Kind);

    /// <summary>The heading to print above this row, or empty for the rest of a section.</summary>
    public bool StartsSection => SectionHeader.Length > 0;

    /// <summary>The classified one is worth marking, because looking is recorded.</summary>
    public bool IsWatched => Kind == ReportKind.Classified;
}

public record ColumnHeading(string Name, bool RightAligned);

/// <summary>
/// One row, as cells.
///
/// The reports have between two and seven columns, so the screen cannot bind to
/// named properties — it builds its columns from the report it was given, the
/// same way the printed version does.
/// </summary>
public record ReportLine(IReadOnlyList<string> Cells);
