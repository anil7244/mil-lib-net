using Avalonia.Controls;
using Avalonia.Input;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A figure was clicked. The ones that lead somewhere walk there; the rest
    /// — running totals that are not screens of their own — do nothing, which
    /// is why the card decides rather than the handler assuming.
    /// </summary>
    private void OnStatTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: StatCard card } && card.CanOpen)
        {
            card.OpenCommand.Execute(null);
        }
    }
}
