using Microsoft.EntityFrameworkCore;
using MilLib.Core.Documents;

namespace MilLib.Core.Data;

/// <summary>
/// Reading the accession register.
///
/// Strictly in accession order, because that is the order the register is in —
/// not the order rows happen to sit in the table, and not alphabetically by
/// title. A register sorted any other way is a list, not a register.
///
/// Read-only, deliberately. Nothing anywhere in this application edits an entry.
/// </summary>
public class Register(MilLibDbContext db, Preferences preferences)
{
    /// <summary>
    /// The whole register, or a stretch of it.
    ///
    /// A range is given as the numbers a person would read off two pages of the
    /// bound ledger, so it is inclusive at both ends.
    /// </summary>
    public async Task<IReadOnlyList<RegisterEntry>> ReadAsync(int? from = null, int? to = null)
    {
        var copies = db.Copies.AsQueryable();

        if (from is not null)
        {
            copies = copies.Where(c => c.AccessionSeq >= from);
        }

        if (to is not null)
        {
            copies = copies.Where(c => c.AccessionSeq <= to);
        }

        var rows = await copies
            .OrderBy(c => c.AccessionSeq)
            .ThenBy(c => c.AccessionNo)
            .Join(db.Titles, c => c.TitleId, t => t.TitleId, (c, t) => new { c, t })
            .Select(x => new
            {
                x.c.AccessionNo,
                x.c.AccessionSeq,
                x.c.BookNo,
                x.c.AccessionDate,
                x.c.LedgerName,
                x.c.Source,
                x.c.BillNo,
                x.c.Cost,
                x.c.Status,
                x.t.TitleId,
                Title = x.t.Name,
                x.t.Edition,
                x.t.PubYear,
                x.t.Pages,
                x.t.ClassificationNo,
                Publisher = x.t.Publisher!.Name,
            })
            .ToListAsync();

        // The authors are fetched for the whole run at once rather than per
        // row. Fourteen hundred entries is fourteen hundred round trips
        // otherwise, to print one document.
        var titleIds = rows.Select(r => r.TitleId).Distinct().ToList();

        var authorsByTitle = (await db.TitleAuthors
                .Where(ta => titleIds.Contains(ta.TitleId))
                .OrderBy(ta => ta.SortOrder)
                .Join(db.Authors, ta => ta.AuthorId, a => a.AuthorId,
                    (ta, a) => new { ta.TitleId, a.Name })
                .ToListAsync())
            .GroupBy(x => x.TitleId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name)));

        return
        [
            .. rows.Select(r => new RegisterEntry(
                preferences.Accession(r.AccessionNo),
                r.AccessionSeq,
                r.BookNo,
                r.AccessionDate,
                authorsByTitle.GetValueOrDefault(r.TitleId, ""),

                // The edition belongs with the title on a register line, the
                // way a catalogue card writes it.
                string.IsNullOrWhiteSpace(r.Edition) ? r.Title : $"{r.Title} ({r.Edition})",

                r.LedgerName,
                r.Publisher,
                r.PubYear,
                r.Pages,
                r.Source,
                r.BillNo,
                r.Cost,
                r.ClassificationNo,
                r.Status))
        ];
    }

    /// <summary>The lowest and highest numbers in use, for the range boxes to start from.</summary>
    public async Task<(int First, int Last)> ExtentAsync()
    {
        var first = await db.Copies.MinAsync(c => (int?)c.AccessionSeq) ?? 0;
        var last = await db.Copies.MaxAsync(c => (int?)c.AccessionSeq) ?? 0;

        return (first, last);
    }
}
