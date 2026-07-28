using Aria.Infrastructure.Persistence;
using Aria.Integrations;
using Aria.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Aria.Workers;

/// <summary>
/// Pulls each connected clinician's real calendar into the local projection the
/// scheduler reads.
///
/// This exists because of an architectural rule worth restating: the API never calls an
/// external system. Everything outbound goes through a worker. So the Schedule screen
/// cannot ask Google what the doctor is doing at 3pm — a worker has to have asked
/// already, and left the answer somewhere the API can read.
///
/// The failure mode drove the design. A scheduler that cannot see a doctor's real
/// commitments must not conclude they are free; it must say it does not know. Hence a
/// full-window replace on every pass (so cancellations in Google disappear here too) and
/// a <c>SyncedAt</c> the UI can age out — never a partial merge that leaves a stale
/// block looking current.
/// </summary>
internal sealed class CalendarSyncWorker(
    IServiceScopeFactory scopes,
    ICalendarAdapter calendar,
    AriaOptions options,
    ILogger<CalendarSyncWorker> logger) : BackgroundService
{
    /// <summary>How far ahead we mirror. Proposals never look further than a week out.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(8);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Google.IsConfigured)
        {
            logger.LogInformation("Calendar sync idle: Google is not configured, the local calendar is in use.");
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One clinician's revoked token must not stop the loop for everyone else.
                logger.LogError(ex, "Calendar sync pass failed; will retry in {Minutes} minutes.", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AriaDbContext>();

        var connections = await db.CalendarConnections.AsNoTracking().ToListAsync(ct);
        if (connections.Count == 0) return;

        var from = DateTimeOffset.Now.Date;
        var to = from + Horizon;

        foreach (var connection in connections)
        {
            try
            {
                var blocks = await calendar.GetBusyAsync(connection.CalendarId, from, to, ct);

                // Replace the window wholesale, inside one transaction. An incremental merge
                // would leave events the clinician deleted in Google sitting in our scheduler
                // forever; a delete-then-insert without the transaction would leave a moment
                // where the doctor's day reads as completely free.
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var stale = await db.ExternalCalendarBlocks
                    .Where(x => x.DoctorId == connection.DoctorId && x.StartAt >= from && x.StartAt < to)
                    .ToListAsync(ct);

                db.ExternalCalendarBlocks.RemoveRange(stale);
                await db.SaveChangesAsync(ct);

                var ordinal = 0;
                foreach (var block in blocks)
                {
                    // Events Aria itself created are already appointments in our own tables;
                    // mirroring them back would double-book the doctor against themselves.
                    if (!block.IsExternal) continue;

                    db.ExternalCalendarBlocks.Add(new ExternalCalendarBlock
                    {
                        // Ordinal, not a content hash: the window is rewritten every pass, so
                        // the id only has to be unique within it — and two identically-named
                        // events at the same time are legal in Google.
                        Id = $"{connection.DoctorId}:{ordinal++}",
                        DoctorId = connection.DoctorId,
                        TenantId = connection.TenantId,
                        StartAt = block.StartAt,
                        EndAt = block.EndAt,
                        Title = block.Title,
                    });
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                logger.LogInformation("Synced {Count} external blocks for {DoctorId} from {CalendarId}.",
                    blocks.Count, connection.DoctorId, connection.CalendarId);
            }
            catch (CalendarUnavailableException ex)
            {
                // Leave the previous window in place and say so. Stale-but-labelled beats
                // an empty day that reads as "completely free".
                logger.LogWarning(ex, "Calendar unavailable for {DoctorId}; keeping the last known blocks.",
                    connection.DoctorId);
            }
        }
    }
}
