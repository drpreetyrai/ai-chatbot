using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Aria.Domain.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
//  Structured output contracts. Every agent returns one of these — never free
//  prose that a caller has to parse. Schema validation failure is a real code
//  path (retry once, then degrade), not an exception nobody catches.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Live extraction during the encounter. Chips, not prose — cheap to scan, cheap to dismiss.</summary>
public sealed class ExtractedEntities
{
    [JsonPropertyName("symptoms")]    public List<ClinicalEntity> Symptoms { get; set; } = [];
    [JsonPropertyName("vitals")]      public List<ClinicalEntity> Vitals { get; set; } = [];
    [JsonPropertyName("medications")] public List<ClinicalEntity> Medications { get; set; } = [];
    [JsonPropertyName("orders")]      public List<ClinicalEntity> Orders { get; set; } = [];

    public IEnumerable<ClinicalEntity> All => Symptoms.Concat(Vitals).Concat(Medications).Concat(Orders);
}

public sealed class ClinicalEntity
{
    [Description("Human-readable label shown on the chip, e.g. 'fever · 3 days'")]
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    [Description("Normalised code where one exists (RxNorm / SNOMED / LOINC), else empty")]
    [JsonPropertyName("code")] public string Code { get; set; } = "";

    [Description("Millisecond offset into the encounter transcript where this was said")]
    [JsonPropertyName("transcriptMs")] public long TranscriptMs { get; set; }

    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.9;

    /// <summary>True when this came from a deterministic extractor rather than the model.</summary>
    [JsonPropertyName("deterministic")] public bool Deterministic { get; set; }
}

/// <summary>
/// The note the scribe produces. Rejected wholesale by CitationEnforcement middleware if any
/// sentence lacks a transcript span — no source, not rendered.
/// </summary>
public sealed class DraftNoteResult
{
    [JsonPropertyName("subjective")] public List<DraftSpan> Subjective { get; set; } = [];
    [JsonPropertyName("objective")]  public List<DraftSpan> Objective { get; set; } = [];
    [JsonPropertyName("assessment")] public List<DraftSpan> Assessment { get; set; } = [];
    [JsonPropertyName("plan")]       public List<DraftSpan> Plan { get; set; } = [];
    [JsonPropertyName("codes")]      public List<DraftCode> Codes { get; set; } = [];

    public IEnumerable<DraftSpan> AllSpans => Subjective.Concat(Objective).Concat(Assessment).Concat(Plan);
}

public sealed class DraftSpan
{
    [Description("One sentence of clinical prose. Never more than one sentence per span.")]
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    [Description("Start of the transcript window this sentence came from, in milliseconds")]
    [JsonPropertyName("startMs")] public long StartMs { get; set; }

    [Description("End of the transcript window, in milliseconds")]
    [JsonPropertyName("endMs")] public long EndMs { get; set; }

    [Description("0.0 to 1.0. Be honest — a low score costs nothing, a wrong confident claim costs trust.")]
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.9;

    [Description("If confidence is below 0.65, say plainly why. Otherwise empty.")]
    [JsonPropertyName("flagReason")] public string FlagReason { get; set; } = "";
}

public sealed class DraftCode
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("system")] public string System { get; set; } = "ICD-10";
    [JsonPropertyName("display")] public string Display { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.8;
}

/// <summary>
/// Chart Q&A. Every claim carries at least one citation; uncited claims are dropped by middleware
/// before the user ever sees them (wireframe S-05: "No citation → the claim is not rendered").
/// </summary>
public sealed class CitedAnswer
{
    [JsonPropertyName("claims")] public List<Claim> Claims { get; set; } = [];

    [Description("Set true and leave claims empty when the record genuinely does not answer the question.")]
    [JsonPropertyName("insufficientEvidence")] public bool InsufficientEvidence { get; set; }
}

public sealed class Claim
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    [Description("Ids of the source documents supporting this claim. At least one, always.")]
    [JsonPropertyName("sourceIds")] public List<string> SourceIds { get; set; } = [];
}

/// <summary>
/// Ranked considerations for the clinician's judgement — never a diagnosis, never a single answer.
/// An item without a resolvable, versioned guideline citation is removed (wireframe S-08).
/// </summary>
public sealed class RankedConsiderations
{
    [JsonPropertyName("considerations")] public List<Consideration> Considerations { get; set; } = [];
    [JsonPropertyName("safetyChecks")]   public List<string> SafetyChecks { get; set; } = [];
}

public sealed class Consideration
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    [Description("Relative ordering strength 1-5. This is rank, not probability.")]
    [JsonPropertyName("strength")] public int Strength { get; set; } = 3;

    [JsonPropertyName("suggested")] public string Suggested { get; set; } = "";

    [Description("Guideline id returned by search_guidelines. Never invent one.")]
    [JsonPropertyName("citationId")] public string CitationId { get; set; } = "";
}

/// <summary>A patient-facing draft. Must resolve to an approved template; free text is rejected.</summary>
public sealed class DraftMessageResult
{
    [JsonPropertyName("templateId")] public string TemplateId { get; set; } = "";
    [JsonPropertyName("parameters")] public Dictionary<string, string> Parameters { get; set; } = [];
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.8;
    [Description("Which policy or record this draft rests on, shown to the approver")]
    [JsonPropertyName("basis")] public string Basis { get; set; } = "";
    [Description("Set true if this needs a human clinician rather than a template reply")]
    [JsonPropertyName("needsEscalation")] public bool NeedsEscalation { get; set; }
}

/// <summary>Scheduling proposals. Max three, each with a reason a patient would understand.</summary>
public sealed class SlotProposalResult
{
    [JsonPropertyName("proposals")] public List<ProposedSlot> Proposals { get; set; } = [];
}

public sealed class ProposedSlot
{
    [JsonPropertyName("startAtIso")] public string StartAtIso { get; set; } = "";
    [JsonPropertyName("durationMinutes")] public int DurationMinutes { get; set; } = 20;
    [Description("Plain language, e.g. 'matches her past 10 AM preference'")]
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>Intent classification for inbound patient messages. Deterministic-ish, cheap, no tools.</summary>
public sealed class MessageIntent
{
    [JsonPropertyName("intent")] public string Intent { get; set; } = "other";
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.5;
}
