using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// One heading, where it sits in the tree, and how much is filed under it.
///
/// <paramref name="Depth"/> is how far in it is drawn; <paramref name="Titles"/>
/// counts only the books filed directly under it, and <paramref name="Below"/>
/// counts those filed anywhere beneath it. Both are worth saying: a heading
/// with nothing directly under it but two hundred books below is doing its job.
/// </summary>
public record SubjectNode(Category Heading, int Depth, int Titles, int Children, int Below)
{
    public long Id => Heading.CategoryId;

    public string Name => Heading.Name;

    public bool IsRoot => Heading.ParentId is null;

    /// <summary>Whether anything at all is filed under it, here or below.</summary>
    public bool InUse => Titles > 0 || Children > 0;
}

/// <summary>
/// The subject headings — what the library files its books under.
///
/// A tree, because subjects are one: "Military history" has "Regimental
/// histories" under it, and a book filed under the second is findable under the
/// first. Kept deliberately small — a name, a parent and a position — because a
/// unit library's scheme is a page of headings its librarian invented, not a
/// thesaurus.
///
/// Two things cannot be done, and both are about not breaking the tree: a
/// heading cannot end up beneath itself, and one that has books or headings
/// under it cannot be deleted out from under them.
/// </summary>
public class Subjects(MilLibDbContext db)
{
    /// <summary>
    /// The whole tree, flattened into the order it is drawn in: each heading
    /// followed by everything beneath it, each level sorted by its own position
    /// and then by name.
    /// </summary>
    public async Task<IReadOnlyList<SubjectNode>> TreeAsync()
    {
        var all = await db.Categories.AsNoTracking().ToListAsync();

        var titles = await db.TitleCategories
            .GroupBy(tc => tc.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        var byParent = all
            .GroupBy(c => c.ParentId)
            .ToDictionary(g => g.Key ?? 0, g => g
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());

        var flat = new List<SubjectNode>(all.Count);

        // Walked rather than recursed. A tree that has somehow acquired a loop
        // — from an older version, or from the other application — would
        // recurse until the process died; this visits each heading once and
        // simply leaves an unreachable one out of the list, where the count at
        // the foot of the screen will show it as missing.
        var seen = new HashSet<long>();

        void Walk(long parent, int depth)
        {
            if (!byParent.TryGetValue(parent, out var children))
            {
                return;
            }

            foreach (var heading in children)
            {
                if (!seen.Add(heading.CategoryId))
                {
                    continue;
                }

                var at = flat.Count;

                flat.Add(new SubjectNode(heading, depth,
                    titles.GetValueOrDefault(heading.CategoryId),
                    byParent.TryGetValue(heading.CategoryId, out var mine) ? mine.Count : 0,
                    0));

                Walk(heading.CategoryId, depth + 1);

                // Everything added since is beneath this heading. Counted here
                // rather than by a second pass, because the walk has just been
                // done and knows the answer.
                var below = flat.Skip(at + 1).Take(flat.Count - at - 1).Sum(n => n.Titles);

                flat[at] = flat[at] with { Below = below };
            }
        }

        Walk(0, 0);

        return flat;
    }

    /// <summary>How many headings exist, whether or not the walk could reach them.</summary>
    public async Task<int> CountAsync() => await db.Categories.CountAsync();

    /// <summary>
    /// Where a heading may be moved to: anything except itself and anything
    /// already beneath it.
    ///
    /// Offered as a list rather than checked after the fact, so the move that
    /// would break the tree is not on the menu at all.
    /// </summary>
    public async Task<IReadOnlyList<SubjectNode>> MayLiveUnderAsync(long headingId)
    {
        var tree = await TreeAsync();

        if (headingId == 0)
        {
            return tree;
        }

        var forbidden = new HashSet<long> { headingId };

        // The tree is in drawn order, so everything beneath a heading follows
        // it and is deeper than it. That is enough to find its descendants
        // without walking the parents again.
        var start = tree.ToList().FindIndex(n => n.Id == headingId);

        if (start >= 0)
        {
            for (var i = start + 1; i < tree.Count && tree[i].Depth > tree[start].Depth; i++)
            {
                forbidden.Add(tree[i].Id);
            }
        }

        return [.. tree.Where(n => !forbidden.Contains(n.Id))];
    }

    public async Task<IReadOnlyList<string>> ProblemsWithAsync(Category heading)
    {
        var problems = new List<string>();

        var name = heading.Name.Trim();

        if (name.Length == 0)
        {
            problems.Add("A heading needs a name.");
        }
        else
        {
            // Compared in memory: SQLite matches text case-sensitively and the
            // list is a page long, so "Signals" and "signals" would otherwise
            // sit beside each other as two headings.
            var siblings = await db.Categories.AsNoTracking()
                .Where(c => c.ParentId == heading.ParentId && c.CategoryId != heading.CategoryId)
                .ToListAsync();

            var clash = siblings.FirstOrDefault(c =>
                string.Equals(c.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase));

            if (clash is not null)
            {
                // The existing heading's spelling, not the one just typed. The
                // two differ only in case — that is why this fired — and the
                // useful thing to show is the one already there.
                problems.Add(heading.ParentId is null
                    ? $"There is already a top-level heading called {clash.Name}."
                    : $"There is already a {clash.Name} under that heading.");
            }
        }

        if (heading.ParentId == heading.CategoryId && heading.CategoryId != 0)
        {
            problems.Add("A heading cannot be filed under itself.");
        }
        else if (heading.ParentId is { } parent && heading.CategoryId != 0)
        {
            // The subtler half of the same rule. Putting a heading under one of
            // its own descendants makes a ring: neither is reachable from the
            // top afterwards, and both vanish off the screen.
            var allowed = await MayLiveUnderAsync(heading.CategoryId);

            if (allowed.All(n => n.Id != parent))
            {
                problems.Add("That heading is already filed under this one. "
                    + "Putting this one under it would leave both unreachable.");
            }
        }

        return problems;
    }

    public async Task<long> SaveAsync(Category heading, long byUserId)
    {
        var isNew = heading.CategoryId == 0;

        heading.Name = heading.Name.Trim();
        heading.UpdatedAt = DateTime.Now;

        if (isNew)
        {
            heading.CreatedAt = DateTime.Now;

            db.Categories.Add(heading);
        }
        else
        {
            db.Categories.Update(heading);
        }

        Journal.Note(db, byUserId, isNew ? "SUBJECT_ADDED" : "SUBJECT_UPDATED",
            "category", isNew ? null : heading.CategoryId,
            new { heading.Name, under = heading.ParentId });

        await db.SaveAndForgetAsync();

        return heading.CategoryId;
    }

    /// <summary>
    /// Why this heading cannot be removed, or null.
    ///
    /// Books filed under it, or headings beneath it. Neither is an error to be
    /// forced through: deleting the heading would either strand the books or
    /// orphan the headings, and both are quiet.
    /// </summary>
    public async Task<string?> WhyNotRemovableAsync(long headingId)
    {
        var titles = await db.TitleCategories.CountAsync(tc => tc.CategoryId == headingId);

        if (titles > 0)
        {
            return $"{titles} {(titles == 1 ? "book is" : "books are")} filed under this heading. "
                + "Move them to another one first — the books themselves are not affected either way.";
        }

        var children = await db.Categories.CountAsync(c => c.ParentId == headingId);

        if (children > 0)
        {
            return $"{children} {(children == 1 ? "heading sits" : "headings sit")} under this one. "
                + "Move or remove those first.";
        }

        return null;
    }

    public async Task RemoveAsync(Category heading, long byUserId)
    {
        db.Categories.Remove(heading);

        Journal.Note(db, byUserId, "SUBJECT_REMOVED", "category", heading.CategoryId,
            new { heading.Name });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// The books filed directly under one heading, for the panel beside the
    /// tree. Not those below it — this answers "what is under this heading",
    /// which is the question somebody clicking it is asking.
    /// </summary>
    public async Task<IReadOnlyList<(long TitleId, string Name)>> FiledUnderAsync(long headingId) =>
        [.. await db.TitleCategories
            .Where(tc => tc.CategoryId == headingId)
            .Join(db.Titles, tc => tc.TitleId, t => t.TitleId, (tc, t) => new { t.TitleId, t.Name })
            .OrderBy(t => t.Name)
            .Select(t => ValueTuple.Create(t.TitleId, t.Name))
            .ToListAsync()];
}
