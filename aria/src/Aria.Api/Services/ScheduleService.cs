using Aria.Domain;
using Aria.Domain.Scheduling;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Services;

/// <summary>
/// Scheduling, with the calendar of record left firmly in charge.
///
/// Two rules shape everything here:
///   • Aria proposes; it does not silently book. A proposal always carries a reason a patient
///     would understand, and there are never more than three of them.
///   • Aria only ever writes into slots it holds. External calendar entries are read-only, so the
///     blast radius of a scheduling bug is bounded by design rather than by care.
///
/// Buffers are first-class objects, not a rounding trick. Clinics run late; a scheduler that
/// pretends otherwise is abandoned in week two.
/// </summary>
public sealed class ScheduleService(
    AriaDbContext db,
    IAuditService audit,
    IAriaEventSink events,
    ILogger<ScheduleService> logger)
{
    private static readonly AvailabilityRule DefaultRules = new() { DoctorId = "*" };

    /// <summary>The merged day view: booked appointments, Aria-held slots, buffers and free time.</summary>
    public async Task<IReadOnlyList<CalendarBlock>> DayAsync(
        string doctorId, DateOnly date, CancellationToken ct = default)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var dayEnd = dayStart.AddDays(1);

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctorId && a.StartAt >= dayStart && a.StartAt < dayEnd && a.Status != "cancelled")
            .ToListAsync(ct);

        var holds = await db.SlotHolds.AsNoTracking()
            .Where(h => h.DoctorId == doctorId && h.StartAt >= dayStart && h.StartAt < dayEnd
                     && h.Status == SlotHoldStatus.Held)
            .ToListAsync(ct);

        // The doctor's real commitments, mirrored from Google by the sync worker. The API is
        // not allowed to call Google itself, so this projection is the only view it gets —
        // and proposals are generated from this same method, which is what stops Aria from
        // offering a slot the doctor is already elsewhere for.
        var external = await db.ExternalCalendarBlocks.AsNoTracking()
            .Where(x => x.DoctorId == doctorId && x.StartAt >= dayStart && x.StartAt < dayEnd)
            .ToListAsync(ct);

        var patientNames = await db.Patients.AsNoTracking()
            .Where(p => appointments.Select(a => a.PatientId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var blocks = new List<CalendarBlock>();

        blocks.AddRange(appointments.Select(a => new CalendarBlock(
            a.StartAt, a.StartAt.AddMinutes(a.DurationMinutes),
            patientNames.GetValueOrDefault(a.PatientId, "Patient"),
            IsExternal: false, IsBuffer: false, PatientId: a.PatientId)));

        blocks.AddRange(holds.Select(h => new CalendarBlock(
            h.StartAt, h.StartAt.AddMinutes(h.DurationMinutes), "held", IsExternal: false)));

        blocks.AddRange(external.Select(x => new CalendarBlock(
            x.StartAt, x.EndAt, x.Title, IsExternal: true)));

        blocks.Add(new CalendarBlock(
            dayStart.Add(DefaultRules.LunchStart.ToTimeSpan()),
            dayStart.Add(DefaultRules.LunchEnd.ToTimeSpan()),
            "Lunch", IsExternal: true));

        return [.. blocks.OrderBy(b => b.StartAt)];
    }

    /// <summary>
    /// At most three proposals, each with a plain-language reason (wireframe S-06).
    ///
    /// The cap is a product decision, not a performance one: more than three options is decision
    /// fatigue for a patient answering on WhatsApp, and it measurably lowers booking rates.
    /// </summary>
    public async Task<IReadOnlyList<SlotProposal>> ProposeAsync(
        ClinicianIdentity identity, string doctorId, string patientId, int withinDays,
        int durationMinutes = 20, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var proposals = new List<SlotProposal>();

        // Learn the patient's habitual time of day from their own history. This is what makes a
        // reason like "matches her past 10 AM preference" honest rather than decorative.
        var pastHours = await db.Appointments.AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Status != "cancelled")
            .Select(a => a.StartAt.Hour)
            .ToListAsync(ct);

        var preferredHour = pastHours.Count > 0
            ? pastHours.GroupBy(h => h).OrderByDescending(g => g.Count()).First().Key
            : (int?)null;

        for (var dayOffset = 1; dayOffset <= withinDays && proposals.Count < SlotProposal.MaxProposals; dayOffset++)
        {
            var date = DateOnly.FromDateTime(now.Date.AddDays(dayOffset));
            if (!DefaultRules.WorkingDays.Contains(date.DayOfWeek)) continue;

            var busy = await DayAsync(doctorId, date, ct);

            var candidates = EnumerateSlots(date, durationMinutes)
                .Where(slot => !busy.Any(b => b.StartAt < slot.AddMinutes(durationMinutes) && b.EndAt > slot))
                .ToList();

            if (candidates.Count == 0) continue;

            // Prefer the patient's usual hour; fall back to the first thing available.
            var chosen = preferredHour is { } hour
                ? candidates.FirstOrDefault(c => c.Hour == hour)
                : default;

            var reason = chosen != default
                ? $"Matches their usual {chosen.Hour:00}:00 appointment time"
                : "Earliest available slot with a buffer after it";

            if (chosen == default) chosen = candidates[0];

            proposals.Add(new SlotProposal(chosen, durationMinutes, reason));
        }

        events.Emit(AriaEvents.SlotOffered, new Dictionary<string, object?>
        {
            ["doctor_id"] = doctorId, ["patient_id"] = patientId, ["count"] = proposals.Count,
        });

        return proposals;
    }

    /// <summary>
    /// Reserves a slot for fifteen minutes. A hold is reversible and expires on its own, which is
    /// why it sits at Hold authority rather than Commit — it changes nothing a patient would see.
    /// </summary>
    public async Task<SlotHold> HoldAsync(
        ClinicianIdentity identity, string doctorId, string patientId,
        DateTimeOffset startAt, int durationMinutes, CancellationToken ct = default)
    {
        var clash = await db.SlotHolds
            .AnyAsync(h => h.DoctorId == doctorId && h.StartAt == startAt && h.Status == SlotHoldStatus.Held, ct);

        if (clash) throw new InvalidOperationException("That slot is already held.");

        var hold = new SlotHold
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            DoctorId = doctorId,
            StartAt = startAt,
            DurationMinutes = durationMinutes,
            HeldForPatientId = patientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        db.SlotHolds.Add(hold);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(identity.TenantId, identity.DoctorId, ActorKind.Clinician,
            AuditActions.SlotHeld, "slot", hold.Id, patientId,
            detail: new { startAt, durationMinutes }, ct: ct);

        logger.LogInformation("Held slot {HoldId} at {Start:g} for patient {PatientId}.", hold.Id, startAt, patientId);
        return hold;
    }

    public async Task ReleaseAsync(string holdId, CancellationToken ct = default)
    {
        var hold = await db.SlotHolds.FirstOrDefaultAsync(h => h.Id == holdId, ct);
        if (hold is null) return;

        hold.Status = SlotHoldStatus.Released;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Working slots for a day, respecting lunch and inserting a buffer every N slots.</summary>
    private static IEnumerable<DateTimeOffset> EnumerateSlots(DateOnly date, int durationMinutes)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(DefaultRules.DayStart), TimeSpan.FromHours(5.5));
        var dayEnd = new DateTimeOffset(date.ToDateTime(DefaultRules.DayEnd), TimeSpan.FromHours(5.5));
        var lunchStart = new DateTimeOffset(date.ToDateTime(DefaultRules.LunchStart), TimeSpan.FromHours(5.5));
        var lunchEnd = new DateTimeOffset(date.ToDateTime(DefaultRules.LunchEnd), TimeSpan.FromHours(5.5));

        var cursor = dayStart;
        var index = 0;

        while (cursor.AddMinutes(durationMinutes) <= dayEnd)
        {
            var overlapsLunch = cursor < lunchEnd && cursor.AddMinutes(durationMinutes) > lunchStart;
            var isBufferSlot = DefaultRules.BufferEveryNSlots > 0
                            && index > 0
                            && index % DefaultRules.BufferEveryNSlots == 0;

            if (!overlapsLunch && !isBufferSlot) yield return cursor;

            cursor = cursor.AddMinutes(DefaultRules.SlotMinutes);
            index++;
        }
    }
}
