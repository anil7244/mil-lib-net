using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.Models;

/// <summary>
/// One screen the menu can reach.
///
/// An item knows what it needs — an ability, and sometimes a feature the
/// install may not have turned on — and the menu is built from the items that
/// pass rather than by hiding controls afterwards. A screen somebody cannot
/// reach should not be on their menu at all: a greyed-out row is an invitation
/// to ask why, every day, of somebody who cannot change the answer.
/// </summary>
public record NavItem(string Section, string Label, string IconKey, Ability? Needs = null, Feature? Requires = null)
{
    public bool AvailableNow =>
        (Needs is null || Session.Can(Needs.Value))
        && (Requires is null || Session.Has(Requires.Value));

    public Geometry? Icon => Glyph(IconKey);

    public static Geometry? Glyph(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var found) == true
            ? found as Geometry
            : null;
}

/// <summary>
/// One button on the menu bar, and whatever sits under it.
///
/// Three shapes, which is the whole of the idea:
///
///   nothing under it   — a plain button, for the two or three screens used
///                        constantly enough to deserve the top row
///   one thing under it — still a plain button, because a menu holding a single
///                        item is a click somebody makes for no reason
///   several            — a button that opens a short list
///
/// A counter clerk should see four or five things across the top, not eighteen.
/// Everything is still one click away; what changes is how much has to be read
/// before finding it.
///
/// It is observable rather than a plain record for one reason: the button has
/// to light up when its screen is the one showing, and that changes as somebody
/// moves about.
/// </summary>
public partial class NavNode : ObservableObject
{
    /// <summary>Whether the screen showing is this button's, or one on its list.</summary>
    [ObservableProperty] private bool _here;

    public NavNode(string label, string iconKey, IReadOnlyList<NavItem> items, bool trailing = false)
    {
        Label = label;
        IconKey = iconKey;
        Items = items;
        Trailing = trailing;
    }

    public string Label { get; }

    public string IconKey { get; }

    public IReadOnlyList<NavItem> Items { get; private set; }

    public bool Trailing { get; }

    /// <summary>A node that is one screen, with no list under it.</summary>
    public static NavNode One(NavItem item, bool trailing = false) =>
        new(item.Label, item.IconKey, [item], trailing);

    /// <summary>Keep only what this person may actually reach.</summary>
    public NavNode Narrowed()
    {
        Items = [.. Items.Where(i => i.AvailableNow)];

        return this;
    }

    public bool AnythingHere => Items.Count > 0;

    /// <summary>Whether it opens a list, or simply goes somewhere.</summary>
    public bool IsMenu => Items.Count > 1;

    public bool IsPlain => Items.Count == 1;

    /// <summary>Where a plain button goes. Meaningless on a menu.</summary>
    public string Section => Items.Count > 0 ? Items[0].Section : "";

    /// <summary>
    /// What the button says. A node holding one screen takes that screen's
    /// name rather than the group's, so "Administration" does not appear over a
    /// menu of one for somebody who can only see reports.
    /// </summary>
    public string Shown => Items.Count == 1 ? Items[0].Label : Label;

    public string ShownIconKey => Items.Count == 1 ? Items[0].IconKey : IconKey;

    public Geometry? Icon => NavItem.Glyph(ShownIconKey);

    public bool Holds(string section) => Items.Any(i => i.Section == section);
}
