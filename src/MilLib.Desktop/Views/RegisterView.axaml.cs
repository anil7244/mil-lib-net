using Avalonia.Controls;
using MilLib.Core.Documents;
using QuestPDF.Fluent;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class RegisterView : UserControl
{
    public RegisterView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not RegisterViewModel model)
        {
            return;
        }

        model.Print -= PrintAsync;
        model.Print += PrintAsync;
    }

    /// <summary>
    /// Writing the register out. The view does this because choosing where a
    /// file goes needs a window to ask from, and the document itself needs the
    /// crest — which is a fact about this machine, not about the records.
    /// </summary>
    private async Task PrintAsync(IReadOnlyList<RegisterEntry> entries, string range)
    {
        var document = new AccessionRegisterDocument(
            Letterheads.Current(),
            entries,
            range,
            Session.Preferences.CurrencySymbol);

        var name = $"Accession Register {DateTime.Now:yyyy-MM-dd}.pdf";

        await Documents.SaveAsync(this, "Save the accession register", name,
            path => document.GeneratePdf(path));
    }
}
