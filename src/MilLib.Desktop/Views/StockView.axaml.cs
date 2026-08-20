using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Views;

public partial class StockView : UserControl
{
    public StockView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => FocusScanBox();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not StockViewModel model)
        {
            return;
        }

        model.PrintShortage -= ShortageAsync;
        model.PrintShortage += ShortageAsync;

        model.PropertyChanged -= OnModelChanged;
        model.PropertyChanged += OnModelChanged;
    }

    /// <summary>
    /// The cursor lives in the scan box while a count is open.
    ///
    /// Same reason as the counter: a scanner is a keyboard that types fast and
    /// presses Enter, and somebody walking a shelf is not looking at the screen
    /// to check where the focus went.
    /// </summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StockViewModel.IsOpen) or nameof(StockViewModel.Scanned)
            && DataContext is StockViewModel { IsOpen: true })
        {
            FocusScanBox();
        }
    }

    private void FocusScanBox() =>
        Dispatcher.UIThread.Post(
            () => this.FindControl<TextBox>("ScanBox")?.Focus(),
            DispatcherPriority.Input);

    private async Task ShortageAsync(StockVerification check, string by, Reconciliation found)
    {
        var document = new ShortageDocument(
            Letterheads.Current(),
            check.Name,
            by,
            check.StartedOn,
            check.CompletedOn,
            check.BoardReference,
            check.TotalExpected,
            check.TotalFound,
            [.. found.Missing.Select(m => new Shortage(
                Session.Preferences.Accession(m.Copy.AccessionNo),
                m.Title.Name,
                Words.Of(m.Copy.Status),
                m.Copy.Cost))],
            found.NotInRegister,
            Session.Preferences.CurrencySymbol);

        await Documents.SaveAsync(this, "Save the shortage statement",
            $"Shortage Statement — {check.Name}.pdf",
            path => document.GeneratePdf(path));
    }
}
