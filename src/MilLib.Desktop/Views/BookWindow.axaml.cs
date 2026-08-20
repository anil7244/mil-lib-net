using Avalonia.Controls;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class BookWindow : Window
{
    /// <summary>For the XAML designer, which can only construct with no arguments.</summary>
    public BookWindow() : this(0)
    {
    }

    public BookWindow(long titleId)
    {
        InitializeComponent();

        var model = new BookViewModel(titleId);

        model.EditRecord += EditAsync;

        DataContext = model;
    }

    /// <summary>
    /// The cataloguing form, over this window. The book screen re-reads itself
    /// when it closes, so a corrected title shows in the heading straight away.
    /// </summary>
    private async Task EditAsync(long titleId)
    {
        var window = new TitleEditWindow(titleId);

        await window.ShowDialog(this);
    }
}
