using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Views;

public partial class ReportsView : UserControl
{
    /// <summary>
    /// How many rows are drawn on screen.
    ///
    /// The PDF and the spreadsheet always carry the whole report; this is only
    /// what is put in front of somebody. Nobody reads the four hundredth row of
    /// anything on a screen, and building ten thousand of them makes the
    /// application appear to hang while it does it.
    /// </summary>
    private const int MostRowsShown = 400;

    public ReportsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not ReportsViewModel model)
        {
            return;
        }

        model.Save -= SaveAsync;
        model.Save += SaveAsync;

        model.PropertyChanged -= OnModelChanged;
        model.PropertyChanged += OnModelChanged;

        Draw(model);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsViewModel.Report) && DataContext is ReportsViewModel model)
        {
            Draw(model);
        }
    }

    /// <summary>
    /// Build the table.
    ///
    /// A report's columns are not known until it has run — between two and
    /// seven of them, different per report — so the table cannot be written in
    /// the markup. The proportions match the printed version so the screen and
    /// the paper are recognisably the same document.
    /// </summary>
    private void Draw(ReportsViewModel model)
    {
        var headings = this.FindControl<Grid>("HeadingRow");
        var body = this.FindControl<StackPanel>("Body");

        if (headings is null || body is null)
        {
            return;
        }

        headings.Children.Clear();
        headings.ColumnDefinitions.Clear();
        body.Children.Clear();

        if (model.Headings.Count == 0)
        {
            return;
        }

        foreach (var heading in model.Headings)
        {
            // 1.35, not 1. A column of figures needs little room for its
            // figures and all of it for its heading — at 1 the widest of them
            // read "DAYS LA".
            headings.ColumnDefinitions.Add(
                new ColumnDefinition(heading.RightAligned ? 1.35 : 2.2, GridUnitType.Star));
        }

        for (var i = 0; i < model.Headings.Count; i++)
        {
            var text = new TextBlock { Text = model.Headings[i].Name.ToUpperInvariant() };

            text.Classes.Add("label");

            if (model.Headings[i].RightAligned)
            {
                text.HorizontalAlignment = HorizontalAlignment.Right;
            }

            Grid.SetColumn(text, i);
            headings.Children.Add(text);
        }

        var shown = Math.Min(model.Lines.Count, MostRowsShown);

        for (var r = 0; r < shown; r++)
        {
            body.Children.Add(Row(model, model.Lines[r], r == model.Lines.Count - 1));
        }

        if (model.Lines.Count > shown)
        {
            var more = new TextBlock
            {
                Text = $"…and {model.Lines.Count - shown:N0} more. "
                     + "The PDF and the spreadsheet carry all of them.",
                Margin = new Thickness(20, 14),
                TextWrapping = TextWrapping.Wrap,
            };

            more.Classes.Add("faint");

            body.Children.Add(more);
        }
    }

    private Grid Row(ReportsViewModel model, ReportLine line, bool last)
    {
        var row = new Grid { Margin = new Thickness(20, 8) };

        foreach (var heading in model.Headings)
        {
            row.ColumnDefinitions.Add(
                new ColumnDefinition(heading.RightAligned ? 1.35 : 2.2, GridUnitType.Star));
        }

        // A total row is the last one and says "Total" in its first cell. It is
        // the only bold thing in the table, which is what makes it findable.
        var isTotal = last && line.Cells.Count > 0
            && line.Cells[0].Equals("Total", StringComparison.Ordinal);

        for (var i = 0; i < model.Headings.Count && i < line.Cells.Count; i++)
        {
            var text = new TextBlock
            {
                Text = line.Cells[i],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
            };

            if (model.Headings[i].RightAligned)
            {
                text.HorizontalAlignment = HorizontalAlignment.Right;
                text.Classes.Add("mono");
            }

            if (isTotal)
            {
                text.FontWeight = FontWeight.SemiBold;
            }

            Grid.SetColumn(text, i);
            row.Children.Add(text);
        }

        return row;
    }

    private async Task SaveAsync(Report report, bool asPdf)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");

        if (asPdf)
        {
            var document = new ReportDocument(Letterheads.Current(), report);

            await Documents.SaveAsync(this, "Save the report", $"{report.Title} {stamp}.pdf",
                path => document.GeneratePdf(path));

            return;
        }

        await Documents.SaveAsync(this, "Save the report as a spreadsheet",
            $"{report.Title} {stamp}.csv",
            path => File.WriteAllText(path, Spreadsheet.From(report), System.Text.Encoding.UTF8));
    }
}
