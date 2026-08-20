using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// Reading back what was done.
///
/// The other side of <see cref="Journal"/>: the same table, read newest first.
/// Only read — there is deliberately no way to edit or delete an entry from
/// anywhere in either application, because a record of what happened is worth
/// having only if it cannot be tidied up afterwards.
///
/// It is one table for both programs, so it shows what somebody did in the web
/// application beside what somebody did here. That is the point of it.
/// </summary>
public class Activity(MilLibDbContext db)
{
    /// <summary>How many entries a page holds. Enough to scroll, not enough to hang.</summary>
    public const int PageSize = 200;

    /// <summary>
    /// One page of history, newest first.
    ///
    /// The log grows without bound — a busy library writes thousands of rows a
    /// month — so it is never read whole. Filtering happens in SQL for the same
    /// reason: narrowing after reading everything would defeat the point.
    /// </summary>
    public async Task<IReadOnlyList<Happening>> ReadAsync(
        string search = "", ActivityKind kind = ActivityKind.Everything,
        long? userId = null, DateOnly? since = null, DateOnly? until = null,
        int page = 0, int pageSize = PageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, PageSize);

        var rows = await Matching(search, kind, userId, since, until)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.LogId)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.LogId,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.Details,
                a.CreatedAt,
                a.UserId,
                a.SecurityClass,
                Who = a.User == null ? null : (a.User.FullName.Length > 0 ? a.User.FullName : a.User.Username),
            })
            .ToListAsync();

        return [.. rows.Select(r => new Happening(
            r.LogId,
            r.CreatedAt,
            r.Who ?? (r.UserId is null ? "the system" : "a deleted account"),
            r.Action,
            r.EntityType,
            r.EntityId,
            r.SecurityClass,
            Retell(r.Action, r.EntityType, r.Details)))];
    }

    /// <summary>
    /// How many entries match — the whole log when nothing is narrowing it.
    ///
    /// Counted with the very same filters the page was read with, so the
    /// figure under the boxes is about what is on the screen. Counted against
    /// the whole table instead, it read "11 of 20" while the filter had picked
    /// out eleven of them — true only by coincidence, and wrong the moment
    /// there is a second page.
    /// </summary>
    public async Task<int> CountAsync(
        string search = "", ActivityKind kind = ActivityKind.Everything,
        long? userId = null, DateOnly? since = null, DateOnly? until = null) =>
        await Matching(search, kind, userId, since, until).CountAsync();

    /// <summary>
    /// The filters, in one place, because the list and its count have to mean
    /// the same thing.
    /// </summary>
    private IQueryable<AuditLog> Matching(
        string search, ActivityKind kind, long? userId, DateOnly? since, DateOnly? until)
    {
        var query = db.AuditLog.AsNoTracking().AsQueryable();

        if (userId is not null)
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (since is not null)
        {
            var from = since.Value.ToDateTime(TimeOnly.MinValue);

            query = query.Where(a => a.CreatedAt >= from);
        }

        if (until is not null)
        {
            // To the end of that day, not to its first instant — "until the
            // 14th" means the 14th is included, which is what anybody typing a
            // date into a box means by it.
            var to = until.Value.ToDateTime(TimeOnly.MaxValue);

            query = query.Where(a => a.CreatedAt <= to);
        }

        // LIKE rather than StartsWith, because SQLite's LIKE ignores case for
        // ASCII and its plain string comparison does not. The actions are not
        // uniformly upper case — the sign-in ones are not — and a filter that
        // silently skipped those would hide the entries most worth finding.
        query = kind switch
        {
            ActivityKind.Circulation => query.Where(a =>
                EF.Functions.Like(a.Action, "ISSUE%")
                || EF.Functions.Like(a.Action, "RETURN%")
                || EF.Functions.Like(a.Action, "RENEW%")),

            ActivityKind.Books => query.Where(a =>
                EF.Functions.Like(a.Action, "COP%")
                || EF.Functions.Like(a.Action, "STOCK%")),

            ActivityKind.Members => query.Where(a => EF.Functions.Like(a.Action, "MEMBER%")),

            ActivityKind.Money => query.Where(a => EF.Functions.Like(a.Action, "FINE%")),

            ActivityKind.Accounts => query.Where(a =>
                EF.Functions.Like(a.Action, "USER%")
                || EF.Functions.Like(a.Action, "%LOGIN%")
                || EF.Functions.Like(a.Action, "PASSWORD%")),

            ActivityKind.Settings => query.Where(a =>
                EF.Functions.Like(a.Action, "SETTINGS%")
                || EF.Functions.Like(a.Action, "FEATURE%")),

            _ => query,
        };

        if (search.Trim().Length > 0)
        {
            var term = search.Trim();

            query = query.Where(a =>
                EF.Functions.Like(a.Action, $"%{term}%")
                || EF.Functions.Like(a.EntityType, $"%{term}%")
                || (a.Details != null && EF.Functions.Like(a.Details, $"%{term}%")));
        }

        return query;
    }

    /// <summary>Everybody who has ever done anything, for the "by whom" list.</summary>
    public async Task<IReadOnlyList<(long Id, string Name)>> WhoHasActedAsync()
    {
        var ids = await db.AuditLog
            .Where(a => a.UserId != null)
            .Select(a => a.UserId!.Value)
            .Distinct()
            .ToListAsync();

        var people = await db.Users
            .Where(u => ids.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FullName, u.Username })
            .ToListAsync();

        return [.. people
            .Select(p => (p.UserId, p.FullName.Length > 0 ? p.FullName : p.Username))
            .OrderBy(p => p.Item2)];
    }

    /// <summary>
    /// One entry said as a sentence.
    ///
    /// The action is a name for a program to match on; this is the line a
    /// person reads down a column of. The stored details are JSON, and they are
    /// shown as they were written rather than interpreted — an entry written by
    /// a version of either application that this one has never heard of should
    /// still say something rather than nothing.
    /// </summary>
    private static string Retell(string action, string entityType, string? details)
    {
        var said = Spoken(action, entityType);

        if (string.IsNullOrWhiteSpace(details))
        {
            return said;
        }

        var facts = Unpack(details);

        return facts.Length == 0 ? said : $"{said} — {facts}";
    }

    private static string Spoken(string action, string entityType) => action.ToUpperInvariant() switch
    {
        "USER_LOGIN" => "Signed in",
        "USER_LOGOUT" => "Signed out",
        "FAILED_LOGIN" => "A sign-in was refused",
        "PASSWORD_CHANGE" => "Changed their own password",
        "USER_CREATE" => "Added a staff account",
        "USER_UPDATE" => "Edited a staff account",
        "USER_REACTIVATE" => "Reinstated a staff account",
        "USER_DEACTIVATE" => "Suspended a staff account",
        "USER_PASSWORD_RESET" => "Reset somebody's password",
        "ISSUE" => "Issued a book",
        "ISSUE_OVERRIDE" => "Issued a book over a block",
        "ISSUE_CLASSIFIED" => "Issued a classified book",
        "RETURN" => "Took a book back",
        "RENEW" => "Renewed a loan",
        "COPIES_ACCESSIONED" => "Accessioned copies",
        "COPIES_WITHDRAWN" => "Withdrew copies",
        "COPY_UPDATED" => "Edited a copy",
        "COPY_ANNOTATED" => "Noted something against a copy",
        "MEMBER_ENROLLED" => "Enrolled a member",
        "MEMBER_UPDATED" => "Edited a member",
        "MEMBER_REMOVED" => "Removed a member",
        "MEMBER_CLEARED" => "Certified a member has no dues",
        "MEMBER_PASS_REISSUED" => "Reissued a pass",
        "FINE_RAISED" => "Raised a charge",
        "FINE_PAID" => "Took payment of a charge",
        "FINE_WAIVED" => "Waived a charge",
        "STOCK_CHECK_STARTED" => "Started a stock check",
        "STOCK_CHECK_CLOSED" => "Closed a stock check",
        "STOCK_CHECK_ABANDONED" => "Abandoned a stock check",
        "CATEGORY_REMOVED" => "Removed a member category",
        "SETTINGS_BRANDING" => "Changed the branding",
        "SETTINGS_ACCESSION" => "Changed the accession numbering",
        "SETTINGS_LABELS" => "Changed the label sizes",
        "FEATURE_ON" => "Turned a screen on",
        "FEATURE_OFF" => "Turned a screen off",
        "VIEW_CLASSIFIED" => "Looked at the classified holdings",

        // Something neither program in this folder wrote, or something added
        // since. Said as best it can be rather than left blank.
        _ => Readable(action) + (entityType.Length > 0 ? $" ({entityType})" : ""),
    };

    /// <summary>SOMETHING_LIKE_THIS becomes Something like this.</summary>
    private static string Readable(string action)
    {
        var words = action.Replace('_', ' ').ToLowerInvariant().Trim();

        return words.Length == 0 ? "Something happened"
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// The stored JSON, flattened into "key: value, key: value".
    ///
    /// Anything that is not JSON at all is shown as it stands. An entry from an
    /// older scheme is still evidence of something and should not be swallowed
    /// by an exception.
    /// </summary>
    private static string Unpack(string details)
    {
        try
        {
            using var document = JsonDocument.Parse(details);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return details.Trim();
            }

            var parts = new List<string>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.True => "yes",
                    JsonValueKind.False => "no",
                    _ => property.Value.ToString(),
                };

                if (value.Length > 0)
                {
                    parts.Add($"{Readable(property.Name).ToLowerInvariant()}: {value}");
                }
            }

            return string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return details.Trim();
        }
    }
}

/// <summary>One thing that was done, ready for the screen.</summary>
public record Happening(
    long Id,
    DateTime When,
    string Who,
    string Action,
    string EntityType,
    long? EntityId,
    SecurityClass? SecurityClass,
    string Said)
{
    public string Day => When.ToString("dd MMM yyyy");

    public string Time => When.ToString("HH:mm");

    /// <summary>
    /// Whether this is one to look twice at.
    ///
    /// Refused sign-ins, changes to who may get in, and anything touching
    /// classified material. Not because they are wrong — most are perfectly
    /// ordinary — but because they are the ones a person scanning this list is
    /// scanning it for.
    ///
    /// Named one at a time rather than matched on "USER_", which caught
    /// user_login as well and turned every row in the column red. A column
    /// where everything is marked teaches the eye to stop reading the marks.
    /// </summary>
    public bool Notable =>
        Watched.Contains(Action)
        || SecurityClass is not null and not Data.SecurityClass.UNCLASSIFIED;

    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILED_LOGIN",
        "USER_CREATE",
        "USER_UPDATE",
        "USER_REACTIVATE",
        "USER_DEACTIVATE",
        "USER_PASSWORD_RESET",
        "PASSWORD_CHANGE",
        "VIEW_CLASSIFIED",
    };
}

public enum ActivityKind
{
    Everything,
    Circulation,
    Books,
    Members,
    Money,
    Accounts,
    Settings,
}
