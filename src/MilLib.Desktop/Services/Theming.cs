using Avalonia;
using Avalonia.Media;
using MilLib.Core.Data;

namespace MilLib.Desktop.Services;

/// <summary>
/// The unit's own colour, applied to the whole application.
///
/// This is what makes one build sellable to any unit. The accent is a setting,
/// not a constant: one row in the settings table recolours every button, every
/// selected menu item, every heading rule and every printed document, and it
/// takes effect the moment it is saved rather than at the next release.
///
/// Everything in the palette that is derived from the accent — the hover
/// shade, the tint behind a selected row, the ring round a focused field — is
/// worked out from it here rather than being three more settings for somebody
/// to get wrong. A unit chooses one colour; the application does the rest.
/// </summary>
public static class Theming
{
    /// <summary>The colour a fresh install starts on, and the fallback for a bad value.</summary>
    public const string DefaultAccent = MilLib.Core.Data.Setup.DefaultAccent;

    public static Color Accent { get; private set; } = Color.Parse(DefaultAccent);

    /// <summary>
    /// Recolour the application from the settings.
    ///
    /// Called at sign-in and again whenever the branding is saved. Avalonia
    /// resolves <c>DynamicResource</c> live, so replacing the colours in the
    /// application's resources repaints every open window — nothing has to be
    /// rebuilt and nothing has to be reopened.
    /// </summary>
    /// <summary>
    /// Raised once the application has been recoloured, so the parts of the
    /// window that read the unit's name and crest rather than a brush can
    /// catch up in the same breath. Without it, changing the colour repaints
    /// the buttons and leaves the old unit's name over them.
    /// </summary>
    public static event Action? Changed;

    public static void Apply(Preferences preferences) => Apply(preferences.AccentColour);

    public static void Apply(string colour)
    {
        var accent = Parse(colour) ?? Color.Parse(DefaultAccent);

        Accent = accent;

        if (Application.Current is not { } app)
        {
            return;
        }

        // The whole family, derived. A unit picks one colour and these follow,
        // because a unit that had to choose a hover shade would choose one that
        // did not go with anything.
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentHoverColor"] = Shade(accent, -0.14);
        app.Resources["AccentBrightColor"] = Shade(accent, 0.14);

        // The soft tint sits behind selected rows and focused fields, so it has
        // to work on both a cream page and a near-black one. Mixed against the
        // page rather than given a fixed lightness, or it disappears on one
        // theme and glares on the other.
        var dark = IsDark(app);

        app.Resources["AccentSoftColor"] = Mix(accent, dark ? Color.Parse("#101215") : Colors.White,
            dark ? 0.82 : 0.90);

        app.Resources["BloomWarmColor"] = With(accent, dark ? (byte)0x3D : (byte)0x1F);
        app.Resources["FocusRingColor"] = With(accent, 0x59);

        // The accent used as writing on the near-black bar at the top, which is
        // black whichever theme the application is on. A red accent reads there
        // as it is; a navy or a forest green does not, and the person's rank
        // under their name turns into a smudge. So it is lifted until it can
        // actually be read, rather than trusting that whoever bought this
        // picked a bright colour.
        app.Resources["AccentOnDarkColor"] = ReadableOn(accent, Color.FromRgb(0x0D, 0x0D, 0x0D));

        Changed?.Invoke();
    }

    /// <summary>
    /// The same colour, lightened until it can be read on the given ground.
    ///
    /// Contrast is measured, not estimated, and the colour is walked towards
    /// white a step at a time until it clears the threshold for ordinary text.
    /// A unit whose colour is a deep navy still gets navy — just a navy that is
    /// legible where it is being used as writing rather than as paint.
    /// </summary>
    private static Color ReadableOn(Color colour, Color ground)
    {
        const double wanted = 4.5;

        for (var lift = 0.0; lift < 1.0; lift += 0.05)
        {
            var tried = Mix(colour, Colors.White, lift);

            if (Contrast(tried, ground) >= wanted)
            {
                return tried;
            }
        }

        return Colors.White;
    }

    /// <summary>The WCAG contrast ratio between two colours, 1 to 21.</summary>
    private static double Contrast(Color a, Color b)
    {
        var high = Math.Max(Luminance(a), Luminance(b));
        var low = Math.Min(Luminance(a), Luminance(b));

        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;

            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R))
            + (0.7152 * Channel(colour.G))
            + (0.0722 * Channel(colour.B));
    }

    /// <summary>
    /// Whether the application is currently on its dark theme. The soft tint
    /// has to be mixed towards whichever page it will sit on.
    /// </summary>
    private static bool IsDark(Application app) =>
        app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    /// <summary>
    /// A colour typed into a settings box, or nothing.
    ///
    /// Anything unparseable is nothing rather than a guess: a wrong colour
    /// applied across the whole application is worse than the default, and the
    /// Settings screen already refuses to store one that is not six hex digits.
    /// </summary>
    public static Color? Parse(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour))
        {
            return null;
        }

        try
        {
            return Color.Parse(colour.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Darker for a negative amount, lighter for a positive one.</summary>
    private static Color Shade(Color colour, double by) => by < 0
        ? Mix(colour, Colors.Black, -by)
        : Mix(colour, Colors.White, by);

    private static Color Mix(Color a, Color b, double towardsB)
    {
        towardsB = Math.Clamp(towardsB, 0, 1);

        byte Blend(byte x, byte y) => (byte)Math.Round(x + ((y - x) * towardsB));

        return Color.FromRgb(Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }

    private static Color With(Color colour, byte alpha) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    /// <summary>
    /// Whether white or black reads better on this colour.
    ///
    /// A unit is free to choose a pale accent, and a white label on pale yellow
    /// is unreadable. Worked out by relative luminance rather than guessed, so
    /// the button text is right whatever they pick.
    /// </summary>
    public static Color ContrastOn(Color colour)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;

            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Channel(colour.R))
            + (0.7152 * Channel(colour.G))
            + (0.0722 * Channel(colour.B));

        return luminance > 0.45 ? Color.Parse("#16191D") : Colors.White;
    }
}
