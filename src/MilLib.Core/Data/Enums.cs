namespace MilLib.Core.Data;

/// <summary>
/// The fixed vocabularies.
///
/// Every one of these is stored as the word itself, not as a number — which is
/// how the PHP application wrote them and is the reason a person can open the
/// data file with any tool and read what it says. The member names are spelt
/// exactly as the database spells them, underscores and all, so the conversion
/// in either direction is the name and nothing else. Renaming one here without
/// renaming it there silently stops matching.
/// </summary>
public enum SecurityClass
{
    UNCLASSIFIED,
    RESTRICTED,
    CONFIDENTIAL,
    SECRET,
    TOP_SECRET,
}

/// <summary>What a signed-in person is allowed to do. See <c>Abilities</c>.</summary>
public enum UserRole
{
    SUPERADMIN,
    LIBRARIAN,
    ASST_LIBRARIAN,
    COUNTER,
    AUDIT,
    READ_ONLY,
}

/// <summary>
/// Where one physical book is. Not where a title is — a title has no status,
/// because a title is not an object and cannot be on a shelf or in a bag.
/// </summary>
public enum CopyStatus
{
    AVAILABLE,
    ISSUED,
    RESERVED,
    IN_TRANSIT,
    BINDING,
    MISSING,
    LOST,
    WITHDRAWN,
    REFERENCE_ONLY,
}

public enum CopyCondition
{
    NEW,
    GOOD,
    FAIR,
    POOR,
    DAMAGED,
}

public enum CopySource
{
    PURCHASE,
    GIFT,
    UNIT_ISSUE,
    TRANSFER,
    REPLACEMENT,
}

public enum MaterialType
{
    BOOK,
    PAMPHLET,
    PRECIS,
    MANUAL,
    MAP,
    PERIODICAL,
    CD,
    DVD,
    OTHER,
}

public enum ClassificationScheme
{
    DDC,
    LOCAL,
}

public enum AuthorRole
{
    AUTHOR,
    EDITOR,
    TRANSLATOR,
    COMPILER,
    ILLUSTRATOR,
}

public enum MemberStatus
{
    ACTIVE,
    SUSPENDED,
    EXPIRED,
    POSTED_OUT,
    CLOSED,
}

public enum CardStatus
{
    ACTIVE,
    LOST,
    DAMAGED,
    SURRENDERED,
    EXPIRED,
}

public enum LoanStatus
{
    OPEN,
    RETURNED,
    OVERDUE,
    LOST,
    CLAIMED_RETURNED,
}

public enum ReservationStatus
{
    WAITING,
    READY,
    COLLECTED,
    EXPIRED,
    CANCELLED,
}

public enum FineType
{
    OVERDUE,
    DAMAGE,
    LOSS,
    OTHER,
}

public enum FineStatus
{
    PENDING,
    PAID,
    WAIVED,
    WRITTEN_OFF,
}

public enum VerificationStatus
{
    IN_PROGRESS,
    COMPLETED,
    ABANDONED,
}

public enum ScanResult
{
    FOUND,
    NOT_IN_REGISTER,
    DUPLICATE_SCAN,
}

public enum WithdrawalReason
{
    LOST,
    DAMAGED,
    OBSOLETE,
    SUPERSEDED,
    TRANSFERRED,
}

public enum SettingType
{
    BOOL,
    INT,
    STRING,
    JSON,
}

/// <summary>
/// Words for the screen.
///
/// The database speaks in capitals and underscores because that is a good way
/// for a database to speak. Nobody at a counter should have to read
/// TOP_SECRET or CLAIMED_RETURNED, so every enum reaches the screen through
/// here instead of through ToString().
/// </summary>
public static class Words
{
    public static string Of(SecurityClass value) => value switch
    {
        SecurityClass.TOP_SECRET => "Top Secret",
        _ => Sentence(value.ToString()),
    };

    /// <summary>
    /// A role, said the one way this application says it.
    ///
    /// Handed straight to <see cref="Abilities.Label(UserRole)"/> rather than
    /// spelt again here. The two disagreed — a dropdown offered "Superadmin"
    /// while the list beside it said "Super Administrator" — and one screen
    /// using two names for one role reads as two different things.
    /// </summary>
    public static string Of(UserRole value) => Abilities.Label(value);

    public static string Of(CopyStatus value) => value switch
    {
        CopyStatus.IN_TRANSIT => "In transit",
        CopyStatus.REFERENCE_ONLY => "Reference only",
        _ => Sentence(value.ToString()),
    };

    public static string Of(LoanStatus value) => value switch
    {
        LoanStatus.CLAIMED_RETURNED => "Claimed returned",
        _ => Sentence(value.ToString()),
    };

    public static string Of(MemberStatus value) => value switch
    {
        MemberStatus.POSTED_OUT => "Posted out",
        _ => Sentence(value.ToString()),
    };

    public static string Of(FineStatus value) => value switch
    {
        FineStatus.WRITTEN_OFF => "Written off",
        _ => Sentence(value.ToString()),
    };

    public static string Of(VerificationStatus value) => value switch
    {
        VerificationStatus.IN_PROGRESS => "In progress",
        _ => Sentence(value.ToString()),
    };

    public static string Of(ScanResult value) => value switch
    {
        ScanResult.NOT_IN_REGISTER => "Not in register",
        ScanResult.DUPLICATE_SCAN => "Scanned twice",
        _ => Sentence(value.ToString()),
    };

    /// <summary>
    /// Named rather than derived, because the general rule lower-cases an
    /// acronym: it made "Ddc" of the scheme every book in the library is
    /// classified under.
    /// </summary>
    public static string Of(ClassificationScheme value) => value switch
    {
        ClassificationScheme.DDC => "Dewey (DDC)",
        ClassificationScheme.LOCAL => "Local scheme",
        _ => Sentence(value.ToString()),
    };

    public static string Of(MaterialType value) => value switch
    {
        MaterialType.CD => "CD",
        MaterialType.DVD => "DVD",
        MaterialType.PRECIS => "Précis",
        _ => Sentence(value.ToString()),
    };

    /// <summary>
    /// How a holdings report is grouped. Named rather than derived, because
    /// these are PascalCase rather than the database's UPPER_SNAKE, and the
    /// general rule below turns "MaterialType" into "Materialtype".
    /// </summary>
    public static string Of(HoldingsBy value) => value switch
    {
        HoldingsBy.MaterialType => "Kind of material",
        HoldingsBy.Classification => "Classification",
        HoldingsBy.Subject => "Subject",
        _ => value.ToString(),
    };

    public static string Of(Enum value) => Sentence(value.ToString());

    /// <summary>
    /// The same, chosen at run time rather than at compile time.
    ///
    /// A dropdown holds its values as objects, so the compiler cannot pick the
    /// right overload above and falls back to the general one — which is how a
    /// list offering "Top Secret" ends up offering "Top secret". This picks by
    /// the actual type instead.
    /// </summary>
    public static string Any(object? value) => value switch
    {
        SecurityClass v => Of(v),
        UserRole v => Of(v),
        CopyStatus v => Of(v),
        LoanStatus v => Of(v),
        MemberStatus v => Of(v),
        FineStatus v => Of(v),
        VerificationStatus v => Of(v),
        ScanResult v => Of(v),
        MaterialType v => Of(v),
        ClassificationScheme v => Of(v),
        HoldingsBy v => Of(v),
        Enum v => Of(v),
        _ => value?.ToString() ?? "",
    };

    /// <summary>SOMETHING_LIKE_THIS becomes Something like this.</summary>
    private static string Sentence(string raw)
    {
        var words = raw.Replace('_', ' ').ToLowerInvariant();

        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
