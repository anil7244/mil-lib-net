using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Core.Records;
using MilLib.Desktop.Services;
using QuestPDF.Fluent;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// Books and members, out to a spreadsheet and back in.
///
/// One screen for the two directions people actually need: get the catalogue or
/// the roll into a file they can work on — a return upstairs, a printed list, a
/// backup somebody keeps in their own drawer — and put a filled-in file back
/// again when a library is first set up or a batch of new members arrives.
///
/// The file dialogs and the letterhead are the window's business, so the work
/// here produces the bytes and asks the view to put them somewhere; the reading
/// and writing of records goes through the same rules the screens use.
/// </summary>
public partial class ImportExportViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private PortSet _set = PortSet.Books;
    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    public ObservableCollection<string> Problems { get; } = [];

    public bool IsBooks => Set == PortSet.Books;

    public bool IsMembers => Set == PortSet.Members;

    public bool HasSaid => Said.Length > 0;

    public string Noun => Set == PortSet.Books ? "books" : "members";

    /// <summary>
    /// Whether this person may write records of the chosen kind. Anyone who can
    /// see them may take them out; only somebody who may catalogue books, or
    /// manage members, may bring a fileful in.
    /// </summary>
    public bool MayImport => Set == PortSet.Books
        ? Session.Can(Ability.CatalogueManage)
        : Session.Can(Ability.MembersManage);

    partial void OnSetChanged(PortSet value)
    {
        OnPropertyChanged(nameof(IsBooks));
        OnPropertyChanged(nameof(IsMembers));
        OnPropertyChanged(nameof(Noun));
        OnPropertyChanged(nameof(MayImport));

        Said = "";
        Problems.Clear();
    }

    partial void OnSaidChanged(string value) => OnPropertyChanged(nameof(HasSaid));

    /// <summary>Raised to save a produced file. The window answers with a picker.</summary>
    public event Func<string, string, string, IReadOnlyList<string>, byte[], Task>? SaveFile;

    /// <summary>Raised to choose a file to import. Returns its path, or null.</summary>
    public event Func<Task<string?>>? PickFile;

    [RelayCommand]
    private void UseBooks() => Set = PortSet.Books;

    [RelayCommand]
    private void UseMembers() => Set = PortSet.Members;

    // ------------------------------------------------------------- exports --

    [RelayCommand]
    private Task ExportExcelAsync() => ExportAsync(async port =>
    {
        var sheet = await Sheet(port);

        return ("xlsx", "Excel workbook", (IReadOnlyList<string>)["*.xlsx"], Workbook.Write(sheet));
    });

    [RelayCommand]
    private Task ExportPdfAsync() => ExportAsync(async port =>
    {
        var report = ToReport(await Sheet(port));
        var document = new ReportDocument(Letterheads.Current(), report);

        return ("pdf", "PDF document", (IReadOnlyList<string>)["*.pdf"], document.GeneratePdf());
    });

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync(async port =>
    {
        var text = Spreadsheet.From(ToReport(await Sheet(port)));

        return ("csv", "Comma-separated values", (IReadOnlyList<string>)["*.csv"],
            Encoding.UTF8.GetBytes(text));
    });

    [RelayCommand]
    private Task DownloadTemplateAsync() => Guarded(async () =>
    {
        var sheet = Set == PortSet.Books ? DataPort.BookTemplate() : DataPort.MemberTemplate();

        var name = $"{Title()} import template.xlsx";

        if (SaveFile is not null)
        {
            await SaveFile(name, "xlsx", "Excel workbook", ["*.xlsx"], Workbook.Write(sheet));
        }
    });

    // -------------------------------------------------------------- import --

    [RelayCommand]
    private Task ImportAsync() => Guarded(async () =>
    {
        if (!MayImport || PickFile is null)
        {
            return;
        }

        var path = await PickFile();

        if (path is null)
        {
            return;
        }

        Problems.Clear();

        await using var stream = File.OpenRead(path);

        var sheet = Workbook.Read(stream);

        await using var db = Workspace.Open();

        var port = new DataPort(db);
        var by = Session.User!.UserId;

        var outcome = Set == PortSet.Books
            ? await port.ImportBooksAsync(sheet, by)
            : await port.ImportMembersAsync(sheet, by);

        foreach (var problem in outcome.Problems.Take(50))
        {
            Problems.Add(problem);
        }

        Said = outcome.Summary
            + (outcome.Problems.Count > 50 ? $" (first 50 of {outcome.Problems.Count} problems shown.)" : "");
        SaidIsGood = outcome.Added > 0 && !outcome.AnyProblems;
    });

    // ------------------------------------------------------------ plumbing --

    private async Task<Sheet> Sheet(DataPort port) => Set == PortSet.Books
        ? await port.ExportBooksAsync()
        : await port.ExportMembersAsync();

    private async Task ExportAsync(
        Func<DataPort, Task<(string ext, string type, IReadOnlyList<string> patterns, byte[] bytes)>> make)
    {
        await Guarded(async () =>
        {
            await using var db = Workspace.Open();

            var (ext, type, patterns, bytes) = await make(new DataPort(db));

            var name = $"{Title()} {DateTime.Now:yyyy-MM-dd}.{ext}";

            if (SaveFile is not null)
            {
                await SaveFile(name, ext, type, patterns, bytes);
            }
        });
    }

    private async Task Guarded(Func<Task> work)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Said = "";

        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Faults.Record("importing or exporting", ex);

            Said = Faults.Explain(ex);
            SaidIsGood = false;
        }
        finally
        {
            Busy = false;
        }
    }

    private string Title() => Set == PortSet.Books ? "Books" : "Members";

    private static Report ToReport(Sheet sheet) => new(sheet.Name, sheet.Headers, sheet.Rows);
}
