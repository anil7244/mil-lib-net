using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace MilLib.Desktop.Models;

/// <summary>
/// A colour a unit can pick without knowing what a hex code is.
///
/// The box for typing one stays, because somebody handed an order saying the
/// regimental colour is #1F4E5F needs to type exactly that. But most people
/// buying this will want their arm's colour and will not have it written down
/// as six characters, and asking them to find out is asking them to leave the
/// application to answer a question about the application.
///
/// Eight, not forty. A long grid of swatches is a decision; eight recognisable
/// ones is a choice.
/// </summary>
public sealed partial class Palette
{
    private readonly Action<string> _pick;

    public Palette(string name, string colour, Action<string> pick)
    {
        Name = name;
        Colour = colour;
        _pick = pick;
        Swatch = new SolidColorBrush(Color.Parse(colour));
    }

    public string Name { get; }

    public string Colour { get; }

    public IBrush Swatch { get; }

    [RelayCommand]
    private void Choose() => _pick(Colour);

    /// <summary>The eight, in the order they read best as a row.</summary>
    public static IReadOnlyList<Palette> All(Action<string> pick) =>
    [
        new("Regimental red", "#c0392b", pick),
        new("Maroon", "#8e2434", pick),
        new("Infantry green", "#2f5d3a", pick),
        new("Navy", "#1f3a5f", pick),
        new("Air force blue", "#2874a6", pick),
        new("Teal", "#1f6f6b", pick),
        new("Sand", "#8a6d3b", pick),
        new("Slate", "#3d4750", pick),
    ];

    /// <summary>
    /// The colours offered for the top bar.
    ///
    /// Deep and dark on purpose — the bar carries the unit's name and the clock
    /// in near-white, and a pale strip would swallow both. These are the arm's
    /// colours taken down to the shade a headline sits on, not the bright accent
    /// version, so a unit gets its own strip and the writing on it still reads.
    /// </summary>
    public static IReadOnlyList<Palette> Bars(Action<string> pick) =>
    [
        new("Black", "#0d0d0d", pick),
        new("Charcoal", "#1c2128", pick),
        new("Gunmetal", "#26303a", pick),
        new("Deep navy", "#132840", pick),
        new("Deep maroon", "#3a1220", pick),
        new("Deep green", "#123326", pick),
        new("Deep teal", "#0f2f2e", pick),
        new("Deep slate", "#232a33", pick),
    ];
}
