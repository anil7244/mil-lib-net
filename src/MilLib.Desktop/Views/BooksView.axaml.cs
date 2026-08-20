using Avalonia.Controls;
using Avalonia.Input;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class BooksView : UserControl
{
    public BooksView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not BooksViewModel model)
        {
            return;
        }

        model.Open -= OpenAsync;
        model.Open += OpenAsync;

        model.Catalogue -= CatalogueAsync;
        model.Catalogue += CatalogueAsync;
    }

    /// <summary>
    /// A double-click opens the book, which is what a row in a list of things
    /// is expected to do. There is a button as well, because a double-click is
    /// not discoverable and this is a screen used by people who were not
    /// trained on it.
    /// </summary>
    private void OnRowActivated(object? sender, TappedEventArgs e)
    {
        if (DataContext is BooksViewModel { HasSelection: true } model)
        {
            model.OpenSelectedCommand.Execute(null);
        }
    }

    /// <summary>
    /// A tap on a book's cover opens it at full size — handled so it does not
    /// also open the book itself, which a double-click on the row does.
    /// </summary>
    private void OnCoverTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BookRow row } && row.HasCover)
        {
            ImagePeek.Show(this, row.Cover, row.Title);

            e.Handled = true;
        }
    }

    private async Task OpenAsync(long titleId)
    {
        var window = new BookWindow(titleId);

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner);
            return;
        }

        window.Show();
    }

    /// <summary>
    /// The cataloguing form. Returns the id that was saved, or null if the
    /// person backed out — which is how the list knows whether to go and show
    /// the new work.
    /// </summary>
    private async Task<long?> CatalogueAsync(long? titleId)
    {
        var window = new TitleEditWindow(titleId);

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        return window.SavedTitleId == 0 ? null : window.SavedTitleId;
    }
}
