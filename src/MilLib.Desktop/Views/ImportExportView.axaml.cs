using Avalonia.Controls;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class ImportExportView : UserControl
{
    public ImportExportView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not ImportExportViewModel model)
        {
            return;
        }

        model.SaveFile -= SaveAsync;
        model.SaveFile += SaveAsync;

        model.PickFile -= PickAsync;
        model.PickFile += PickAsync;
    }

    /// <summary>
    /// Put a produced file somewhere. The dialogs belong to the window, which is
    /// why the view model hands the bytes over rather than saving them itself.
    /// </summary>
    private async Task SaveAsync(
        string suggestedName, string extension, string typeName,
        IReadOnlyList<string> patterns, byte[] bytes)
    {
        await Documents.SaveAsync(this, "Save the file", suggestedName, extension, typeName, patterns,
            path => File.WriteAllBytes(path, bytes));
    }

    /// <summary>Choose an Excel file to import.</summary>
    private Task<string?> PickAsync() =>
        Documents.OpenAsync(this, "Choose the Excel file to import", "Excel workbook", ["*.xlsx"]);
}
