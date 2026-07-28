using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aria.Infrastructure.Persistence;

/// <summary>
/// Adds tables that appeared in the model after the local database was created.
///
/// <see cref="RelationalDatabaseFacadeExtensions"/>' EnsureCreated is all-or-nothing: it
/// creates the schema when the file is absent and does nothing at all when it is present.
/// So the first time a new entity ships, every developer — and every person trying the
/// app — hits "no such table", and the only advice is "delete your database", which also
/// deletes the accounts they just signed up with.
///
/// This is a development convenience and nothing more. Production runs EF migrations
/// against Postgres (plan.md §14), where dropping a column is a decision someone reviews,
/// not a side effect of a build. Accordingly this only ever CREATEs — it never alters or
/// drops anything, so the worst case is a table that exists but is out of date, which
/// surfaces loudly rather than silently losing data.
/// </summary>
public static class DevSchema
{
    public static async Task EnsureNewTablesAsync(AriaDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite()) return;

        var script = db.Database.GenerateCreateScript();
        var created = 0;

        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!statement.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
                created++;
            }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Expected for everything that was already there.
            }
        }

        if (created > 0)
            logger?.LogInformation("Local schema updated: {Count} new table(s) or index(es) created.", created);
    }
}
