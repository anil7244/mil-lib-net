using Avalonia.Controls;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Views;

public partial class LabelsView : UserControl
{
    public LabelsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not LabelsViewModel model)
        {
            return;
        }

        model.PrintSheet -= SheetAsync;
        model.PrintSheet += SheetAsync;

        model.PrintRoll -= RollAsync;
        model.PrintRoll += RollAsync;

        model.SaveZpl -= ZebraAsync;
        model.SaveZpl += ZebraAsync;
    }

    private async Task SheetAsync(
        IReadOnlyList<LabelFor> books, LabelKind kind, LabelCode code, float width, float height)
    {
        var document = new LabelSheetDocument(Letterheads.Current(), books, kind, code, width, height);

        var name = $"Labels {kind.ToString().ToLowerInvariant()} {DateTime.Now:yyyy-MM-dd}.pdf";

        await Documents.SaveAsync(this, "Save the sheet of labels", name,
            path => document.GeneratePdf(path));
    }

    /// <summary>
    /// The universal path: one label to a page at the stock size, as a PDF any
    /// label printer prints through its own driver — no printer language, so no
    /// dependence on one make.
    /// </summary>
    private async Task RollAsync(
        IReadOnlyList<LabelFor> books, LabelKind kind, LabelCode code, float width, float height)
    {
        var document = new LabelSheetDocument(
            Letterheads.Current(), books, kind, code, width, height, roll: true);

        var name = $"Labels {kind.ToString().ToLowerInvariant()} (roll) {DateTime.Now:yyyy-MM-dd}.pdf";

        await Documents.SaveAsync(this, "Save the labels for a label printer", name,
            path => document.GeneratePdf(path));
    }

    /// <summary>
    /// The thermal path. Text, so it can be read before it is sent — and every
    /// dimension in it comes from the stock size on the Settings screen rather
    /// than from a dot count compiled in.
    ///
    /// A calibration label goes at the front of the file. It carries a 10 mm
    /// square and the numbers it was drawn from, so the first label off the
    /// roll says whether the settings match the stock. Getting that wrong is
    /// otherwise found out five hundred labels later.
    /// </summary>
    private async Task ZebraAsync(IReadOnlyList<LabelFor> books, LabelKind kind, Stock stock)
    {
        var name = $"labels-{kind.ToString().ToLowerInvariant()}.zpl";

        await Documents.SaveAsync(this, "Save the Zebra instructions", name,
            path => File.WriteAllText(path,
                Zpl.Calibration(stock) + Zpl.Batch(books, kind, stock)));
    }
}
