using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// Handing out accession numbers.
///
/// The accession number is the library's statutory identity for one physical
/// book. It is sequential, it has no gaps, and it is never reused — not even
/// when the copy it belonged to is withdrawn the same afternoon. A register
/// with a gap in it is a register somebody has to explain.
///
/// All of it lives here so that the one guarantee that matters — two people
/// accessioning at the same moment never get the same number — is made in one
/// place rather than in every screen that adds a book.
/// </summary>
public class Accession(MilLibDbContext db, Preferences preferences)
{
    private const string Scope = "default";

    /// <summary>Stored form: the zero-padded sequence, with no prefix.</summary>
    public string Format(int seq) =>
        seq.ToString(new string('0', Math.Max(1, preferences.AccessionPadLength)));

    /// <summary>Display form: what the unit calls it, prefix and all.</summary>
    public string Display(int seq) => preferences.Accession(Format(seq));

    /// <summary>The number the next copy would be given, for the form to show.</summary>
    public async Task<int> PeekAsync() =>
        await db.AccessionCounters
            .Where(c => c.Scope == Scope)
            .Select(c => (int?)c.NextSeq)
            .FirstOrDefaultAsync() ?? 1;

    /// <summary>
    /// Accession copies of a title — one, or a shelf-load — each with the next
    /// number, all or nothing.
    ///
    /// The counter is bumped before anything is read back from it, which is
    /// what makes this safe: that write takes the lock, so a second caller
    /// waits rather than reading the same number. If any part of the batch
    /// fails, the whole thing rolls back and no number is spent — which is the
    /// other half of "gap-free".
    /// </summary>
    public async Task<IReadOnlyList<Copy>> AccessionAsync(
        long titleId, int quantity, Copy pattern, long byUserId)
    {
        quantity = Math.Max(1, quantity);

        await using var transaction = await db.Database.BeginTransactionAsync();

        await EnsureCounterAsync();

        // Bump first, read second. The other order lets two people read the
        // same number and then both write it, and the unique index on
        // accession_seq turns that into a failed save at the counter rather
        // than a wait of a few milliseconds.
        await db.AccessionCounters
            .Where(c => c.Scope == Scope)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.NextSeq, c => c.NextSeq + quantity));

        var after = await db.AccessionCounters
            .Where(c => c.Scope == Scope)
            .Select(c => c.NextSeq)
            .FirstAsync();

        var first = after - quantity;
        var now = DateTime.Now;
        var made = new List<Copy>(quantity);

        for (var i = 0; i < quantity; i++)
        {
            var seq = first + i;
            var number = Format(seq);

            made.Add(new Copy
            {
                TitleId = titleId,
                AccessionSeq = seq,
                AccessionNo = number,

                // The barcode carries the accession number, so the label and
                // the register say the same thing and a scan at the counter
                // finds the book either way.
                Barcode = number,

                AccessionDate = pattern.AccessionDate,
                Source = pattern.Source,
                Supplier = pattern.Supplier,
                BillNo = pattern.BillNo,
                BillDate = pattern.BillDate,
                Cost = pattern.Cost,
                BranchId = pattern.BranchId,
                Location = pattern.Location,
                Condition = pattern.Condition,
                Remarks = pattern.Remarks,
                IsCirculating = pattern.IsCirculating,
                Status = CopyStatus.AVAILABLE,
                CreatedBy = byUserId,
                UpdatedBy = byUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.Copies.AddRange(made);

        Journal.Note(db, byUserId, "COPIES_ACCESSIONED", "title", titleId,
            new { quantity, from = Format(first), to = Format(first + quantity - 1) });

        await db.SaveAndForgetAsync();

        await transaction.CommitAsync();

        return made;
    }

    /// <summary>
    /// A library that has never accessioned anything has no counter row. It is
    /// seeded from the highest number already in use rather than from one, so
    /// an install carrying imported books does not start handing out numbers
    /// that are already on a shelf.
    /// </summary>
    private async Task EnsureCounterAsync()
    {
        if (await db.AccessionCounters.AnyAsync(c => c.Scope == Scope))
        {
            return;
        }

        var highest = await db.Copies.MaxAsync(c => (int?)c.AccessionSeq) ?? 0;

        db.AccessionCounters.Add(new AccessionCounter
        {
            Scope = Scope,
            NextSeq = Math.Max(highest + 1, Math.Max(1, preferences.AccessionStartFrom)),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        });

        await db.SaveAndForgetAsync();
    }
}
