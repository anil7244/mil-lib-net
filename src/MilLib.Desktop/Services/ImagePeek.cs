using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MilLib.Desktop.Services;

/// <summary>
/// A picture, shown large.
///
/// A thumbnail in a table is small on purpose — it says whether there is a
/// photograph and roughly who it is, and no more. When somebody wants to
/// actually look at it, a click opens it at a size worth looking at, and any
/// click or key closes it again. It is deliberately nothing more than a viewer:
/// it does not edit, save or print, so it needs no buttons.
/// </summary>
public static class ImagePeek
{
    public static void Show(Visual anchor, Bitmap? image, string caption)
    {
        if (image is null)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(anchor) as Window;

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };

        panel.Children.Add(new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            MaxWidth = 640,
            MaxHeight = 720,
        });

        panel.Children.Add(new TextBlock
        {
            Text = caption,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Application.Current?.Resources["InkMuted"] as IBrush,
            FontSize = 12,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Click anywhere to close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Application.Current?.Resources["InkFaint"] as IBrush,
            FontSize = 11,
        });

        var window = new Window
        {
            Title = caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Application.Current?.Resources["Surface"] as IBrush,
            Content = panel,
        };

        window.PointerPressed += (_, _) => window.Close();
        window.KeyDown += (_, _) => window.Close();

        if (owner is not null)
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }
}
