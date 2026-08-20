using System.Globalization;
using Avalonia.Data.Converters;
using MilLib.Core.Data;

namespace MilLib.Desktop;

/// <summary>
/// A stored word said the way a person would say it.
///
/// The database speaks in capitals and underscores, which is a good way for a
/// database to speak and a poor way for a dropdown to. Every list that offers
/// one of these values goes through here, so nobody at a counter is asked to
/// pick between GOOD and DAMAGED, or to notice that TOP_SECRET is two words.
/// </summary>
public sealed class Spoken : IValueConverter
{
    public static readonly Spoken Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Words.Any(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// True when the bound value equals the converter parameter.
///
/// Used by the navigation rail so the highlighted item follows whichever screen
/// is actually showing — including when the screen was chosen by something
/// other than a click on the rail itself.
/// </summary>
public sealed class IsEqualTo : IValueConverter
{
    public static readonly IsEqualTo Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    // One-way only: the rail sets the section through its command, not through
    // this binding, so an unchecked button must not try to write anything back.
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// A true/false answer turned into one of two colours from the palette.
///
/// Looked up by name at the moment it is needed rather than written into the
/// markup, so a screen tinted this way follows the theme the way everything
/// else does — including when it is switched while the application is open.
/// </summary>
public sealed class Swatch(string whenTrue, string whenFalse) : IValueConverter
{
    /// <summary>The band above the counter: something went through, or it didn't.</summary>
    public static readonly Swatch GoodOrBad = new("GoodSoft", "BadSoft");

    public static readonly Swatch GoodOrBadInk = new("Good", "Bad");

    /// <summary>A loan past its date, said in red; one that isn't, said quietly.</summary>
    public static readonly Swatch LateOrNot = new("Bad", "InkFaint");

    /// <summary>
    /// The ordinary case said in the ordinary ink, the unusual one in red.
    ///
    /// Used where most rows are fine: a column of green "Active" badges teaches
    /// the eye to skip the column, and then the one suspended member in it goes
    /// unnoticed too. Colour is spent on the exception or it is not spent.
    /// </summary>
    public static readonly Swatch PlainOrBad = new("Ink", "Bad");

    /// <summary>
    /// Red when a thing is true, green when it is not — for a state that is
    /// either fine or needs acting on, where the true case is the bad one.
    /// </summary>
    public static readonly Swatch BadOrGood = new("Bad", "Good");

    public static readonly Swatch BadOrGoodSoft = new("BadSoft", "GoodSoft");

    /// <summary>Good news tinted, everything else left as plain material.</summary>
    public static readonly Swatch GoodOrPlain = new("GoodSoft", "Glass");

    /// <summary>Green ink when the answer is yes, ordinary ink when it is no.</summary>
    public static readonly Swatch GoodOrPlainInk = new("Good", "InkMuted");

    /// <summary>
    /// The other way round from <see cref="PlainOrBad"/>: red when the thing is
    /// true. For columns where the unusual case is the true one — a loss, a
    /// damage — rather than the false one.
    /// </summary>
    public static readonly Swatch BadOrPlain = new("Bad", "Ink");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? whenTrue : whenFalse;

        return Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var brush) == true
            ? brush
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// A classification turned into the colours it is conventionally marked in.
///
/// The two halves come from one place in the core — <c>Band()</c> — so a screen,
/// a pass and a spine label cannot end up disagreeing about what Confidential
/// looks like. Fixed colours rather than palette names on purpose: a marking
/// that changed with the unit's chosen accent would be a marking that means
/// nothing, and the whole point of it is that it means the same everywhere.
/// </summary>
public sealed class Marking(bool foreground) : IValueConverter
{
    public static readonly Marking Ground = new(false);

    public static readonly Marking Ink = new(true);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SecurityClass classification)
        {
            return null;
        }

        var (background, ink) = classification.Band();

        return new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(foreground ? ink : background));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// Which end of the menu bar a button belongs at.
///
/// Everything sits on the left in the order the work is done, except the one
/// node holding what a unit sets once and looks at rarely — that goes to the
/// far end, out of the way of the day.
/// </summary>
public sealed class Side : IValueConverter
{
    public static readonly Side RightOrLeft = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Avalonia.Controls.Dock.Right : Avalonia.Controls.Dock.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// A depth turned into a left margin.
///
/// A tree drawn as a flat list is only a tree because of the indent, and the
/// indent has to come from the data rather than from one template per level.
/// </summary>
public sealed class Indent : IValueConverter
{
    public static readonly Indent Instance = new();

    private const double Step = 22;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Avalonia.Thickness(value is int depth ? Math.Max(0, depth) * Step : 0, 0, 12, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// A true/false answer turned into a font weight — for a list where the top
/// level of a tree should read as headings and the rest as entries under them.
/// </summary>
public sealed class Weight(Avalonia.Media.FontWeight whenTrue, Avalonia.Media.FontWeight whenFalse)
    : IValueConverter
{
    public static readonly Weight MediumOrNormal =
        new(Avalonia.Media.FontWeight.Medium, Avalonia.Media.FontWeight.Normal);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? whenTrue : whenFalse;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// True when two bound values are the same.
///
/// The rail is built from a list, so the thing an item must compare itself
/// against — the section currently showing — is itself a binding rather than a
/// constant written into the markup. A converter parameter cannot be a binding,
/// so both sides arrive here instead.
/// </summary>
public sealed class AreEqual : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2
        && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
}

/// <summary>
/// A palette key turned into its brush, looked up by name at the moment it is
/// drawn.
///
/// The dashboard's rings and donut wedges carry the colour they should be drawn
/// in as a word — Accent, Good, Bad — rather than a fixed brush, so the same
/// list draws right on the light theme and the dark one. Resolved through the
/// application resources, the way the rest of the palette is.
/// </summary>
public sealed class Hue : IValueConverter
{
    public static readonly Hue Brush = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString();

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var brush) == true
            ? brush
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
