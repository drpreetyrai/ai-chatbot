using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Agents.Runtime;
using Aria.Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Aria.Agents.Models;

/// <summary>
/// A deterministic, rule-based clinical model used when no Foundry endpoint is configured.
///
/// This exists so the entire product — guardrails, memory, tool authority, citation enforcement,
/// evaluation, audit — can be run, demonstrated and CI-tested by anyone with a clone of the repo
/// and no Azure subscription. It is NOT a language model and does not pretend to be: it is a
/// transparent set of clinical rules over the transcript, and the UI labels it as a stub wherever
/// its output is shown.
///
/// It is deliberately honest in two ways that matter:
///   • It emits genuinely low confidence on the passage the demo transcript marks as unclear,
///     so the low-confidence review path is exercised rather than bypassed.
///   • It never invents a citation id. Ask it for evidence it does not have and it returns none,
///     which is exactly what the citation guardrail expects to handle.
/// </summary>
public sealed partial class LocalDemoChatClient : IChatClient
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ChatClientMetadata Metadata { get; } = new("aria-local-stub", new Uri("inproc://aria/local-demo"), "aria-local-stub-1");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var all = messages.ToList();

        // Agent Framework delivers the agent's instructions via ChatOptions.Instructions rather
        // than as a system message, so both sources have to be considered — reading only the
        // messages leaves the stub with no idea which agent is asking.
        var system = string.Join("\n",
            new[] { options?.Instructions }
                .Concat(all.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        var user = string.Join("\n", all.Where(m => m.Role != ChatRole.System).Select(m => m.Text));

        var agentId = AgentMarker().Match(system) is { Success: true } m ? m.Groups[1].Value : "unknown";
        var payload = Respond(agentId, system, user);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, payload))
        {
            ModelId = Metadata.DefaultModelId,
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    // ─────────────────────────────────────────────────────────────────────────
    private static string Respond(string agentId, string system, string user)
    {
        // Retrieved sources reach the model two ways: inline in tool results as [SOURCE:id], and
        // fenced into the user turn as <untrusted_content id="...">. Scanning only one of them
        // makes the stub answer "insufficient evidence" while the evidence is sitting in context.
        var everything = system + "\n" + user;

        return agentId switch
        {
            AgentIds.Scribe           => JsonSerializer.Serialize(BuildNote(user), Json),
            AgentIds.Extraction       => JsonSerializer.Serialize(BuildEntities(user), Json),
            AgentIds.ChartQa          => JsonSerializer.Serialize(BuildChartAnswer(everything), Json),
            AgentIds.ClinicalEvidence => JsonSerializer.Serialize(BuildConsiderations(everything), Json),
            AgentIds.PatientComms     => JsonSerializer.Serialize(BuildMessageDraft(system, user), Json),
            AgentIds.Classifier       => JsonSerializer.Serialize(Classify(user), Json),
            _                         => "{}",
        };
    }

    /// <summary>Ids the model can legitimately cite this turn, from either delivery mechanism.</summary>
    private static List<string> VisibleSourceIds(string text) =>
    [
        .. SourceMarker().Matches(text).Select(m => m.Groups[1].Value)
            .Concat(FenceMarker().Matches(text).Select(m => m.Groups[1].Value))
            .Distinct(),
    ];

    /// <summary>Transcript lines arrive as "[startMs] Speaker Text". Parse back to timed utterances.</summary>
    private static List<(long Ms, string Speaker, string Text)> ParseTranscript(string text) =>
        [.. TranscriptLine().Matches(text).Select(m =>
            (long.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()))];

    private static DraftNoteResult BuildNote(string user)
    {
        var lines = ParseTranscript(user);
        var note = new DraftNoteResult();
        if (lines.Count == 0) return note;

        (long start, long end) Window(params string[] needles)
        {
            var hits = lines.Where(l => needles.Any(n => l.Text.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            return hits.Count == 0 ? (0, 0) : (hits.Min(h => h.Ms), hits.Max(h => h.Ms) + 4_000);
        }

        void Add(List<DraftSpan> section, string text, double confidence, string flag, params string[] needles)
        {
            var (s, e) = Window(needles);
            if (s == 0 && e == 0) return;                       // no transcript evidence, no span
            section.Add(new DraftSpan { Text = text, StartMs = s, EndMs = e, Confidence = confidence, FlagReason = flag });
        }

        // ── SUBJECTIVE ──
        Add(note.Subjective, "34-year-old male presenting with a 3-day history of fever and dry cough.",
            0.94, "", "fever for about three days", "dry cough");
        Add(note.Subjective, "Reports exertional breathlessness since yesterday, on climbing stairs.",
            0.92, "", "breathless climbing");
        Add(note.Subjective, "Denies chest pain. No recent travel.",
            0.95, "", "No pain. No travel");
        // The deliberately unclear passage — low confidence, forces explicit review.
        Add(note.Subjective, "Cough reported as predominantly dry, with a possible productive episode this morning.",
            0.61, "Overlapping speech and ambiguous phrasing in the source audio.", "productive of something");

        // ── OBJECTIVE ──
        Add(note.Objective, "Temp 38.4 °C · HR 96 · BP 122/78 · SpO2 94% on room air.",
            0.93, "", "thirty-eight point four", "ninety-four percent");
        Add(note.Objective, "Chest: scattered crackles at the right base.",
            0.91, "", "crackles at the right base");

        // ── ASSESSMENT ──
        Add(note.Assessment, "Community-acquired pneumonia, likely right lower lobe.",
            0.88, "", "chest infection", "right lower lobe");
        Add(note.Assessment, "Differential includes viral lower respiratory tract infection and early COVID-19.",
            0.72, "", "chest infection");

        // ── PLAN ──
        Add(note.Plan, "1. Chest X-ray, PA view — today.", 0.95, "", "chest X-ray today");
        Add(note.Plan, "2. Full blood count and CRP.", 0.95, "", "full blood count and CRP");
        Add(note.Plan, "3. Paracetamol 500 mg twice daily for 5 days.", 0.96, "", "paracetamol five hundred");
        Add(note.Plan, "4. Penicillin allergy documented — azithromycin 500 mg once daily for 3 days.",
            0.94, "", "azithromycin");
        Add(note.Plan, "5. Review in 3 days, sooner if breathlessness worsens.", 0.93, "", "three days");

        note.Codes.AddRange([
            new DraftCode { Code = "J18.9", System = "ICD-10", Display = "Pneumonia, unspecified organism", Confidence = 0.84 },
            new DraftCode { Code = "R50.9", System = "ICD-10", Display = "Fever, unspecified", Confidence = 0.79 },
        ]);

        return note;
    }

    private static ExtractedEntities BuildEntities(string user)
    {
        var lines = ParseTranscript(user);
        var e = new ExtractedEntities();

        void Try(List<ClinicalEntity> bucket, string label, string code, params string[] needles)
        {
            var hit = lines.FirstOrDefault(l => needles.Any(n => l.Text.Contains(n, StringComparison.OrdinalIgnoreCase)));
            if (hit.Text is null) return;
            bucket.Add(new ClinicalEntity { Label = label, Code = code, TranscriptMs = hit.Ms, Confidence = 0.93 });
        }

        Try(e.Symptoms,    "fever · 3 days",              "R50.9",  "fever for about three days");
        Try(e.Symptoms,    "dry cough",                   "R05",    "dry cough");
        Try(e.Symptoms,    "breathlessness on exertion",  "R06.02", "breathless climbing");
        Try(e.Vitals,      "Temp 38.4 °C",                "8310-5", "thirty-eight point four");
        Try(e.Vitals,      "SpO2 94%",                    "59408-5","ninety-four percent");
        Try(e.Medications, "Paracetamol 500 mg · BD · 5 d","161",    "paracetamol five hundred");
        Try(e.Medications, "Amoxicillin 500 mg",          "723",     "amoxicillin");
        Try(e.Medications, "Azithromycin 500 mg · OD · 3 d","18631", "azithromycin");
        Try(e.Orders,      "Chest X-ray PA",              "168731", "chest X-ray");
        Try(e.Orders,      "CBC",                         "58410-2","full blood count");

        return e;
    }

    private static CitedAnswer BuildChartAnswer(string text)
    {
        // The stub cites only ids it can actually see — never an invented one. If retrieval
        // returned nothing, "the record does not answer this" is the correct output.
        var available = VisibleSourceIds(text);
        if (available.Count == 0)
            return new CitedAnswer { InsufficientEvidence = true };

        return new CitedAnswer
        {
            Claims =
            [
                new Claim
                {
                    Text = "This patient's signed record contains prior entries relevant to the question; see the cited notes.",
                    SourceIds = [.. available.Take(2)],
                },
            ],
        };
    }

    private static RankedConsiderations BuildConsiderations(string text)
    {
        var available = VisibleSourceIds(text);
        var result = new RankedConsiderations();

        // Only propose what the retrieved evidence supports. No evidence, no considerations.
        if (available.Contains("bts-cap-2023-4.2"))
            result.Considerations.Add(new Consideration
            {
                Title = "Community-acquired pneumonia", Strength = 4,
                Suggested = "Chest X-ray PA, full blood count, CRP", CitationId = "bts-cap-2023-4.2",
            });

        if (available.Contains("nice-ng191-covid"))
            result.Considerations.Add(new Consideration
            {
                Title = "Early COVID-19 / viral lower respiratory tract infection", Strength = 3,
                Suggested = "SARS-CoV-2 test if exposure history", CitationId = "nice-ng191-covid",
            });

        if (available.Contains("gina-2024-4.3"))
            result.Considerations.Add(new Consideration
            {
                Title = "Asthma exacerbation", Strength = 2,
                Suggested = "Known asthmatic; note absence of documented wheeze", CitationId = "gina-2024-4.3",
            });

        if (available.Contains("curb65-2023"))
            result.SafetyChecks.Add("SpO2 94% with exertional dyspnoea — assess against CURB-65 admission threshold.");

        return result;
    }

    private static DraftMessageResult BuildMessageDraft(string system, string user)
    {
        var lower = user.ToLowerInvariant();

        if (lower.Contains("bp tablet") || lower.Contains("blood pressure") || lower.Contains("before coming"))
            return new DraftMessageResult
            {
                TemplateId = "clinical_qa_v2",
                Parameters = new()
                {
                    ["answer"] = "Yes — please take your regular morning tablets as usual, including your blood " +
                                 "pressure medicine. Bring the strip with you so Dr. Rao can review the dose.",
                    ["clinic_phone"] = "080-4000-4400",
                },
                Confidence = 0.88,
                Basis = "Active medication list · pre-visit policy · NICE CG127 §1.4",
            };

        if (lower.Contains("fast") || lower.Contains("eat before") || lower.Contains("blood test"))
            return new DraftMessageResult
            {
                TemplateId = "clinical_qa_v2",
                Parameters = new()
                {
                    ["answer"] = "A full blood count and CRP do not need fasting — eat and drink normally.",
                    ["clinic_phone"] = "080-4000-4400",
                },
                Confidence = 0.91,
                Basis = "Northbridge SOP-LAB-04 (2024)",
            };

        // Anything the stub is not confident about goes to a human. That is the correct default.
        return new DraftMessageResult { NeedsEscalation = true, Confidence = 0.2, Basis = "No approved template matches this question." };
    }

    private static MessageIntent Classify(string user)
    {
        var l = user.ToLowerInvariant();
        var intent =
            l.Contains("reschedule") || l.Contains("change my appointment") ? "reschedule_request" :
            l.Contains("book") || l.Contains("appointment")                 ? "booking_request" :
            l.Contains("fast") || l.Contains("before")                      ? "clinical_qa" :
            l.Contains("medicine") || l.Contains("tablet") || l.Contains("dose") ? "medication_question" :
            "other";
        return new MessageIntent { Intent = intent, Confidence = intent == "other" ? 0.4 : 0.82 };
    }

    [GeneratedRegex(@"AGENT-ID:\s*([\w\-]+)")]                private static partial Regex AgentMarker();
    [GeneratedRegex(@"^\[(\d+)\]\s*(\S+)\s+(.*)$", RegexOptions.Multiline)] private static partial Regex TranscriptLine();
    [GeneratedRegex(@"\[SOURCE:([^\]]+)\]")]                  private static partial Regex SourceMarker();
    [GeneratedRegex("<untrusted_content id=\"([^\"]+)\"")]     private static partial Regex FenceMarker();
}
