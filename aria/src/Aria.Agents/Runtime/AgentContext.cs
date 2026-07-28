using Aria.Domain;

namespace Aria.Agents.Runtime;

/// <summary>
/// Everything an agent is allowed to know about who it is acting for.
///
/// The security-relevant identifiers live HERE and only here. They are bound into tool closures
/// at construction time, so no tool schema ever exposes a tenant, patient or doctor id for the
/// model to supply — or to alter (plan.md §4.1, rule 2).
/// </summary>
public sealed record AgentContext(
    ClinicianIdentity Identity,
    string FacilityId,
    string? PatientId = null,
    string? EncounterId = null,
    string? ThreadId = null,
    string GuidelinePackVersion = "guidelines-v1")
{
    public string TenantId => Identity.TenantId;
    public string DoctorId => Identity.DoctorId;
    public string Department => Identity.Department;

    /// <summary>
    /// Lifecycle stage governs which tool authorities are legal right now. A Commit tool invoked
    /// before signature is not a retryable error — it is a security event.
    /// </summary>
    public AgentLifecycle Lifecycle { get; init; } = AgentLifecycle.PreSignature;
}

public enum AgentLifecycle { PreSignature, PostSignature }

/// <summary>Stable agent identifiers. Used for tool registration, telemetry and model cards.</summary>
public static class AgentIds
{
    public const string Extraction      = "aria-extraction";
    public const string Scribe          = "aria-scribe";
    public const string ChartQa         = "aria-chart-qa";
    public const string ClinicalEvidence= "aria-clinical-evidence";
    public const string Scheduling      = "aria-scheduling";
    public const string PatientComms    = "aria-patient-comms";
    public const string Classifier      = "aria-classifier";
}
