using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Aria.IntegrationTests;

/// <summary>
/// The hero loop, end to end, through real HTTP (wireframe J1).
///
/// This is the test that would catch a regression nobody notices until a clinic
/// does: the transcript persisting to the wrong table, the draft losing its
/// provenance, the signature failing to release the follow-up.
/// </summary>
[Collection(AriaCollection.Name)]
public class ClinicalJourneyTests(AriaTestHost host)
{
    [Fact]
    public async Task Full_encounter_to_signature_journey()
    {
        var doctor = await host.AsClinicianAsync();

        // ── Who am I? Identity is the tenancy boundary. ──
        var me = await doctor.GetJsonAsync("/v1/auth/me");
        Assert.Equal("DR-1042", me.GetProperty("doctorId").GetString());
        Assert.Equal("Cardiology", me.GetProperty("department").GetString());
        Assert.True(me.GetProperty("permissions").GetProperty("canSign").GetBoolean());

        // ── Today's queue, with PHI masked by default. ──
        var queue = await doctor.GetJsonAsync("/v1/encounters/today");
        var john = queue.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "enc-john");
        var phone = john.GetProperty("patient").GetProperty("phone").GetString()!;

        Assert.Contains("•", phone);   // masked unless explicitly unmasked, which is audited

        var encounterId = await NewEncounterAsync(doctor, "pt-john");

        // ── Consent gates capture. This must fail. ──
        var premature = await doctor.PostAsync($"/v1/encounters/{encounterId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Contains("consent", (await premature.JsonAsync()).GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // ── With consent, capture starts. ──
        await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/consent", new { granted = true });
        var started = await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/start");
        Assert.Equal("Recording", started.GetProperty("state").GetString());

        // ── The transcript streams and persists. ──
        var segments = await doctor.StreamAsync($"/v1/encounters/{encounterId}/transcript/stream");
        Assert.True(segments.Count >= 15, $"Expected the full consultation, got {segments.Count} segments.");

        var persisted = await doctor.GetJsonAsync($"/v1/encounters/{encounterId}/transcript");
        Assert.Equal(segments.Count, persisted.GetArrayLength());

        // ── The draft. Every sentence must carry provenance. ──
        await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/end");
        var draft = await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/draft");
        var noteId = draft.GetProperty("noteId").GetString()!;

        var note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        var spans = note.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("spans").EnumerateArray())
            .ToList();

        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.True(
            s.GetProperty("hasProvenance").GetBoolean(),
            $"Span without provenance survived to render: \"{s.GetProperty("text").GetString()}\""));

        // The model and prompt that produced it are recorded, not inferred later.
        Assert.False(string.IsNullOrWhiteSpace(note.GetProperty("modelVersion").GetString()));
        Assert.Contains("@", note.GetProperty("promptVersion").GetString()!);   // sha-pinned

        // ── Low confidence blocks signing until a human decides. ──
        Assert.False(note.GetProperty("signable").GetBoolean());
        Assert.Contains("low-confidence", note.GetProperty("blocker").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // Every low-confidence span, not "the" one: a span's confidence is capped by what
        // the recogniser heard, so one badly-heard passage can legitimately flag more than
        // one sentence. The gate is "a human decided about each", not "there is exactly one".
        var flagged = spans.Where(s => s.GetProperty("band").GetString() == "Low").ToList();
        Assert.NotEmpty(flagged);

        foreach (var span in flagged)
            await doctor.PostJsonAsync($"/v1/notes/{noteId}/spans/{span.GetProperty("id").GetString()}/accept");

        note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        Assert.True(note.GetProperty("signable").GetBoolean());

        // ── THE WRITE BARRIER. Nothing queued for THIS note before signature. ──
        // Scoped to the note rather than the whole outbox: sibling tests sign their own
        // notes concurrently, and a global count would make this assertion flaky for a
        // reason that has nothing to do with the property being tested.
        var before = (await doctor.GetJsonAsync("/v1/admin/outbox"))
            .EnumerateArray().Count(o => o.GetProperty("noteId").GetString() == noteId);
        Assert.Equal(0, before);

        var signed = await doctor.PostJsonAsync($"/v1/notes/{noteId}/sign");
        var queued = signed.GetProperty("queuedActions").EnumerateArray()
            .Select(a => a.GetString()).ToList();

        Assert.Contains("EhrDocumentWrite", queued);
        Assert.Contains("CalendarBooking", queued);
        Assert.Contains("PatientMessage", queued);

        // ── Every queued item names the note that released it. ──
        var after = (await doctor.GetJsonAsync("/v1/admin/outbox"))
            .EnumerateArray()
            .Where(o => o.GetProperty("noteId").GetString() == noteId)
            .ToList();

        Assert.Equal(queued.Count, after.Count);

        // Idempotency keys are unique per action, so a retry cannot double-send.
        var keys = after.Select(o => o.GetProperty("idempotencyKey").GetString()).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task The_allergy_conflict_fires_during_the_consultation()
    {
        // The safety property that justifies ambient capture at all: the conflict is
        // caught while the patient is still in the room, not after the note is written.
        var doctor = await host.AsClinicianAsync();

        var encounterId = await NewEncounterAsync(doctor, "pt-john");
        await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/consent", new { granted = true });
        await doctor.PostAsync($"/v1/encounters/{encounterId}/start", null);
        await doctor.StreamAsync($"/v1/encounters/{encounterId}/transcript/stream");

        // 75s in — just after the clinician says "amoxicillin" out loud, and long
        // before any note exists.
        var live = await doctor.GetJsonAsync($"/v1/encounters/{encounterId}/entities?uptoMs=75000");
        var conflicts = live.GetProperty("conflicts").EnumerateArray().ToList();

        var conflict = Assert.Single(conflicts);
        Assert.Contains("moxicillin", conflict.GetProperty("drugLabel").GetString()!);
        Assert.Contains("enicillin", conflict.GetProperty("allergyLabel").GetString()!);
        Assert.Equal("Severe", conflict.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Signing_is_idempotent()
    {
        // A double-tap on a flaky clinic connection must not sign twice, and must
        // not enqueue a second set of external writes.
        var doctor = await host.AsClinicianAsync();
        var noteId = await SignedNoteAsync(doctor, await NewEncounterAsync(doctor, "pt-sarah"));

        var outboxAfterFirst = (await doctor.GetJsonAsync("/v1/admin/outbox"))
            .EnumerateArray().Count(o => o.GetProperty("noteId").GetString() == noteId);

        var second = await doctor.PostAsync($"/v1/notes/{noteId}/sign", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var outboxAfterSecond = (await doctor.GetJsonAsync("/v1/admin/outbox"))
            .EnumerateArray().Count(o => o.GetProperty("noteId").GetString() == noteId);

        Assert.Equal(outboxAfterFirst, outboxAfterSecond);
    }

    [Fact]
    public async Task A_signed_note_is_immutable_and_takes_addenda_instead()
    {
        var doctor = await host.AsClinicianAsync();
        var noteId = await SignedNoteAsync(doctor, await NewEncounterAsync(doctor, "pt-ali"));

        var note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        var spanId = note.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("spans").EnumerateArray())
            .First().GetProperty("id").GetString();

        // Editing a signed note is refused outright.
        var edit = await doctor.PatchAsJsonAsync($"/v1/notes/{noteId}/spans/{spanId}", new { text = "tampered" });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains("addendum", (await edit.JsonAsync()).GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // Corrections go in as addenda, with their own audit trail.
        var addendum = await doctor.PostJsonAsync($"/v1/notes/{noteId}/addenda",
            new { body = "Correction: CRP result pending at time of signing." });
        Assert.False(string.IsNullOrWhiteSpace(addendum.GetProperty("id").GetString()));

        var reread = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        Assert.Equal(1, reread.GetProperty("addenda").GetArrayLength());
    }

    [Fact]
    public async Task Rejecting_a_span_removes_the_claim_entirely()
    {
        // A rejected AI claim must not survive in the record in any form.
        var doctor = await host.AsClinicianAsync();
        var noteId = await DraftAsync(doctor, await NewEncounterAsync(doctor, "pt-john"));

        var note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        var before = CountSpans(note);

        var target = note.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("spans").EnumerateArray())
            .First(s => s.GetProperty("band").GetString() == "Low");

        await doctor.PostJsonAsync($"/v1/notes/{noteId}/spans/{target.GetProperty("id").GetString()}/reject");

        var after = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        Assert.Equal(before - 1, CountSpans(after));

        var texts = after.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("spans").EnumerateArray())
            .Select(s => s.GetProperty("text").GetString());

        Assert.DoesNotContain(target.GetProperty("text").GetString(), texts);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh encounter for the given patient.
    ///
    /// Tests must not share the seeded encounters: an encounter is a state machine, so the first
    /// test to run would consume it and every later test would see "Illegal transition". Creating
    /// one per test makes the suite order-independent and safe to parallelise.
    /// </summary>
    [Fact]
    public async Task A_span_is_never_more_confident_than_the_audio_it_came_from()
    {
        var doctor = await host.AsClinicianAsync();
        var encounterId = await NewEncounterAsync(doctor, "pt-vikram");
        var noteId = await DraftAsync(doctor, encounterId);

        var note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");
        var segments = await doctor.GetJsonAsync($"/v1/encounters/{encounterId}/transcript");

        var heard = segments.EnumerateArray()
            .Select(s => (Start: s.GetProperty("startMs").GetInt64(),
                          End: s.GetProperty("endMs").GetInt64(),
                          Confidence: s.GetProperty("confidence").GetDouble()))
            .ToList();

        var spans = note.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("spans").EnumerateArray())
            .ToList();

        Assert.NotEmpty(spans);

        foreach (var span in spans)
        {
            var start = span.GetProperty("transcriptStartMs").GetInt64();
            var end = span.GetProperty("transcriptEndMs").GetInt64();

            var overlapping = heard.Where(h => h.Start < end && h.End > start).ToList();
            if (overlapping.Count == 0) continue;

            // The rule: a model cannot be more certain of a sentence than the recogniser
            // was of the words. Without this cap a hosted model self-reports 0.9+ on
            // everything, and the review gate stops engaging exactly when the audio is bad.
            Assert.True(span.GetProperty("confidence").GetDouble() <= overlapping.Min(h => h.Confidence) + 0.0001,
                $"Span \"{span.GetProperty("text").GetString()}\" claims more confidence than its audio.");
        }
    }

    public static async Task<string> NewEncounterAsync(HttpClient doctor, string patientId)
    {
        var created = await doctor.PostJsonAsync("/v1/encounters",
            new { patientId, chiefComplaint = "integration test", room = "Room 1" });

        return created.GetProperty("id").GetString()!;
    }

    public static async Task<string> DraftAsync(HttpClient doctor, string encounterId)
    {
        await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/consent", new { granted = true });
        await doctor.PostAsync($"/v1/encounters/{encounterId}/start", null);
        await doctor.StreamAsync($"/v1/encounters/{encounterId}/transcript/stream");
        await doctor.PostAsync($"/v1/encounters/{encounterId}/end", null);

        var draft = await doctor.PostJsonAsync($"/v1/encounters/{encounterId}/draft");
        return draft.GetProperty("noteId").GetString()!;
    }

    public static async Task<string> SignedNoteAsync(HttpClient doctor, string encounterId)
    {
        var noteId = await DraftAsync(doctor, encounterId);
        var note = await doctor.GetJsonAsync($"/v1/notes/{noteId}");

        foreach (var span in note.GetProperty("sections").EnumerateArray()
                     .SelectMany(s => s.GetProperty("spans").EnumerateArray())
                     .Where(s => s.GetProperty("band").GetString() == "Low"))
        {
            await doctor.PostAsync($"/v1/notes/{noteId}/spans/{span.GetProperty("id").GetString()}/accept", null);
        }

        var response = await doctor.PostAsync($"/v1/notes/{noteId}/sign", null);
        response.EnsureSuccessStatusCode();
        return noteId;
    }

    private static int CountSpans(JsonElement note) =>
        note.GetProperty("sections").EnumerateArray().Sum(s => s.GetProperty("spans").GetArrayLength());
}
