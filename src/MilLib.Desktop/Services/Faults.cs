using System.Text;

namespace MilLib.Desktop.Services;

/// <summary>
/// What happens when something goes wrong that nobody anticipated.
///
/// The default is that the process disappears without a word, which during a
/// demonstration is the worst possible behaviour: nothing to show the customer
/// and nothing to diagnose afterwards. So every fault is written down, and the
/// application is given the chance to stay on its feet.
/// </summary>
public static class Faults
{
    /// <summary>
    /// Beside the data file, because that is the folder someone will already
    /// have been told about.
    /// </summary>
    public static string LogPath =>
        Path.Combine(Path.GetDirectoryName(Workspace.DatabasePath) ?? AppContext.BaseDirectory, "errors.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record("unhandled", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("background task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Appends the fault to the log. Never throws: a failure to record a
    /// failure must not become a second failure.
    /// </summary>
    public static void Record(string context, Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            var text = new StringBuilder()
                .AppendLine("---- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  (" + context + ")")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(LogPath, text);
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }

    /// <summary>
    /// One line fit to put in front of a person: what failed, and where the
    /// detail was written.
    /// </summary>
    public static string Explain(Exception ex) =>
        ex.Message + "\n\nThe details were written to " + LogPath;
}
