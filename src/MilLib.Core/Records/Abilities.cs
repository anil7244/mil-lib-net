namespace MilLib.Core.Data;

/// <summary>
/// The catalogue of things a member of staff can be permitted to do.
///
/// Abilities are the unit of permission — roles are granted sets of them —
/// and the naming is area.action so related permissions sort together. The
/// strings are the same strings the PHP application gates its routes on, kept
/// identical on purpose: one library, one answer to who may do what, no matter
/// which door somebody came in by.
/// </summary>
public enum Ability
{
    CatalogueView,
    CatalogueManage,
    MembersView,
    MembersManage,
    CirculationOperate,

    /// <summary>
    /// Waiving a block that can be waived — a suspended member, a
    /// reference-only book, somebody already at their limit. A supervisor's
    /// decision, deliberately not a counter's.
    /// </summary>
    CirculationOverride,

    ReservationsManage,
    FinesManage,
    StockVerify,
    WithdrawalsManage,
    ReportsView,
    ReportsAdvanced,
    AuditView,
    SettingsManage,
    UsersManage,
}

/// <summary>
/// Who may do what.
///
/// This matrix is a copy of the one in the PHP application and has to stay a
/// copy. Two front doors onto one library that disagree about permission is
/// worse than either of them being wrong on its own, because whichever answer
/// somebody dislikes they can go and get the other one.
/// </summary>
public static class Abilities
{
    public static bool Can(UserRole role, Ability ability) =>
        // The top role passes everything without consulting the matrix, which
        // is also how it is short-circuited on the other side.
        role == UserRole.SUPERADMIN || GrantedTo(role).Contains(ability);

    public static IReadOnlySet<Ability> GrantedTo(UserRole role) => role switch
    {
        UserRole.SUPERADMIN => Everything,

        UserRole.LIBRARIAN => new HashSet<Ability>
        {
            Ability.CatalogueView,
            Ability.CatalogueManage,
            Ability.MembersView,
            Ability.MembersManage,
            Ability.CirculationOperate,
            Ability.CirculationOverride,
            Ability.ReservationsManage,
            Ability.FinesManage,
            Ability.StockVerify,
            Ability.WithdrawalsManage,
            Ability.ReportsView,
            Ability.ReportsAdvanced,
            Ability.AuditView,
        },

        UserRole.ASST_LIBRARIAN => new HashSet<Ability>
        {
            Ability.CatalogueView,
            Ability.CatalogueManage,
            Ability.MembersView,
            Ability.MembersManage,
            Ability.CirculationOperate,
            Ability.ReservationsManage,
            Ability.FinesManage,
            Ability.ReportsView,
        },

        UserRole.COUNTER => new HashSet<Ability>
        {
            Ability.CatalogueView,
            Ability.MembersView,
            Ability.CirculationOperate,
            Ability.ReservationsManage,
        },

        UserRole.AUDIT => new HashSet<Ability>
        {
            Ability.CatalogueView,
            Ability.MembersView,
            Ability.ReportsView,
            Ability.ReportsAdvanced,
            Ability.AuditView,
        },

        UserRole.READ_ONLY => new HashSet<Ability>
        {
            Ability.CatalogueView,
            Ability.MembersView,
            Ability.ReportsView,
        },

        _ => new HashSet<Ability>(),
    };

    private static readonly HashSet<Ability> Everything =
        [.. Enum.GetValues<Ability>()];

    public static string Label(Ability ability) => ability switch
    {
        Ability.CatalogueView => "View catalogue",
        Ability.CatalogueManage => "Manage catalogue & accession",
        Ability.MembersView => "View members",
        Ability.MembersManage => "Manage members & passes",
        Ability.CirculationOperate => "Issue / return / renew",
        Ability.CirculationOverride => "Override a blocked issue",
        Ability.ReservationsManage => "Manage reservations",
        Ability.FinesManage => "Manage fines",
        Ability.StockVerify => "Run stock verification",
        Ability.WithdrawalsManage => "Manage withdrawals / condemnation",
        Ability.ReportsView => "View reports",
        Ability.ReportsAdvanced => "View advanced reports",
        Ability.AuditView => "View audit log",
        Ability.SettingsManage => "Manage settings & feature flags",
        Ability.UsersManage => "Manage staff accounts",
        _ => ability.ToString(),
    };

    public static string Label(UserRole role) => role switch
    {
        UserRole.SUPERADMIN => "Super Administrator",
        UserRole.LIBRARIAN => "Librarian",
        UserRole.ASST_LIBRARIAN => "Assistant Librarian",
        UserRole.COUNTER => "Counter Staff",
        UserRole.AUDIT => "Audit",
        UserRole.READ_ONLY => "Read Only",
        _ => role.ToString(),
    };
}
