using Avalonia.Controls;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Views;

public partial class MembersView : UserControl
{
    public MembersView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    /// <summary>
    /// Opening the edit window is the view's job, not the view model's — a view
    /// model that constructs windows cannot be reasoned about without a screen
    /// in front of it. The model raises the request; this answers it, and waits,
    /// so the list refreshes only once the window has actually gone.
    /// </summary>
    private void Watch()
    {
        if (DataContext is not MembersViewModel model)
        {
            return;
        }

        model.Edit -= OpenAsync;
        model.Edit += OpenAsync;

        model.PrintPasses -= PrintAsync;
        model.PrintPasses += PrintAsync;
    }

    private async Task OpenAsync(long? memberId)
    {
        var window = new MemberEditWindow(memberId);

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner);
            return;
        }

        window.Show();
    }

    /// <summary>
    /// The passes, as a PDF to look at.
    ///
    /// Viewed rather than saved, because a pass is printed and then thrown
    /// away — nobody keeps the file. The photograph paths are resolved here:
    /// the database records where the web application put them, and only this
    /// side knows where that is on this machine.
    /// </summary>
    private async Task PrintAsync(IReadOnlyList<PassFor> passes)
    {
        var found = passes
            .Select(p => p with { PhotoPath = Workspace.CoverPath(p.PhotoPath) })
            .ToList();

        var document = new PassDocument(Letterheads.Current(), found);

        var name = passes.Count == 1
            ? $"Pass {passes[0].MembershipNo}.pdf"
            : $"Library passes {DateTime.Now:yyyy-MM-dd}.pdf";

        await Documents.ViewAsync(this, "Printing the passes", name,
            path => document.GeneratePdf(path));
    }
}
