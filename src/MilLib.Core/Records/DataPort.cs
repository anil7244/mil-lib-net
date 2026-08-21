using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;

namespace MilLib.Core.Records;

/// <summary>Which set of records is being taken in or out.</summary>
public enum PortSet
{
    Books,
    Members,
}

/// <summary>What an import did: how many rows became records, and what went wrong.</summary>
public sealed record ImportOutcome(int Added, int Skipped, IReadOnlyList<string> Problems)
{
    public bool AnyProblems => Problems.Count > 0;

    public string Summary => Added == 0 && Skipped == 0
        ? "The file had no rows to import."
        : $"{Added} added" + (Skipped > 0 ? $", {Skipped} skipped" : "") + ".";
}

/// <summary>
/// Books and members, out to a spreadsheet and back in from one.
///
/// The whole point is that the file in the middle is an ordinary spreadsheet a
/// person edits by hand. So export and template are the same shape — a template
/// is just an export with no rows — and import reads a file by the names of its
/// columns rather than their order, so a unit that rearranged the sheet, or
/// filled in the export of another library, is still understood.
///
/// Records are made through the same <see cref="Cataloguing"/> and
/// <see cref="Roll"/> the screens use, so an imported book or member is a book
/// or member catalogued the ordinary way — validated the same, journalled the
/// same — not a row poked into a table behind the rules' back.
/// </summary>
public class DataPort(MilLibDbContext db)
{
    // ================================================================ books ==

    public async Task<Sheet> ExportBooksAsync()
    {
        var titles = await db.Titles
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Name,
                t.Subtitle,
                Authors = t.Authors.OrderBy(a => a.SortOrder).Select(a => a.Author!.Name).ToList(),
                Publisher = t.Publisher!.Name,
                t.Isbn,
                t.Edition,
                t.PubYear,
                t.PubPlace,
                t.Pages,
                t.Language,
                t.CallNumber,
                t.ClassificationNo,
                t.SubjectHeadings,
                t.MaterialType,
                t.SecurityClass,
                t.IsUnitPublication,
                Copies = t.Copies.Count(),
                Accession = t.Copies.OrderBy(c => c.CopyId).Select(c => c.AccessionNo).FirstOrDefault(),
                t.Notes,
            })
            .ToListAsync();

        var rows = titles.Select(t => (IReadOnlyList<string>)new[]
        {
            t.Name,
            t.Subtitle ?? "",
            string.Join("; ", t.Authors),
            t.Publisher ?? "",
            t.Isbn ?? "",
            t.Edition ?? "",
            t.PubYear?.ToString() ?? "",
            t.PubPlace ?? "",
            t.Pages ?? "",
            t.Language,
            t.CallNumber ?? "",
            t.ClassificationNo ?? "",
            t.SubjectHeadings ?? "",
            Words.Of(t.MaterialType),
            Words.Of(t.SecurityClass),
            t.IsUnitPublication ? "Yes" : "No",
            t.Copies.ToString(),
            string.IsNullOrEmpty(t.Accession) ? "" : t.Accession,
            t.Notes ?? "",
        }).ToList();

        return new Sheet("Books", BookHeaders, rows);
    }

    /// <summary>An empty books sheet — the headings and nothing under them.</summary>
    public static Sheet BookTemplate() => new("Books", BookHeaders, []);

    private static readonly string[] BookHeaders =
    [
        "Title", "Subtitle", "Authors (separate with ;)", "Publisher", "ISBN", "Edition",
        "Pub year", "Pub place", "Pages", "Language", "Call number", "Classification no",
        "Subject headings", "Material type", "Security class", "Unit publication (Yes/No)",
        "Copies", "First accession no", "Notes",
    ];

    public async Task<ImportOutcome> ImportBooksAsync(Sheet sheet, long byUserId)
    {
        var h = sheet.Headers;

        var cTitle = Col(h, "title");
        var cSub = Col(h, "subtitle");
        var cAuthors = Col(h, "author");
        var cPublisher = Col(h, "publish");
        var cIsbn = Col(h, "isbn");
        var cEdition = Col(h, "edition");
        var cYear = Col(h, "year");
        var cPlace = Col(h, "place");
        var cPages = Col(h, "page");
        var cLang = Col(h, "lang");
        var cCall = Col(h, "call");
        var cClass = Col(h, "classification");
        var cSubjects = Col(h, "subject");
        var cMaterial = Col(h, "material");
        var cSecurity = Col(h, "security");
        var cUnitPub = Col(h, "unit pub");
        var cNotes = Col(h, "note");

        if (cTitle < 0)
        {
            return new ImportOutcome(0, 0, ["The sheet has no “Title” column, so there is nothing to import from it."]);
        }

        var cataloguing = new Cataloguing(db);
        var problems = new List<string>();
        var added = 0;
        var skipped = 0;
        var line = 1;

        foreach (var row in sheet.Rows)
        {
            line++;

            var name = Cell(row, cTitle);

            if (name.Length == 0)
            {
                continue;
            }

            var title = new Title
            {
                Name = name,
                Subtitle = Or(Cell(row, cSub)),
                Isbn = Or(Cell(row, cIsbn)),
                Edition = Or(Cell(row, cEdition)),
                PubYear = Year(Cell(row, cYear)),
                PubPlace = Or(Cell(row, cPlace)),
                Pages = Or(Cell(row, cPages)),
                Language = Cell(row, cLang) is { Length: > 0 } lang ? lang : "English",
                CallNumber = Or(Cell(row, cCall)),
                ClassificationNo = Or(Cell(row, cClass)),
                SubjectHeadings = Or(Cell(row, cSubjects)),
                MaterialType = Material(Cell(row, cMaterial)),
                SecurityClass = Classification(Cell(row, cSecurity)),
                IsUnitPublication = YesNo(Cell(row, cUnitPub)),
                Notes = Or(Cell(row, cNotes)),
            };

            var authors = Cell(row, cAuthors)
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => new AuthorEntry(a, null, AuthorRole.AUTHOR))
                .ToList();

            var issues = await cataloguing.ProblemsWithAsync(title, title.PubYear);

            if (issues.Count > 0)
            {
                problems.Add($"Row {line} ({name}): {issues[0]}");
                skipped++;
                continue;
            }

            await cataloguing.SaveAsync(title, Or(Cell(row, cPublisher)), authors, [], byUserId);
            added++;
        }

        return new ImportOutcome(added, skipped, problems);
    }

    // ============================================================== members ==

    public async Task<Sheet> ExportMembersAsync()
    {
        var members = await db.Members
            .OrderBy(m => m.FullName)
            .Select(m => new
            {
                m.MembershipNo,
                m.FullName,
                m.Rank,
                m.PersonnelNo,
                m.UnitCoy,
                m.Appointment,
                Category = m.Category!.Name,
                m.Phone,
                m.Email,
                m.ClearanceLevel,
                m.EnrolledOn,
                m.ValidUpto,
                m.Status,
            })
            .ToListAsync();

        var rows = members.Select(m => (IReadOnlyList<string>)new[]
        {
            m.MembershipNo,
            m.FullName,
            m.Rank ?? "",
            m.PersonnelNo ?? "",
            m.UnitCoy ?? "",
            m.Appointment ?? "",
            m.Category ?? "",
            m.Phone ?? "",
            m.Email ?? "",
            Words.Of(m.ClearanceLevel),
            m.EnrolledOn.ToString("yyyy-MM-dd"),
            m.ValidUpto?.ToString("yyyy-MM-dd") ?? "",
            Words.Of(m.Status),
        }).ToList();

        return new Sheet("Members", MemberHeaders, rows);
    }

    public static Sheet MemberTemplate() => new("Members", MemberHeaders, []);

    private static readonly string[] MemberHeaders =
    [
        "Membership no (blank to auto-number)", "Full name", "Rank", "Personnel no", "Unit/Coy",
        "Appointment", "Category (name or code)", "Phone", "Email", "Clearance",
        "Enrolled on (yyyy-mm-dd)", "Valid upto (yyyy-mm-dd)", "Status",
    ];

    public async Task<ImportOutcome> ImportMembersAsync(Sheet sheet, long byUserId)
    {
        var h = sheet.Headers;

        var cNo = Col(h, "membership");
        var cName = Col(h, "full name", "name");
        var cRank = Col(h, "rank");
        var cPno = Col(h, "personnel");
        var cUnit = Col(h, "unit");
        var cAppt = Col(h, "appoint");
        var cCat = Col(h, "categ");
        var cPhone = Col(h, "phone");
        var cEmail = Col(h, "email");
        var cClear = Col(h, "clear");
        var cValid = Col(h, "valid");

        if (cName < 0 || cCat < 0)
        {
            return new ImportOutcome(0, 0,
                ["The sheet needs a “Full name” column and a “Category” column, and one of them is missing."]);
        }

        var categories = await db.MemberCategories.ToListAsync();

        var roll = new Roll(db);
        var problems = new List<string>();
        var added = 0;
        var skipped = 0;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var line = 1;

        foreach (var row in sheet.Rows)
        {
            line++;

            var name = Cell(row, cName);

            if (name.Length == 0)
            {
                continue;
            }

            var wanted = Cell(row, cCat);

            var category = categories.FirstOrDefault(c =>
                string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Code, wanted, StringComparison.OrdinalIgnoreCase));

            if (category is null)
            {
                problems.Add($"Row {line} ({name}): no member category called “{wanted}”.");
                skipped++;
                continue;
            }

            var member = new Member
            {
                MembershipNo = Cell(row, cNo) is { Length: > 0 } given
                    ? given
                    : await roll.SuggestedNumberAsync(),
                FullName = name,
                CategoryId = category.CategoryId,
                Rank = Or(Cell(row, cRank)),
                PersonnelNo = Or(Cell(row, cPno)),
                UnitCoy = Or(Cell(row, cUnit)),
                Appointment = Or(Cell(row, cAppt)),
                Phone = Or(Cell(row, cPhone)),
                Email = Or(Cell(row, cEmail)),
                ClearanceLevel = Classification(Cell(row, cClear)),
                EnrolledOn = today,
                ValidUpto = Date(Cell(row, cValid)),
                Status = MemberStatus.ACTIVE,
            };

            var issues = await roll.ProblemsWithAsync(member, category);

            if (issues.Count > 0)
            {
                problems.Add($"Row {line} ({name}): {issues[0]}");
                skipped++;
                continue;
            }

            await roll.EnrolAsync(member, byUserId);
            added++;
        }

        return new ImportOutcome(added, skipped, problems);
    }

    // =============================================================== helpers ==

    /// <summary>
    /// The column that answers to a name, matched loosely: an exact heading
    /// first, then one that starts with the word, then one that merely contains
    /// it — so both this application's own export and a hand-made sheet are read.
    /// </summary>
    private static int Col(IReadOnlyList<string> headers, params string[] keys)
    {
        var lower = headers.Select(x => x.Trim().ToLowerInvariant()).ToList();

        foreach (var key in keys)
        {
            var exact = lower.FindIndex(x => x == key);
            if (exact >= 0) return exact;
        }

        foreach (var key in keys)
        {
            var starts = lower.FindIndex(x => x.StartsWith(key, StringComparison.Ordinal));
            if (starts >= 0) return starts;
        }

        foreach (var key in keys)
        {
            var has = lower.FindIndex(x => x.Contains(key, StringComparison.Ordinal));
            if (has >= 0) return has;
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> row, int col) =>
        col >= 0 && col < row.Count ? row[col].Trim() : "";

    private static string? Or(string value) => value.Length > 0 ? value : null;

    private static int? Year(string value) =>
        int.TryParse(value, out var y) && y is > 0 and < 3000 ? y : null;

    private static bool YesNo(string value) =>
        value.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

    private static DateOnly? Date(string value) =>
        DateOnly.TryParse(value, out var d) ? d : null;

    private static MaterialType Material(string value)
    {
        var key = new string(value.Where(char.IsLetter).ToArray()).ToUpperInvariant();

        return key switch
        {
            "PAMPHLET" => MaterialType.PAMPHLET,
            "PRECIS" => MaterialType.PRECIS,
            "MANUAL" => MaterialType.MANUAL,
            "MAP" => MaterialType.MAP,
            "PERIODICAL" => MaterialType.PERIODICAL,
            "CD" => MaterialType.CD,
            "DVD" => MaterialType.DVD,
            "OTHER" => MaterialType.OTHER,
            _ => MaterialType.BOOK,
        };
    }

    private static SecurityClass Classification(string value)
    {
        var key = new string(value.Where(char.IsLetter).ToArray()).ToUpperInvariant();

        return key switch
        {
            "RESTRICTED" => SecurityClass.RESTRICTED,
            "CONFIDENTIAL" => SecurityClass.CONFIDENTIAL,
            "SECRET" => SecurityClass.SECRET,
            "TOPSECRET" => SecurityClass.TOP_SECRET,
            _ => SecurityClass.UNCLASSIFIED,
        };
    }
}
