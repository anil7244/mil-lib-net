using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MilLib.Core.Data;

/// <summary>
/// The connection to the library's records.
///
/// It builds no schema and owns no migrations. The tables were created by the
/// PHP application and are the same tables whichever database this is pointed
/// at, so this file's whole job is to describe what is already there
/// faithfully — and to be wrong loudly rather than quietly if it ever isn't.
/// </summary>
public class MilLibDbContext(DatabaseSource source) : DbContext
{
    private readonly DatabaseSource _source = source;

    static MilLibDbContext()
    {
        // The tables were written by PHP, which stores a due date as the date
        // it is and nothing more — no zone, no offset, because a book borrowed
        // in a unit is returned to the same counter it left. Npgsql's modern
        // behaviour treats an unmarked timestamp as UTC and converts it, which
        // would slide every due date, issue date and fine by the machine's
        // offset the moment the library moved onto PostgreSQL. Reading the
        // columns as written is the only correct answer here.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Title> Titles => Set<Title>();
    public DbSet<Copy> Copies => Set<Copy>();
    public DbSet<CopyAnnotation> CopyAnnotations => Set<CopyAnnotation>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Publisher> Publishers => Set<Publisher>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<TitleAuthor> TitleAuthors => Set<TitleAuthor>();
    public DbSet<TitleCategory> TitleCategories => Set<TitleCategory>();

    public DbSet<Member> Members => Set<Member>();
    public DbSet<MemberCategory> MemberCategories => Set<MemberCategory>();
    public DbSet<MemberCard> MemberCards => Set<MemberCard>();

    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Renewal> Renewals => Set<Renewal>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Fine> Fines => Set<Fine>();

    public DbSet<StockVerification> StockVerifications => Set<StockVerification>();
    public DbSet<StockVerificationScan> StockVerificationScans => Set<StockVerificationScan>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();

    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();
    public DbSet<AccessionCounter> AccessionCounters => Set<AccessionCounter>();
    public DbSet<LicenseInfo> LicenseInfo => Set<LicenseInfo>();

    /// <summary>
    /// Save, and then let go of everything that was saved.
    ///
    /// Reads on this connection are untracked, so the only things it ever holds
    /// are rows a write path deliberately attached. Leaving them attached
    /// afterwards means the next operation that reads the same row gets a
    /// second instance of it — and EF refuses to have two, with an error about
    /// identity that says nothing about the book somebody is standing there
    /// holding. Issuing a book and taking it back again in one sitting is an
    /// ordinary thing to do, and it must not depend on which order the screens
    /// happened to open their connections.
    /// </summary>
    public async Task SaveAndForgetAsync()
    {
        await SaveChangesAsync();

        ChangeTracker.Clear();
    }

    private static readonly Dictionary<string, ServerVersion> _versions = [];

    /// <summary>
    /// Which MariaDB this is, asked once and then remembered.
    ///
    /// The provider needs to know before it can build a query, and the only
    /// way to know for certain is to ask the server. Two things follow, and
    /// both have bitten:
    ///
    /// Asked every time, that is a round trip on the wire before every screen
    /// — on a counter machine reading a server in another building, a visible
    /// pause on every click. So the answer is kept.
    ///
    /// Asked when the server is down, it throws from here rather than from the
    /// query, which means the application cannot so much as describe its own
    /// tables while the server is unreachable — including on the one screen
    /// that exists to point it somewhere else. A stated version is assumed
    /// instead, and the failure then arrives where it belongs: from the read,
    /// with a message naming the server.
    /// </summary>
    private static ServerVersion VersionOf(string connection)
    {
        lock (_versions)
        {
            if (_versions.TryGetValue(connection, out var known))
            {
                return known;
            }
        }

        ServerVersion version;

        try
        {
            version = ServerVersion.AutoDetect(connection);
        }
        catch
        {
            // MariaDB 10.4 is what the unit servers this was written against
            // run, and what XAMPP ships. Nothing here uses a feature that
            // turns on the answer.
            version = new MariaDbServerVersion(new Version(10, 4, 32));

            return version;
        }

        lock (_versions)
        {
            _versions[connection] = version;
        }

        return version;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        switch (_source.Kind)
        {
            case DatabaseKind.MySql:
                options.UseMySql(_source.Connection, VersionOf(_source.Connection));
                break;

            case DatabaseKind.Postgres:
                options.UseNpgsql(_source.Connection);
                break;

            default:
                options.UseSqlite(_source.Connection);
                break;
        }

        // Nothing here writes through a change tracker it has not first read
        // into, and several screens list thousands of rows. Tracking them all
        // costs memory and buys nothing.
        options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // ------------------------------------------------------ keys ------

        model.Entity<TitleAuthor>().HasKey(x => new { x.TitleId, x.AuthorId, x.Role });
        model.Entity<TitleCategory>().HasKey(x => new { x.TitleId, x.CategoryId });

        // --------------------------------------------- what joins what ----

        model.Entity<Title>(e =>
        {
            e.HasOne(x => x.Publisher).WithMany().HasForeignKey(x => x.PublisherId);
            e.HasMany(x => x.Copies).WithOne(x => x.Title!).HasForeignKey(x => x.TitleId);
            e.HasMany(x => x.Authors).WithOne(x => x.Title!).HasForeignKey(x => x.TitleId);
            e.HasMany(x => x.Categories).WithOne(x => x.Title!).HasForeignKey(x => x.TitleId);
            e.Ignore(x => x.FullTitle);
        });

        model.Entity<TitleAuthor>()
            .HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);

        model.Entity<TitleCategory>()
            .HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);

        model.Entity<Copy>()
            .HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);

        model.Entity<Category>()
            .HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);

        model.Entity<CopyAnnotation>()
            .HasOne(x => x.Author).WithMany().HasForeignKey(x => x.CreatedBy);

        model.Entity<User>(e =>
        {
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
            e.Ignore(x => x.Display);
            e.Ignore(x => x.Initials);
        });

        model.Entity<Author>().Ignore(x => x.Display);

        model.Entity<Member>(e =>
        {
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
            e.Ignore(x => x.Display);
        });

        model.Entity<MemberCard>()
            .HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId);

        model.Entity<Loan>(e =>
        {
            e.HasOne(x => x.Copy).WithMany().HasForeignKey(x => x.CopyId);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId);
        });

        model.Entity<Reservation>(e =>
        {
            e.HasOne(x => x.Title).WithMany().HasForeignKey(x => x.TitleId);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId);
        });

        model.Entity<Fine>(e =>
        {
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId);
            e.HasOne(x => x.Loan).WithMany().HasForeignKey(x => x.LoanId);
        });

        model.Entity<StockVerificationScan>()
            .HasOne(x => x.Copy).WithMany().HasForeignKey(x => x.CopyId);

        model.Entity<AuditLog>()
            .HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);

        // ------------------------------------------------- conventions ----

        NameColumnsTheWayTheDatabaseDoes(model);
        StoreWordsAsWords(model);

        // Only where dates and money are text. On MySQL these are real column
        // types and the driver already handles them; forcing a string through
        // would write nonsense into a DATE column.
        if (_source.Kind == DatabaseKind.Sqlite)
        {
            StampTimesLikeTheRestOfTheFile(model);
            KeepMoneyComparable(model);
        }
    }

    /// <summary>
    /// PascalCase here, snake_case there.
    ///
    /// Written once rather than as three hundred HasColumnName lines, because
    /// three hundred hand-written mappings is three hundred chances to mistype
    /// one — and a mistyped column name is not caught until the screen that
    /// uses it is opened. A property that names its own column keeps it.
    /// </summary>
    private static void NameColumnsTheWayTheDatabaseDoes(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(SnakeCase(property.Name));
                }
            }
        }
    }

    private static string SnakeCase(string name)
    {
        var text = new System.Text.StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(name[i]));
        }

        return text.ToString();
    }

    /// <summary>
    /// Every fixed vocabulary is stored as the word, not as the number of the
    /// word. That is how the PHP application wrote them, and it is also why the
    /// data file can be read by somebody who has never seen this code.
    /// </summary>
    private static void StoreWordsAsWords(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (type.IsEnum)
                {
                    property.SetProviderClrType(typeof(string));
                }
            }
        }
    }

    /// <summary>
    /// Writes every date the way this file already writes dates.
    ///
    /// Left alone, the provider stores a moment with seven decimal places of
    /// seconds — "2026-08-16 15:12:35.4628087" — while every row the PHP
    /// application wrote reads "2026-08-16 15:12:35". Both come back as the
    /// same moment here, so nothing breaks loudly; what breaks quietly is
    /// sorting and comparison in the other application's SQL, and anyone
    /// reading the table who now has two shapes of timestamp to explain.
    /// </summary>
    private static void StampTimesLikeTheRestOfTheFile(ModelBuilder model)
    {
        const string moment = "yyyy-MM-dd HH:mm:ss";
        const string day = "yyyy-MM-dd";

        var required = new ValueConverter<DateTime, string>(
            v => v.ToString(moment, CultureInfo.InvariantCulture),
            v => ReadMoment(v) ?? default);

        var optional = new ValueConverter<DateTime?, string?>(
            v => v == null ? null : v.Value.ToString(moment, CultureInfo.InvariantCulture),
            v => ReadMoment(v));

        var requiredDay = new ValueConverter<DateOnly, string>(
            v => v.ToString(day, CultureInfo.InvariantCulture),
            v => ReadDay(v) ?? default);

        var optionalDay = new ValueConverter<DateOnly?, string?>(
            v => v == null ? null : v.Value.ToString(day, CultureInfo.InvariantCulture),
            v => ReadDay(v));

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(required);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(optional);
                }
                else if (property.ClrType == typeof(DateOnly))
                {
                    property.SetValueConverter(requiredDay);
                }
                else if (property.ClrType == typeof(DateOnly?))
                {
                    property.SetValueConverter(optionalDay);
                }
            }
        }
    }

    /// <summary>
    /// Money as a number rather than as text.
    ///
    /// SQLite has no decimal type, and left alone the provider writes these as
    /// strings — which reads back correctly here and sorts wrongly everywhere
    /// else, because "9.00" is greater than "10.00" when compared as text. A
    /// fine of nine rupees is not larger than a fine of ten, and no report
    /// should have to know which of those it is looking at.
    /// </summary>
    private static void KeepMoneyComparable(ModelBuilder model)
    {
        var required = new ValueConverter<decimal, double>(
            v => (double)v,
            v => (decimal)v);

        var optional = new ValueConverter<decimal?, double?>(
            v => v == null ? null : (double)v.Value,
            v => v == null ? null : (decimal)v.Value);

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(decimal))
                {
                    property.SetValueConverter(required);
                }
                else if (property.ClrType == typeof(decimal?))
                {
                    property.SetValueConverter(optional);
                }
            }
        }
    }

    /// <summary>
    /// Reading is forgiving, because these columns hold whatever years of the
    /// older application put there — including the occasional empty string
    /// where a null was meant. A date nobody can parse becomes no date, which
    /// every screen already knows how to show.
    /// </summary>
    private static DateTime? ReadMoment(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when
            : null;

    private static DateOnly? ReadDay(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? DateOnly.FromDateTime(when)
            : null;
}
