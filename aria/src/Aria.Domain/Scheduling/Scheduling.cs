namespace Aria.Domain.Scheduling;

/// <summary>
/// Aria only ever writes into slots it holds. External Google events are read-only by contract,
/// so the blast radius of a scheduling bug is bounded by design (wireframe S-06).
/// </summary>
public sealed class SlotHold
{
    public required string Id { get; init; }
    public required string DoctorId { get; init; }
    public required DateTimeOffset StartAt { get; init; }
    public required int DurationMinutes { get; init; }
    public string? HeldForPatientId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public SlotHoldStatus Status { get; set; } = SlotHoldStatus.Held;

    public bool IsLive(DateTimeOffset now) => Status is SlotHoldStatus.Held && ExpiresAt > now;
}

public sealed class Appointment
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string PatientId { get; init; }
    public required string DoctorId { get; init; }
    public required DateTimeOffset StartAt { get; init; }
    public int DurationMinutes { get; init; } = 20;
    public string? GoogleEventId { get; set; }
    public string Source { get; init; } = "aria";
    public string Status { get; set; } = "confirmed";
    public string? Reason { get; init; }
}

/// <summary>Calendar entry as Aria sees it. <c>IsExternal</c> entries can never be edited here.</summary>
public sealed record CalendarBlock(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string Title,
    bool IsExternal,
    bool IsBuffer = false,
    string? PatientId = null);

/// <summary>
/// A proposal with a reason, never a silent booking. Capped at three because more than three
/// options is decision fatigue, not helpfulness (wireframe S-06, J2).
/// </summary>
public sealed record SlotProposal(
    DateTimeOffset StartAt,
    int DurationMinutes,
    string Reason)
{
    public const int MaxProposals = 3;
}

public sealed class AvailabilityRule
{
    public required string DoctorId { get; init; }
    public TimeOnly DayStart { get; init; } = new(9, 0);
    public TimeOnly DayEnd { get; init; } = new(17, 0);
    public TimeOnly LunchStart { get; init; } = new(13, 0);
    public TimeOnly LunchEnd { get; init; } = new(14, 0);
    public int SlotMinutes { get; init; } = 20;
    /// <summary>Clinics run late. A scheduler that pretends otherwise is abandoned in week two.</summary>
    public int BufferEveryNSlots { get; init; } = 3;
    public HashSet<DayOfWeek> WorkingDays { get; init; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
}
