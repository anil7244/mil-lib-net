using Avalonia.Controls;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class MemberEditWindow : Window
{
    /// <summary>For the XAML designer, which can only construct with no arguments.</summary>
    public MemberEditWindow() : this(null)
    {
    }

    public MemberEditWindow(long? memberId)
    {
        InitializeComponent();

        var model = new MemberEditViewModel(memberId);

        // Saved and abandoned both close it. Which one happened is the list's
        // business — it reloads either way, and reloading after a cancel costs
        // nothing and cannot be wrong.
        model.Saved += Close;
        model.Abandoned += Close;

        DataContext = model;
    }
}
