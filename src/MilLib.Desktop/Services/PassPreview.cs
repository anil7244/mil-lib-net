using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MilLib.Core.Documents;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Services;

/// <summary>
/// The pass, on the screen.
///
/// A pass used to go straight to a PDF in whatever opens PDFs — which meant
/// leaving the application to see what you were about to print. Here it is
/// shown in the application first, exactly as it prints (the picture is the
/// print document rendered, not a second drawing of it that could drift from
/// it), and printing or saving is a button on that window rather than the only
/// thing that happens.
///
/// One member is shown as a single card. A whole intake — a company's worth of
/// passes at once — is a sheet to be printed and cut, not a thing to look at
/// one card at a time, so that goes to the PDF as before.
/// </summary>
public static class PassPreview
{
    public static async Task ShowAsync(Visual anchor, IReadOnlyList<PassFor> passes)
    {
        var top = TopLevel.GetTopLevel(anchor) as Window;

        // The photo paths are recorded the way the web application filed them;
        // only this side knows where that is on this machine.
        var found = passes
            .Select(p => p with { PhotoPath = Workspace.CoverPath(p.PhotoPath) })
            .ToList();

        var unit = Letterheads.Current();

        // A sheet of passes is a printing job, not a viewing one.
        if (found.Count != 1)
        {
            var sheet = new PassDocument(unit, found);

            await Documents.ViewAsync(anchor, "Printing the passes",
                $"Library passes {DateTime.Now:yyyy-MM-dd}.pdf",
                path => sheet.GeneratePdf(path));

            return;
        }

        var member = found[0];

        Bitmap picture;

        try
        {
            var image = new PassDocument(unit, [member], singleCard: true)
                .GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { RasterDpi = 300 })
                .First();

            using var stream = new MemoryStream(image);

            picture = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Faults.Record("drawing the pass", ex);

            await Notice.ShowAsync(top, "The pass could not be drawn", Faults.Explain(ex));

            return;
        }

        Show(top, unit, member, picture);
    }

    private static void Show(Window? owner, Letterhead unit, PassFor member, Bitmap picture)
    {
        var name = string.IsNullOrWhiteSpace(member.Rank)
            ? member.FullName
            : $"{member.Rank} {member.FullName}";

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(26) };

        panel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"{member.MembershipNo} · the card as it prints, 85.6 × 54 mm",
            Foreground = Application.Current?.Resources["InkFaint"] as IBrush,
            FontSize = 12,
        });

        // The card at roughly life size on screen — wide enough to read every
        // field and check the photo, which is the whole reason to look first.
        panel.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = picture,
                Width = 428,
                Height = 270,
                Stretch = Stretch.Uniform,
            },
        });

        var window = new Window
        {
            Title = "The pass",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var print = new Button { Content = "Print…" };
        print.Classes.Add("primary");
        print.Click += async (_, _) =>
        {
            var one = new PassDocument(unit, [member]);

            await Documents.ViewAsync(window, "Printing the pass",
                $"Pass {member.MembershipNo}.pdf", path => one.GeneratePdf(path));
        };

        var save = new Button { Content = "Save as PDF…" };
        save.Click += async (_, _) =>
        {
            var one = new PassDocument(unit, [member]);

            await Documents.SaveAsync(window, "Saving the pass",
                $"Pass {member.MembershipNo}.pdf", path => one.GeneratePdf(path));
        };

        var close = new Button { Content = "Close", IsDefault = true };
        close.Click += (_, _) => window.Close();

        buttons.Children.Add(print);
        buttons.Children.Add(save);
        buttons.Children.Add(close);

        panel.Children.Add(buttons);
        window.Content = panel;

        if (owner is null)
        {
            window.Show();
        }
        else
        {
            _ = window.ShowDialog(owner);
        }
    }
}
