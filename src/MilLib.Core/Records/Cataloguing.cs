using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// One person on a title, as the form holds them before they are a row.
///
/// A name and a rank rather than an author id, because a unit library
/// catalogues from the book in hand: whoever wrote it is very often somebody
/// this catalogue has never heard of, and stopping to create an author record
/// first is a step nobody would thank anybody for.
/// </summary>
public record AuthorEntry(string Name, string? Rank, AuthorRole Role);

/// <summary>
/// Writing the bibliographic record — the half of the two-level model that is
/// about the work rather than the objects.
///
/// Nothing here touches a copy. Adding a title creates no accession number and
/// puts nothing on the register: the work exists as a description first, and
/// becomes physical only when somebody accessions copies of it. That is why
/// there is no quantity on this form and never will be.
///
/// Authors and publishers are found or created by name, exactly as the PHP
/// does it, so the two applications grow one authority list rather than two.
/// </summary>
public class Cataloguing(MilLibDbContext db)
{
    /// <summary>A title that has not been saved yet, with the defaults a new one starts on.</summary>
    public static Title Fresh() => new()
    {
        Language = "English",
        ClassificationSch = ClassificationScheme.DDC,
        MaterialType = MaterialType.BOOK,
        SecurityClass = SecurityClass.UNCLASSIFIED,
    };

    /// <summary>
    /// What is wrong with this record, in words, or nothing.
    ///
    /// Deliberately short. A real catalogue is messy — classification numbers
    /// are inconsistent, pagination is written six ways, half the older books
    /// have no ISBN — and a form that refuses those refuses the library. Only
    /// the things that would make the record useless are checked.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProblemsWithAsync(Title title, int? pubYear)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(title.Name))
        {
            problems.Add("A title is needed — it is what the book is found by.");
        }

        if (pubYear is { } year && (year < 1000 || year > DateTime.Now.Year + 1))
        {
            problems.Add($"A year of {year} is not a year this book was published in.");
        }

        // Not refused, only warned about, because a library legitimately holds
        // two editions catalogued under the same title. It is said so that
        // somebody about to catalogue the same book twice notices.
        if (!string.IsNullOrWhiteSpace(title.Isbn))
        {
            var isbn = title.Isbn.Trim();

            var clash = await db.Titles
                .Where(t => t.Isbn == isbn && t.TitleId != title.TitleId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();

            if (clash is not null)
            {
                problems.Add($"That ISBN is already on \"{clash}\". "
                    + "Clear the ISBN if this really is a separate record.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Save the work — new or existing — with its authors, its publisher and
    /// its subjects.
    ///
    /// All of it in one save, because a title whose authors did not attach is
    /// a title nobody will find by its author, and the failure would be silent.
    /// </summary>
    public async Task<long> SaveAsync(
        Title title,
        string? publisherName,
        IReadOnlyList<AuthorEntry> authors,
        IReadOnlyList<long> subjectIds,
        long byUserId)
    {
        var isNew = title.TitleId == 0;

        title.Name = title.Name.Trim();
        title.PublisherId = await PublisherIdAsync(publisherName);
        title.UpdatedBy = byUserId;
        title.UpdatedAt = DateTime.Now;

        if (isNew)
        {
            title.CreatedBy = byUserId;
            title.CreatedAt = DateTime.Now;

            db.Titles.Add(title);
        }
        else
        {
            db.Titles.Update(title);
        }

        await db.SaveAndForgetAsync();

        await AttachAuthorsAsync(title.TitleId, authors);
        await AttachSubjectsAsync(title.TitleId, subjectIds);

        await Journal.NoteAloneAsync(db, byUserId, isNew ? "TITLE_CREATE" : "TITLE_UPDATE",
            "title", title.TitleId, new
            {
                title.Name,
                authors = authors.Count,
                subjects = subjectIds.Count,
                classification = title.SecurityClass.ToString(),
            });

        return title.TitleId;
    }

    /// <summary>
    /// The publisher, by name, created if this is the first book from them.
    ///
    /// Matched without regard to case or surrounding space, so "HMSO", "hmso "
    /// and "HMSO" are the one publisher rather than three. Done in memory
    /// because SQLite compares text case-sensitively and the list is short.
    /// </summary>
    private async Task<long?> PublisherIdAsync(string? name)
    {
        name = name?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var existing = (await db.Publishers.AsNoTracking().ToListAsync())
            .FirstOrDefault(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing.PublisherId;
        }

        var made = new Publisher
        {
            Name = name,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        db.Publishers.Add(made);

        await db.SaveAndForgetAsync();

        return made.PublisherId;
    }

    /// <summary>
    /// The people on the title, in the order the form listed them.
    ///
    /// The order is the order on the title page and is worth keeping: the
    /// first-named author is how a book is cited and how it sorts on a shelf
    /// list. Replaced wholesale rather than merged, because the form shows all
    /// of them and what it shows is what is meant.
    /// </summary>
    private async Task AttachAuthorsAsync(long titleId, IReadOnlyList<AuthorEntry> authors)
    {
        await db.TitleAuthors.Where(ta => ta.TitleId == titleId).ExecuteDeleteAsync();

        var known = await db.Authors.AsNoTracking().ToListAsync();

        var order = 0;

        // The same person twice on one title would be the same primary key
        // twice, and the save would fail on a form that looked perfectly
        // reasonable. Kept by (person, role), which is what the key is.
        var placed = new HashSet<(long, AuthorRole)>();

        foreach (var entry in authors)
        {
            var name = entry.Name.Trim();

            if (name.Length == 0)
            {
                continue;
            }

            var author = known.FirstOrDefault(a =>
                string.Equals(a.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

            if (author is null)
            {
                author = new Author
                {
                    Name = name,
                    Rank = string.IsNullOrWhiteSpace(entry.Rank) ? null : entry.Rank.Trim(),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                db.Authors.Add(author);

                await db.SaveAndForgetAsync();

                known.Add(author);
            }
            else if (!string.IsNullOrWhiteSpace(entry.Rank) && string.IsNullOrWhiteSpace(author.Rank))
            {
                // A rank learnt now for somebody catalogued without one before.
                await db.Authors
                    .Where(a => a.AuthorId == author.AuthorId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Rank, entry.Rank!.Trim()));

                author.Rank = entry.Rank.Trim();
            }

            if (!placed.Add((author.AuthorId, entry.Role)))
            {
                continue;
            }

            db.TitleAuthors.Add(new TitleAuthor
            {
                TitleId = titleId,
                AuthorId = author.AuthorId,
                Role = entry.Role,
                SortOrder = order++,
            });
        }

        await db.SaveAndForgetAsync();
    }

    private async Task AttachSubjectsAsync(long titleId, IReadOnlyList<long> subjectIds)
    {
        await db.TitleCategories.Where(tc => tc.TitleId == titleId).ExecuteDeleteAsync();

        foreach (var id in subjectIds.Distinct())
        {
            db.TitleCategories.Add(new TitleCategory { TitleId = titleId, CategoryId = id });
        }

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Why this title cannot be removed, or null.
    ///
    /// One reason, and it is absolute: a copy of it has been accessioned. Those
    /// numbers are the register's, they are never reissued, and a register that
    /// refers to a title nobody can look up is a register with a hole in it.
    /// Getting rid of the books is condemnation, on the Withdrawals screen, and
    /// that leaves the record standing on purpose.
    /// </summary>
    public async Task<string?> WhyNotRemovableAsync(long titleId)
    {
        var copies = await db.Copies.CountAsync(c => c.TitleId == titleId);

        if (copies == 0)
        {
            return null;
        }

        return $"{copies} {(copies == 1 ? "copy has" : "copies have")} been accessioned against this "
            + "book, and those numbers are in the register for good. To take the books off the "
            + "shelves, condemn them on the Withdrawals screen — the record stays either way.";
    }

    /// <summary>
    /// Remove a title that never became anything physical — a duplicate, or
    /// something catalogued in error before any copy was accessioned.
    /// </summary>
    public async Task RemoveAsync(Title title, long byUserId)
    {
        await db.TitleAuthors.Where(ta => ta.TitleId == title.TitleId).ExecuteDeleteAsync();
        await db.TitleCategories.Where(tc => tc.TitleId == title.TitleId).ExecuteDeleteAsync();

        db.Titles.Remove(title);

        Journal.Note(db, byUserId, "TITLE_REMOVED", "title", title.TitleId, new { title.Name });

        await db.SaveAndForgetAsync();
    }

    /// <summary>Every subject heading, for the ticking list.</summary>
    public async Task<IReadOnlyList<Category>> SubjectsAsync() =>
        await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

    /// <summary>The subjects already on one title.</summary>
    public async Task<IReadOnlyList<long>> SubjectsOnAsync(long titleId) =>
        await db.TitleCategories
            .Where(tc => tc.TitleId == titleId)
            .Select(tc => tc.CategoryId)
            .ToListAsync();

    /// <summary>The people on one title, in the order they are printed.</summary>
    public async Task<IReadOnlyList<AuthorEntry>> AuthorsOnAsync(long titleId)
    {
        var rows = await db.TitleAuthors
            .Where(ta => ta.TitleId == titleId)
            .OrderBy(ta => ta.SortOrder)
            .Join(db.Authors, ta => ta.AuthorId, a => a.AuthorId,
                (ta, a) => new { a.Name, a.Rank, ta.Role })
            .ToListAsync();

        return [.. rows.Select(r => new AuthorEntry(r.Name, r.Rank, r.Role))];
    }

    /// <summary>Every publisher already known, so the same one is not typed two ways.</summary>
    public async Task<IReadOnlyList<string>> PublishersAsync() =>
        await db.Publishers.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

    /// <summary>
    /// Languages offered as suggestions. The column is free text — this is a
    /// list to save typing, not a list of what is allowed.
    /// </summary>
    public static IReadOnlyList<string> Languages { get; } =
    [
        "English", "Hindi", "Sanskrit", "Urdu", "Punjabi", "Bengali", "Assamese",
        "Tamil", "Telugu", "Marathi", "Gujarati", "Kannada", "Malayalam", "Odia",
        "Kashmiri", "Dogri", "Nepali", "Manipuri", "Konkani", "Bodo",
    ];
}
