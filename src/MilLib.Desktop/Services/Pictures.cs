using Avalonia.Media.Imaging;

namespace MilLib.Desktop.Services;

/// <summary>
/// Getting a picture off the disk and onto a screen.
///
/// Every picture in this application is optional — a library may have no crest
/// and most books have no cover — so the only interesting question here is what
/// happens when the file is missing, unreadable, or not really a picture at
/// all. The answer is always the same: nothing on screen, and no interruption.
/// A book with a broken cover file is still a book.
/// </summary>
public static class Pictures
{
    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // Read into memory first and let the file go. Held open, the file
            // cannot be replaced — and replacing the crest with a better scan
            // is a thing people do while the application is running.
            using var stream = new MemoryStream(File.ReadAllBytes(path));

            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Faults.Record($"reading the picture at {path}", ex);

            return null;
        }
    }
}
