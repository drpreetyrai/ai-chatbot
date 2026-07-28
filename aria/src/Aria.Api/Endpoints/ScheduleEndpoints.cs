using Aria.Api.Auth;
using Aria.Api.Services;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/schedule");

        group.MapGet("/day", async (
            DateOnly? date, HttpContext http, ScheduleService schedule, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var day = date ?? DateOnly.FromDateTime(DateTime.Today);
            var blocks = await schedule.DayAsync(me.DoctorId, day, ct);

            return Results.Ok(new
            {
                Date = day,
                Blocks = blocks.Select(b => new
                {
                    b.StartAt, b.EndAt, b.Title,
                    // External entries render read-only. Google Calendar is the source of truth,
                    // not a mirror — no dual-write, no drift (wireframe S-06).
                    b.IsExternal, b.IsBuffer, b.PatientId,
                }),
            });
        });

        group.MapPost("/proposals", async (
            ProposalRequest request, HttpContext http, ScheduleService schedule, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var proposals = await schedule.ProposeAsync(
                me, me.DoctorId, request.PatientId, request.WithinDays ?? 7,
                request.DurationMinutes ?? 20, ct);

            return Results.Ok(new
            {
                Proposals = proposals.Select(p => new { p.StartAt, p.DurationMinutes, p.Reason }),
                // Stated explicitly so the UI never has to hard-code the rule.
                MaxProposals = Domain.Scheduling.SlotProposal.MaxProposals,
                Note = "Proposals only. Nothing is booked until a clinician signs or an autonomy dial permits it.",
            });
        });

        group.MapPost("/holds", async (
            HoldRequest request, HttpContext http, ScheduleService schedule, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            try
            {
                var hold = await schedule.HoldAsync(
                    me, me.DoctorId, request.PatientId, request.StartAt, request.DurationMinutes, ct);

                return Results.Ok(new { hold.Id, hold.StartAt, hold.DurationMinutes, hold.ExpiresAt });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapDelete("/holds/{id}", async (string id, HttpContext http, ScheduleService schedule, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();
            await schedule.ReleaseAsync(id, ct);
            return Results.Ok(new { released = id });
        });

        group.MapGet("/appointments", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var appointments = await db.Appointments.AsNoTracking()
                .Where(a => a.DoctorId == me.DoctorId && a.Status != "cancelled")
                .OrderBy(a => a.StartAt)
                .ToListAsync(ct);

            var patients = await db.Patients.AsNoTracking()
                .Where(p => appointments.Select(a => a.PatientId).Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            return Results.Ok(appointments.Select(a => new
            {
                a.Id, a.StartAt, a.DurationMinutes, a.Reason, a.Status, a.GoogleEventId,
                PatientName = patients.GetValueOrDefault(a.PatientId)?.Name ?? "Unknown",
                a.PatientId,
            }));
        });
    }
}

public sealed record ProposalRequest(string PatientId, int? WithinDays, int? DurationMinutes);
public sealed record HoldRequest(string PatientId, DateTimeOffset StartAt, int DurationMinutes);
