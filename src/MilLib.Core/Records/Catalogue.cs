using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>One book on the shelf, with everything a screen needs to say about it.</summary>
public record CopyOnShelf(Copy Copy, Branch? Branch, Loan? OnLoan, Member? HeldBy);

/// <summary>A whole work, and every physical copy of it.</summary>
public record BookRecord(
    Title Title,
    Publisher? Publisher,
    IReadOnlyList<Author> Authors,
    IReadOnlyList<Category> Subjects,
    IReadOnlyList<CopyOnShelf> Copies);

/// <summary>
/// The catalogue side: reading one work in full, and the few things that may be
/// changed about a copy after it has been accessioned.
///
/// What may not be changed is the point. An accession number, the date it was
/// accessioned and how it was acquired are the register's entry for that book,
/// and a register that can be edited is not a register. Corrections are made by
/// appending a note, never by overwriting what was written.
/// </summary>
public class Catalogue(MilLibDbContext db)
{
    public async Task<BookRecord?> ReadAsync(long titleId)
    {
        var title = await db.Titles.FirstOrDefaultAsync(t => t.TitleId == titleId);

        if (title is null)
        {
            return null;
        }

        var publisher = title.PublisherId is null
            ? null
            : await db.Publishers.FirstOrDefaultAsync(p => p.PublisherId == title.PublisherId);

        var authors = await db.TitleAuthors
            .Where(ta => ta.TitleId == titleId)
            .OrderBy(ta => ta.SortOrder)
            .Join(db.Authors, ta => ta.AuthorId, a => a.AuthorId, (ta, a) => a)
            .ToListAsync();

        var subjects = await db.TitleCategories
            .Where(tc => tc.TitleId == titleId)
            .Join(db.Categories, tc => tc.CategoryId, c => c.CategoryId, (tc, c) => c)
            .OrderBy(c => c.Name)
            .ToListAsync();

        // Ordered by the accession sequence, which is the order the register
        // itself is in — not by when the row happened to be written.
        var copies = await db.Copies
            .Where(c => c.TitleId == titleId)
            .OrderBy(c => c.AccessionSeq)
            .ThenBy(c => c.AccessionNo)
            .ToListAsync();

        var ids = copies.Select(c => c.CopyId).ToList();

        var loans = await db.Loans
            .Where(l => ids.Contains(l.CopyId)
                     && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE))
            .ToListAsync();

        var holderIds = loans.Select(l => l.MemberId).Distinct().ToList();

        var holders = await db.Members
            .Where(m => holderIds.Contains(m.MemberId))
            .ToDictionaryAsync(m => m.MemberId);

        var branches = await db.Branches.ToDictionaryAsync(b => b.BranchId);

        var onShelf = copies.Select(copy =>
        {
            var loan = loans.FirstOrDefault(l => l.CopyId == copy.CopyId);

            return new CopyOnShelf(
                copy,
                copy.BranchId is null ? null : branches.GetValueOrDefault(copy.BranchId.Value),
                loan,
                loan is null ? null : holders.GetValueOrDefault(loan.MemberId));
        }).ToList();

        return new BookRecord(title, publisher, authors, subjects, onShelf);
    }

    /// <summary>The notes appended against one copy, newest first.</summary>
    public async Task<IReadOnlyList<(CopyAnnotation Note, string By)>> NotesOnAsync(long copyId)
    {
        var rows = await db.CopyAnnotations
            .Where(a => a.CopyId == copyId)
            .OrderByDescending(a => a.AnnotationId)
            .ToListAsync();

        var authorIds = rows.Where(a => a.CreatedBy is not null).Select(a => a.CreatedBy!.Value).Distinct().ToList();

        var authors = await db.Users
            .Where(u => authorIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Display);

        return
        [
            .. rows.Select(a => (a, a.CreatedBy is null
                ? "somebody"
                : authors.GetValueOrDefault(a.CreatedBy.Value, "somebody")))
        ];
    }

    /// <summary>
    /// The operational facts about a copy: where it is, what state it is in,
    /// and whether it circulates.
    ///
    /// Deliberately nothing else. The accession number, the date and the
    /// acquisition record are the register's entry and are not editable from
    /// anywhere in this application.
    /// </summary>
    public async Task ReviseCopyAsync(
        Copy copy, CopyStatus status, CopyCondition condition,
        string? location, bool circulating, long byUserId)
    {
        var was = (copy.Status, copy.Condition, copy.Location, copy.IsCirculating);

        copy.Status = status;
        copy.Condition = condition;
        copy.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        copy.IsCirculating = circulating;
        copy.UpdatedBy = byUserId;
        copy.UpdatedAt = DateTime.Now;

        db.Copies.Update(copy);

        Journal.Note(db, byUserId, "COPY_UPDATED", "copy", copy.CopyId, new
        {
            copy.AccessionNo,
            from = new { status = was.Status.ToString(), condition = was.Condition.ToString() },
            to = new { status = status.ToString(), condition = condition.ToString() },
        });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Add a note to a copy. Appended and never edited — this is how the
    /// register records a correction without anybody rewriting the original.
    /// </summary>
    public async Task AnnotateAsync(Copy copy, string note, long byUserId)
    {
        db.CopyAnnotations.Add(new CopyAnnotation
        {
            CopyId = copy.CopyId,
            Note = note.Trim(),
            CreatedBy = byUserId,
            CreatedAt = DateTime.Now,
        });

        Journal.Note(db, byUserId, "COPY_ANNOTATED", "copy", copy.CopyId,
            new { copy.AccessionNo });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// Why this copy cannot be marked as it is being asked to be, or null.
    ///
    /// The one that matters: a book somebody is holding cannot be put back on
    /// the shelf by editing it. It comes back through the counter, where the
    /// loan is closed and the condition recorded — anything else leaves a loan
    /// open against a book the register says is available.
    /// </summary>
    public async Task<string?> WhyNotAsync(Copy copy, CopyStatus wanted)
    {
        var out_ = await db.Loans.AnyAsync(l => l.CopyId == copy.CopyId
            && (l.Status == LoanStatus.OPEN || l.Status == LoanStatus.OVERDUE));

        if (!out_)
        {
            return null;
        }

        return wanted == CopyStatus.ISSUED
            ? null
            : "Somebody is holding this book. Take it back at the counter — that closes the loan and records the condition. Changing it here would leave the loan open.";
    }
}
