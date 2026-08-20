using Microsoft.EntityFrameworkCore;

namespace MilLib.Core.Data;

/// <summary>
/// The lending policy: the categories a member can belong to, and what each one
/// permits.
///
/// This is the most consequential screen in the application and the least
/// visited. Every loan period, borrowing limit, renewal allowance and fine rate
/// in the library is a number in one of these rows — nothing about lending is
/// written in code — so changing one here changes what happens at the counter
/// tomorrow morning.
/// </summary>
public class Policies(MilLibDbContext db)
{
    public async Task<IReadOnlyList<(MemberCategory Category, int Members)>> AllAsync()
    {
        var categories = await db.MemberCategories.OrderBy(c => c.Name).ToListAsync();

        var counts = await db.Members
            .GroupBy(m => m.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        return [.. categories.Select(c => (c, counts.GetValueOrDefault(c.CategoryId)))];
    }

    /// <summary>A sensible starting point for a new category, not a rule.</summary>
    public static MemberCategory Fresh() => new()
    {
        MaxBooks = 2,
        LoanDays = 14,
        MaxRenewals = 1,
        FinePerDay = 1.00m,
        GraceDays = 3,
        CanReserve = true,
        MaxClearance = SecurityClass.UNCLASSIFIED,
        IsActive = true,
    };

    public async Task<IReadOnlyList<string>> ProblemsWithAsync(MemberCategory category)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(category.Name))
        {
            problems.Add("A name is needed — it is what the counter sees.");
        }

        if (string.IsNullOrWhiteSpace(category.Code))
        {
            problems.Add("A short code is needed.");
        }
        else if (await db.MemberCategories.AnyAsync(c =>
            c.Code == category.Code && c.CategoryId != category.CategoryId))
        {
            problems.Add($"The code {category.Code} is already in use.");
        }

        // Zero books for zero days is a category that cannot borrow anything.
        // That may be deliberate, but it is worth being told rather than
        // discovering at the counter.
        if (category.MaxBooks <= 0)
        {
            problems.Add("Nobody in this category could borrow anything — set how many books they may hold.");
        }

        if (category.LoanDays <= 0)
        {
            problems.Add("A loan has to last at least a day.");
        }

        if (category.FinePerDay < 0)
        {
            problems.Add("A fine cannot be less than nothing.");
        }

        if (category.RequiresDeposit && (category.DepositAmount ?? 0) <= 0)
        {
            problems.Add("This category asks for a deposit but no amount is set.");
        }

        return problems;
    }

    public async Task SaveAsync(MemberCategory category, long byUserId)
    {
        var now = DateTime.Now;
        var isNew = category.CategoryId == 0;

        category.UpdatedAt = now;

        if (isNew)
        {
            category.CreatedAt = now;

            db.MemberCategories.Add(category);
        }
        else
        {
            db.MemberCategories.Update(category);
        }

        Journal.Note(db, byUserId, isNew ? "CATEGORY_CREATED" : "CATEGORY_UPDATED",
            "member_category", isNew ? null : category.CategoryId,
            new
            {
                category.Name,
                category.MaxBooks,
                category.LoanDays,
                category.MaxRenewals,
                fine_per_day = category.FinePerDay,
            });

        await db.SaveAndForgetAsync();
    }

    /// <summary>
    /// A category with members in it cannot go: every one of them would be left
    /// without any lending rules at all, which is a worse state than being in a
    /// category somebody wanted to tidy away. Move them first.
    /// </summary>
    public async Task<string?> WhyNotRemovableAsync(MemberCategory category)
    {
        var members = await db.Members.CountAsync(m => m.CategoryId == category.CategoryId);

        return members == 0
            ? null
            : members == 1
                ? "One member is still in this category. Move them to another one first."
                : $"{members} members are still in this category. Move them to another one first.";
    }

    public async Task RemoveAsync(MemberCategory category, long byUserId)
    {
        Journal.Note(db, byUserId, "CATEGORY_REMOVED", "member_category", category.CategoryId,
            new { category.Name, category.Code });

        db.MemberCategories.Remove(category);

        await db.SaveAndForgetAsync();
    }
}
