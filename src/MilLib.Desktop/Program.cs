using Avalonia;
using System;

namespace MilLib.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Services.Faults.Install();

        // QuestPDF refuses to produce a document until the licence that applies
        // has been declared. Community is the correct one for a company under
        // the revenue threshold; without this line every printed document
        // throws instead of printing.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // If the application cannot even start, the reason is worth keeping.
            Services.Faults.Record("startup", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
