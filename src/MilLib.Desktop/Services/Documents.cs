using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace MilLib.Desktop.Services;

/// <summary>
/// Asking where a document should go, writing it there, and saying what
/// happened.
///
/// Every printed document goes through here so they all behave alike, and so
/// that a failure to write one is reported rather than fatal: these are called
/// from click handlers, where an escaping exception ends the process.
/// </summary>
public static class Documents
{
    /// <summary>
    /// Writes the document somewhere temporary and opens it in whatever reads
    /// PDFs on this machine.
    ///
    /// This is what somebody wants most of the time — to look at the thing
    /// before deciding whether it is worth keeping. Saving is the deliberate
    /// act; viewing should not require choosing a folder first.
    /// </summary>
    public static async Task ViewAsync(Visual anchor, string title, string suggestedFileName, Action<string> write)
    {
        var top = TopLevel.GetTopLevel(anchor) as Window;

        try
        {
            // A folder of our own inside the system temporary directory, so
            // the file has a readable name rather than a random one — a person
            // who saves it from the viewer gets "Invoice GEN-00007.pdf".
            var folder = Path.Combine(Path.GetTempPath(), "Library Documents");

            Directory.CreateDirectory(folder);

            var path = Free(folder, Clean(suggestedFileName));

            write(path);

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Faults.Record(title, ex);

            await Notice.ShowAsync(top, "The document could not be opened", Faults.Explain(ex));
        }
    }

    /// <summary>
    /// Save a file of a given kind — a spreadsheet, a PDF — choosing the folder
    /// first. The same as the PDF save below, but the picker offers the right
    /// extension and filter so the name comes out ".xlsx", not ".pdf".
    /// </summary>
    public static async Task SaveAsync(
        Visual anchor, string title, string suggestedFileName,
        string extension, string typeName, IReadOnlyList<string> patterns,
        Action<string> write)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top)
        {
            return;
        }

        string? path = null;

        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = Clean(suggestedFileName),
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [.. patterns] }],
            });

            path = file?.TryGetLocalPath();

            if (path is null)
            {
                return;
            }

            write(path);
        }
        catch (Exception ex)
        {
            Faults.Record(title, ex);

            await Notice.ShowAsync(top as Window, "The file could not be saved", Faults.Explain(ex));

            return;
        }

        await Notice.ShowAsync(top as Window, "Saved", path, path);
    }

    /// <summary>
    /// Choose a file to read — an import. Returns its path, or null if the
    /// person backed out of the picker.
    /// </summary>
    public static async Task<string?> OpenAsync(
        Visual anchor, string title, string typeName, IReadOnlyList<string> patterns)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [.. patterns] }],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public static async Task SaveAsync(
        Visual anchor, string title, string suggestedFileName, Action<string> write)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top)
        {
            return;
        }

        string? path = null;

        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = Clean(suggestedFileName),
                DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }],
            });

            path = file?.TryGetLocalPath();

            if (path is null)
            {
                return;
            }

            write(path);
        }
        catch (Exception ex)
        {
            Faults.Record(title, ex);

            await Notice.ShowAsync(top as Window, "The document could not be saved", Faults.Explain(ex));

            return;
        }

        await Notice.ShowAsync(top as Window, "Saved", path, path);
    }

    /// <summary>
    /// A name in that folder that can actually be written to.
    ///
    /// The obvious one, unless the viewer still has it open — which is exactly
    /// what happens when somebody prints a pass, looks at it, spots a wrong
    /// rank, corrects it and prints again. Windows refuses to replace a file
    /// another process is holding, and the second print failed every time
    /// until the viewer was closed. Numbered copies rather than a timestamp,
    /// so the name stays the readable one it was chosen to be.
    /// </summary>
    private static string Free(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var path = Path.Combine(folder,
                attempt == 0 ? name : $"{stem} ({attempt + 1}){extension}");

            if (!File.Exists(path))
            {
                return path;
            }

            try
            {
                // Opened for writing and closed again: the only reliable way to
                // ask whether something else is holding it.
                File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None).Dispose();

                return path;
            }
            catch (IOException)
            {
                // Held by the viewer. Try the next name.
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Windows will not accept these in a file name, and a client's name is
    /// as likely as not to contain one.
    /// </summary>
    private static string Clean(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray());

        while (cleaned.Contains("  "))
        {
            cleaned = cleaned.Replace("  ", " ");
        }

        return cleaned.Trim();
    }
}

/// <summary>
/// A small message with an acknowledgement — and, when a file was just
/// written, the offer to open it, since that is what someone does next.
/// </summary>
public static class Notice
{
    public static Task ShowAsync(Window? owner, string heading, string detail, string? openPath = null)
    {
        var text = new StackPanel { Spacing = 10, Margin = new Thickness(24) };

        text.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        text.Children.Add(new SelectableTextBlock
        {
            Text = detail,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var window = new Window
        {
            Title = heading,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        if (openPath is not null)
        {
            var open = new Button { Content = "Open it" };

            open.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Faults.Record("opening " + openPath, ex);
                }

                window.Close();
            };

            buttons.Children.Add(open);
        }

        var close = new Button { Content = "Close", IsDefault = true };
        close.Classes.Add("primary");
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(close);

        text.Children.Add(buttons);
        window.Content = text;

        if (owner is null)
        {
            window.Show();

            return Task.CompletedTask;
        }

        return window.ShowDialog(owner);
    }
}
