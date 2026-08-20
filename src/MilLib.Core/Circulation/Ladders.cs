namespace MilLib.Core.Data;

/// <summary>
/// The two orderings the library depends on: how classified a thing is, and
/// what condition a book is in.
///
/// Both live here and nowhere else. "Is this person cleared for this book" and
/// "did this come back worse than it went out" are each one comparison, not a
/// chain of string tests scattered through the counter screen — which is how
/// they end up disagreeing with each other.
/// </summary>
public static class Ladders
{
    // ------------------------------------------------------- classification --

    /// <summary>Rank on the ladder. Higher means more restricted.</summary>
    public static int Level(this SecurityClass value) => value switch
    {
        SecurityClass.UNCLASSIFIED => 0,
        SecurityClass.RESTRICTED => 1,
        SecurityClass.CONFIDENTIAL => 2,
        SecurityClass.SECRET => 3,
        SecurityClass.TOP_SECRET => 4,
        _ => 0,
    };

    /// <summary>
    /// Can a holder of this clearance have an item classified at <paramref name="item"/>?
    ///
    /// The central rule: clearance must meet or exceed the item's
    /// classification. "Block an issue above the member's clearance" is exactly
    /// this returning false.
    /// </summary>
    public static bool Allows(this SecurityClass clearance, SecurityClass item) =>
        clearance.Level() >= item.Level();

    /// <summary>
    /// The lower of two. Used to cap a member's own clearance at the ceiling
    /// their category allows — a member cleared to Secret in a category that
    /// stops at Restricted is, for this library, cleared to Restricted.
    /// </summary>
    public static SecurityClass LowerOf(this SecurityClass value, SecurityClass other) =>
        value.Level() <= other.Level() ? value : other;

    /// <summary>Anything above unclassified — subject to custody and audit rules.</summary>
    public static bool IsClassified(this SecurityClass value) =>
        value != SecurityClass.UNCLASSIFIED;

    /// <summary>
    /// Everything a holder of this clearance may see.
    ///
    /// A list rather than a comparison, because the database stores the word
    /// and not the rank — so "at or below Confidential" has to become "one of
    /// Unclassified, Restricted, Confidential" before it can be asked in SQL.
    /// Every report filters through this and none of them re-derives it.
    /// </summary>
    public static IReadOnlyList<SecurityClass> UpTo(this SecurityClass clearance) =>
        [.. Enum.GetValues<SecurityClass>().Where(c => c.Level() <= clearance.Level())];

    /// <summary>
    /// Conventional marking colours, darkening as the classification rises.
    /// Used on passes, labels and the band on the counter screen.
    /// </summary>
    public static (string Background, string Ink) Band(this SecurityClass value) => value switch
    {
        SecurityClass.RESTRICTED => ("#1565C0", "#FFFFFF"),
        SecurityClass.CONFIDENTIAL => ("#F9A825", "#1A1A1A"),
        SecurityClass.SECRET => ("#E65100", "#FFFFFF"),
        SecurityClass.TOP_SECRET => ("#B71C1C", "#FFFFFF"),
        _ => ("#2E7D32", "#FFFFFF"),
    };

    // ------------------------------------------------------------ condition --

    /// <summary>Quality rank. Higher is better, so a drop is deterioration.</summary>
    public static int Rank(this CopyCondition value) => value switch
    {
        CopyCondition.NEW => 4,
        CopyCondition.GOOD => 3,
        CopyCondition.FAIR => 2,
        CopyCondition.POOR => 1,
        CopyCondition.DAMAGED => 0,
        _ => 0,
    };

    public static bool IsWorseThan(this CopyCondition value, CopyCondition other) =>
        value.Rank() < other.Rank();

    /// <summary>How many steps a copy dropped between issue and return, or zero.</summary>
    public static int DegradedFrom(this CopyCondition returned, CopyCondition issued) =>
        Math.Max(0, issued.Rank() - returned.Rank());

    // -------------------------------------------------------------- members --

    /// <summary>
    /// What this member is actually cleared for: their own clearance, capped at
    /// their category's ceiling. Never their personal clearance on its own.
    /// </summary>
    public static SecurityClass EffectiveClearance(this Member member, MemberCategory category) =>
        member.ClearanceLevel.LowerOf(category.MaxClearance);

    public static bool CanAccess(this Member member, MemberCategory category, SecurityClass item) =>
        member.EffectiveClearance(category).Allows(item);
}
