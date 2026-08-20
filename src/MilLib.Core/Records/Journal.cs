using System.Text.Json;

namespace MilLib.Core.Data;

/// <summary>
/// Writing down what was done.
///
/// The same table the PHP application writes to, in the same shape, so the
/// Activity screen shows one history of the library rather than one history per
/// program that touched it. Nothing in the application edits or deletes a row
/// here: a record of what happened is only worth having if it cannot be tidied
/// up afterwards.
/// </summary>
public static class Journal
{
    /// <summary>
    /// Adds an entry. It is not saved — the caller saves it with whatever it
    /// was doing, so the entry and the thing it describes succeed or fail
    /// together. A loan that was issued with no note of who issued it, or a
    /// note of an issue that never happened, are both worse than neither.
    /// </summary>
    public static void Note(
        MilLibDbContext db,
        long? userId,
        string action,
        string entityType,
        long? entityId = null,
        object? details = null,
        SecurityClass? securityClass = null)
    {
        db.AuditLog.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            SecurityClass = securityClass,

            // The PHP application records where the request came from. This one
            // is the machine it is running on, always, so saying so plainly is
            // more honest than leaving the column empty.
            IpAddress = "127.0.0.1",

            Details = details is null ? null : JsonSerializer.Serialize(details),
            CreatedAt = DateTime.Now,
        });
    }

    /// <summary>Adds an entry and saves it on its own. For things with nothing else to save.</summary>
    public static async Task NoteAloneAsync(
        MilLibDbContext db,
        long? userId,
        string action,
        string entityType,
        long? entityId = null,
        object? details = null)
    {
        Note(db, userId, action, entityType, entityId, details);

        await db.SaveAndForgetAsync();
    }
}
