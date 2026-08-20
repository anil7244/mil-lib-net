using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MilLib.Core.Data;

/// <summary>
/// The records, exactly as the library keeps them.
///
/// These are the tables the PHP application built, name for name and column for
/// column. Nothing here is a fresh design: the point of this application is to
/// be a different front door onto the same library, and the moment the shapes
/// drift the two stop being able to read each other's work.
///
/// Only the primary keys are named explicitly. Every other column follows the
/// same rule — PascalCase here, snake_case there — and is mapped in one place
/// in <see cref="MilLibDbContext"/> rather than repeated on 300 properties.
/// </summary>

// ================================================================= people ==

[Table("users")]
public class User
{
    [Key, Column("user_id")] public long UserId { get; set; }

    public string Username { get; set; } = "";

    /// <summary>
    /// bcrypt, cost 12 — the same hash the PHP application writes, so a
    /// password set on either side works on the other at the next sign-in.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    public string FullName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.COUNTER;
    public long? BranchId { get; set; }
    public SecurityClass ClearanceLevel { get; set; } = SecurityClass.UNCLASSIFIED;
    public string? ThemePreference { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public string? RememberToken { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Branch? Branch { get; set; }

    public string Display => FullName.Length > 0 ? FullName : Username;

    /// <summary>Two letters for the corner of the window, from the name.</summary>
    public string Initials
    {
        get
        {
            var parts = Display.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
                _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
            };
        }
    }
}

[Table("branches")]
public class Branch
{
    [Key, Column("branch_id")] public long BranchId { get; set; }

    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Location { get; set; }
    public bool IsDefault { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// ============================================================== catalogue ==

[Table("publishers")]
public class Publisher
{
    [Key, Column("publisher_id")] public long PublisherId { get; set; }

    public string Name { get; set; } = "";
    public string? Place { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

[Table("authors")]
public class Author
{
    [Key, Column("author_id")] public long AuthorId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>A unit library's authors are often ranked. Optional.</summary>
    public string? Rank { get; set; }

    public string? Notes { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Rank) ? Name : $"{Rank} {Name}";
}

/// <summary>A subject heading. Self-nesting, so a subject may sit under another.</summary>
[Table("categories")]
public class Category
{
    [Key, Column("category_id")] public long CategoryId { get; set; }

    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Category? Parent { get; set; }
}

/// <summary>
/// The intellectual work — one row per book, not per copy on the shelf.
///
/// There is deliberately no quantity column. How many there are is a question
/// about the physical objects, and it is answered by counting them.
/// </summary>
[Table("titles")]
public class Title
{
    [Key, Column("title_id")] public long TitleId { get; set; }

    public string? AccessionPrefix { get; set; }
    public string? Isbn { get; set; }

    [Column("title")] public string Name { get; set; } = "";

    public string? Subtitle { get; set; }
    public string? StatementOfResp { get; set; }
    public string? Edition { get; set; }
    public long? PublisherId { get; set; }
    public int? PubYear { get; set; }
    public string? PubPlace { get; set; }
    public string? Pages { get; set; }
    public string Language { get; set; } = "English";
    public string? ClassificationNo { get; set; }
    public ClassificationScheme ClassificationSch { get; set; } = ClassificationScheme.DDC;
    public string? SubjectHeadings { get; set; }
    public string? CallNumber { get; set; }
    public MaterialType MaterialType { get; set; } = MaterialType.BOOK;
    public SecurityClass SecurityClass { get; set; } = SecurityClass.UNCLASSIFIED;
    public bool IsUnitPublication { get; set; }
    public string? AmendmentNo { get; set; }
    public DateOnly? AmendmentDate { get; set; }
    public long? SupersededBy { get; set; }
    public string? Notes { get; set; }
    public string? CoverPath { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Publisher? Publisher { get; set; }
    public List<Copy> Copies { get; set; } = [];
    public List<TitleAuthor> Authors { get; set; } = [];
    public List<TitleCategory> Categories { get; set; } = [];

    public string FullTitle => string.IsNullOrWhiteSpace(Subtitle) ? Name : $"{Name}: {Subtitle}";
}

[Table("title_author")]
public class TitleAuthor
{
    public long TitleId { get; set; }
    public long AuthorId { get; set; }
    public AuthorRole Role { get; set; } = AuthorRole.AUTHOR;
    public int SortOrder { get; set; }

    public Title? Title { get; set; }
    public Author? Author { get; set; }
}

[Table("title_category")]
public class TitleCategory
{
    public long TitleId { get; set; }
    public long CategoryId { get; set; }

    public Title? Title { get; set; }
    public Category? Category { get; set; }
}

/// <summary>
/// One physical book. The accession number is the library's own statutory
/// identifier for it and never changes, not even when the book is withdrawn.
/// </summary>
[Table("copies")]
public class Copy
{
    [Key, Column("copy_id")] public long CopyId { get; set; }

    public long TitleId { get; set; }
    public string AccessionNo { get; set; } = "";

    /// <summary>The number in the unit's own stock ledger, where there is one.</summary>
    public string? BookNo { get; set; }

    /// <summary>The accession number as the ledger wrote it, before any tidying.</summary>
    public string? SourceAccn { get; set; }

    public int? AccessionSeq { get; set; }
    public DateOnly AccessionDate { get; set; }
    public string Barcode { get; set; } = "";
    public long? BranchId { get; set; }
    public string? Location { get; set; }
    public string? LedgerName { get; set; }
    public string? LedgerPageNo { get; set; }
    public string? AccountingUnit { get; set; }
    public int? QtyLedger { get; set; }
    public int? QtyGround { get; set; }
    public CopyStatus Status { get; set; } = CopyStatus.AVAILABLE;

    [Column("condition")] public CopyCondition Condition { get; set; } = CopyCondition.NEW;

    public bool IsCirculating { get; set; } = true;
    public CopySource Source { get; set; } = CopySource.PURCHASE;
    public string? Supplier { get; set; }
    public string? BillNo { get; set; }
    public DateOnly? BillDate { get; set; }
    public decimal? Cost { get; set; }
    public string? Remarks { get; set; }
    public DateOnly? WithdrawnAt { get; set; }
    public long? WithdrawalId { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Title? Title { get; set; }
    public Branch? Branch { get; set; }
}

/// <summary>
/// A note against one copy. Appended, never edited: the history of a book's
/// condition is only worth having if nobody can tidy it afterwards.
/// </summary>
[Table("copy_annotations")]
public class CopyAnnotation
{
    [Key, Column("annotation_id")] public long AnnotationId { get; set; }

    public long CopyId { get; set; }
    public string Note { get; set; } = "";
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? Author { get; set; }
}

// ================================================================ members ==

/// <summary>
/// The lending policy, held as data.
///
/// How many books, for how long, how many renewals, and what a late day costs
/// are all answered from the member's category. None of it is in the code, and
/// none of it should ever be.
/// </summary>
[Table("member_categories")]
public class MemberCategory
{
    [Key, Column("category_id")] public long CategoryId { get; set; }

    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int MaxBooks { get; set; } = 1;
    public int LoanDays { get; set; } = 14;
    public int MaxRenewals { get; set; }
    public decimal FinePerDay { get; set; }
    public int GraceDays { get; set; }
    public bool CanReserve { get; set; } = true;
    public SecurityClass MaxClearance { get; set; } = SecurityClass.UNCLASSIFIED;
    public bool RequiresDeposit { get; set; }
    public decimal? DepositAmount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

[Table("members")]
public class Member
{
    [Key, Column("member_id")] public long MemberId { get; set; }

    public string MembershipNo { get; set; } = "";
    public long CategoryId { get; set; }
    public string FullName { get; set; } = "";
    public string? Rank { get; set; }
    public string? PersonnelNo { get; set; }
    public string? UnitCoy { get; set; }
    public string? Appointment { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? PhotoPath { get; set; }
    public string QrToken { get; set; } = "";

    /// <summary>Set only where members sign in for themselves. Usually null.</summary>
    public string? Password { get; set; }

    public string? RememberToken { get; set; }
    public SecurityClass ClearanceLevel { get; set; } = SecurityClass.UNCLASSIFIED;
    public DateOnly EnrolledOn { get; set; }
    public DateOnly? ValidUpto { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.ACTIVE;
    public DateOnly? PostedOutOn { get; set; }
    public DateOnly? ClearedOn { get; set; }
    public decimal? SecurityDeposit { get; set; }
    public string? Remarks { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public MemberCategory? Category { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Rank) ? FullName : $"{Rank} {FullName}";
}

[Table("member_cards")]
public class MemberCard
{
    [Key, Column("card_id")] public long CardId { get; set; }

    public long MemberId { get; set; }
    public string CardNo { get; set; } = "";
    public string QrToken { get; set; } = "";
    public DateOnly IssuedOn { get; set; }
    public DateOnly? ValidUpto { get; set; }
    public CardStatus Status { get; set; } = CardStatus.ACTIVE;
    public long? IssuedBy { get; set; }
    public string? Remarks { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Member? Member { get; set; }
}

// ============================================================ circulation ==

[Table("loans")]
public class Loan
{
    [Key, Column("loan_id")] public long LoanId { get; set; }

    public long CopyId { get; set; }
    public long MemberId { get; set; }
    public DateTime IssuedOn { get; set; }
    public DateOnly DueOn { get; set; }
    public DateTime? ReturnedOn { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.OPEN;
    public int RenewalCount { get; set; }
    public long? IssuedBy { get; set; }
    public long? ReturnedTo { get; set; }
    public CopyCondition IssueCondition { get; set; } = CopyCondition.GOOD;
    public CopyCondition? ReturnCondition { get; set; }
    public string? CustodyWitness { get; set; }
    public string? CustodySignature { get; set; }
    public string? IssuedToSubunit { get; set; }
    public string? Remarks { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Copy? Copy { get; set; }
    public Member? Member { get; set; }

    /// <summary>
    /// Days past due as of today, or zero. Read off the due date rather than
    /// off the status, because a loan only becomes OVERDUE when something looks
    /// at it — and nothing may have looked at it since yesterday.
    /// </summary>
    public int DaysOverdue(DateOnly today) =>
        Status == LoanStatus.RETURNED ? 0 : Math.Max(0, today.DayNumber - DueOn.DayNumber);
}

[Table("renewals")]
public class Renewal
{
    [Key, Column("renewal_id")] public long RenewalId { get; set; }

    public long LoanId { get; set; }
    public DateTime RenewedOn { get; set; }
    public DateOnly OldDueOn { get; set; }
    public DateOnly NewDueOn { get; set; }
    public long? RenewedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// A hold. Placed against a title rather than a copy, because a member wants
/// the book, not one particular object.
/// </summary>
[Table("reservations")]
public class Reservation
{
    [Key, Column("reservation_id")] public long ReservationId { get; set; }

    public long TitleId { get; set; }
    public long MemberId { get; set; }
    public DateTime ReservedOn { get; set; }
    public int QueuePosition { get; set; } = 1;
    public ReservationStatus Status { get; set; } = ReservationStatus.WAITING;
    public DateTime? ReadyOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public long? FulfilledCopyId { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Title? Title { get; set; }
    public Member? Member { get; set; }
}

[Table("fines")]
public class Fine
{
    [Key, Column("fine_id")] public long FineId { get; set; }

    public long MemberId { get; set; }
    public long? LoanId { get; set; }
    public FineType Type { get; set; } = FineType.OVERDUE;
    public decimal Amount { get; set; }
    public DateOnly CalculatedOn { get; set; }
    public int? DaysOverdue { get; set; }
    public FineStatus Status { get; set; } = FineStatus.PENDING;
    public DateOnly? PaidOn { get; set; }
    public string? ReceiptNo { get; set; }
    public long? WaivedBy { get; set; }
    public string? WaiverReason { get; set; }
    public string? Remarks { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Member? Member { get; set; }
    public Loan? Loan { get; set; }
}

// ================================================================== stock ==

[Table("stock_verifications")]
public class StockVerification
{
    [Key, Column("verification_id")] public long VerificationId { get; set; }

    [Column("title")] public string Name { get; set; } = "";

    public DateOnly StartedOn { get; set; }
    public DateOnly? CompletedOn { get; set; }
    public long? BranchId { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.IN_PROGRESS;
    public string? BoardReference { get; set; }
    public int TotalExpected { get; set; }
    public int TotalFound { get; set; }
    public int TotalMissing { get; set; }
    public long? ConductedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

[Table("stock_verification_scans")]
public class StockVerificationScan
{
    [Key, Column("scan_id")] public long ScanId { get; set; }

    public long VerificationId { get; set; }
    public long? CopyId { get; set; }
    public string BarcodeScanned { get; set; } = "";
    public ScanResult Result { get; set; } = ScanResult.FOUND;
    public DateTime ScannedAt { get; set; }
    public long? ScannedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Copy? Copy { get; set; }
}

/// <summary>
/// A board's decision to take books off the register. The books stay in the
/// data — a withdrawn copy is still a copy, and the register still shows it.
/// </summary>
[Table("withdrawals")]
public class Withdrawal
{
    [Key, Column("withdrawal_id")] public long WithdrawalId { get; set; }

    public string WithdrawalNo { get; set; } = "";
    public DateOnly WithdrawalDate { get; set; }
    public WithdrawalReason Reason { get; set; }
    public string? BoardProceedings { get; set; }
    public string? SanctionAuthority { get; set; }
    public DateOnly? SanctionDate { get; set; }
    public decimal TotalValue { get; set; }
    public string? Remarks { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// ================================================================ housekeeping ==

[Table("settings")]
public class Setting
{
    [Key, Column("setting_id")] public long SettingId { get; set; }

    [Column("key")] public string Key { get; set; } = "";
    [Column("value")] public string? Value { get; set; }
    [Column("type")] public SettingType Type { get; set; } = SettingType.STRING;
    [Column("group")] public string Group { get; set; } = "";

    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public bool IsEditable { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// What was done, by whom, and when. Nothing in this table is edited or
/// deleted from anywhere in the application.
/// </summary>
[Table("audit_log")]
public class AuditLog
{
    [Key, Column("log_id")] public long LogId { get; set; }

    public long? UserId { get; set; }
    public long? MemberId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public long? EntityId { get; set; }
    public SecurityClass? SecurityClass { get; set; }
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? User { get; set; }
}

/// <summary>
/// The next accession number to hand out. One row per scope, and the counter
/// only ever goes up: a number that has been issued is never reissued, even if
/// the copy it belonged to was withdrawn the same afternoon.
/// </summary>
[Table("accession_counters")]
public class AccessionCounter
{
    [Key, Column("id")] public long Id { get; set; }

    public string Scope { get; set; } = "";
    public int NextSeq { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// This installation's licence. Bound to the machine, so a copied folder is a
/// copied folder and not a second licence.
/// </summary>
[Table("license_info")]
public class LicenseInfo
{
    [Key, Column("id")] public long Id { get; set; }

    public string HardwareId { get; set; } = "";
    public string? LicenseKey { get; set; }
    public string? AppName { get; set; }
    public string? AppVersion { get; set; }
    public bool IsActive { get; set; }
    public DateTime? TrialStartedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public string? Notes { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
