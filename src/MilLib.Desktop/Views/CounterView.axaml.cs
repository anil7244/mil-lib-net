using System.ComponentModel;
using Avalonia.Controls;

using Avalonia.Threading;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

/// <summary>
/// The counter screen's one piece of behaviour that cannot live in a view
/// model: keeping the cursor in the scan box.
///
/// A barcode scanner is a keyboard that types very fast and then presses Enter.
/// If the cursor is anywhere else when it fires, the barcode goes into whatever
/// had focus — a remarks box, or nothing at all — and the operator finds out
/// when the book does not come up. So the box takes focus when the screen opens,
/// takes it back the moment the counter returns to waiting, and takes it back
/// after every scan.
/// </summary>
public partial class CounterView : UserControl
{
    public CounterView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => FocusScanBox();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not CounterViewModel model)
        {
            return;
        }

        model.PropertyChanged -= OnModelChanged;
        model.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Back to waiting: whatever panel was up has gone, and the next thing to
        // happen is a scan.
        if (e.PropertyName is nameof(CounterViewModel.Stage) or nameof(CounterViewModel.Busy)
            && DataContext is CounterViewModel { Stage: Stage.Ready, Busy: false })
        {
            FocusScanBox();
        }
    }

    /// <summary>
    /// Posted rather than called: at the moment a panel closes the box may not
    /// be laid out yet, and focusing a control that is not there does nothing
    /// and reports no error.
    /// </summary>
    private void FocusScanBox() =>
        Dispatcher.UIThread.Post(
            () => this.FindControl<TextBox>("ScanBox")?.Focus(),
            DispatcherPriority.Input);
}
