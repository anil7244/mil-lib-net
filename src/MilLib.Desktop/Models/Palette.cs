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
}
