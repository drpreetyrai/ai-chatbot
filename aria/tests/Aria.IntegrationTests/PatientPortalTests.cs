using System.Net;
using System.Net.Http.Json;

namespace Aria.IntegrationTests;

/// <summary>
/// The patient surface, and the boundary around it.
///
/// A patient is the only role that sees PHI they do not own by mistake rather than by
/// privilege — so the tests here are about one thing: a signed-in patient can reach their
/// own record through the portal, and cannot reach anyone else's through any route.
/// </summary>
[Collection(AriaCollection.Name)]
public sealed class PatientPortalTests(AriaTestHost host)
{
    [Fact]
    public async Task A_patient_sees_their_own_record_without_naming_it()
    {
        var patient = await host.AsPatientAsync();

        // No patient id anywhere in the path. The server resolves it from the account link,
        // which means there is no identifier for a client to tamper with.
        var me = await patient.GetJsonAsync("/v1/portal/me");

        Assert.Equal("John Abraham", me.GetProperty("name").GetString());
        Assert.Contains("Penicillin", me.GetProperty("allergies").ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_patient_cannot_read_another_patients_record()
    {
        var patient = await host.AsPatientAsync();

        var other = await patient.GetAsync("/v1/patients/pt-sarah");

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task A_patient_cannot_list_the_clinics_patients()
    {
        var patient = await host.AsPatientAsync();

        var list = await patient.GetAsync("/v1/patients");

        // Even without record contents, the list is a roster of who attends this clinic.
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task A_patient_cannot_sign_a_note()
    {
        var patient = await host.AsPatientAsync();

        var attempt = await patient.PostAsJsonAsync("/v1/notes/n-does-not-matter/sign", new { });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task The_assistant_answers_a_patient_from_their_own_record_only()
    {
        var patient = await host.AsPatientAsync();

        var reply = await patient.PostJsonAsync("/v1/assistant/chat",
            new { message = "When is my next appointment?" });

        Assert.False(string.IsNullOrWhiteSpace(reply.GetProperty("text").GetString()));

        // Whatever it says, it must not have reached for another patient's record to say it.
        var text = reply.GetProperty("text").GetString()!;
        Assert.DoesNotContain("Sarah", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vikram", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_lay_question_still_finds_the_visit_it_is_about()
    {
        // Sign a visit for John so there is something to ground on.
        var doctor = await host.AsClinicianAsync();
        await ClinicalJourneyTests.SignedNoteAsync(
            doctor, await ClinicalJourneyTests.NewEncounterAsync(doctor, "pt-john"));

        var patient = await host.AsPatientAsync();

        // Deliberately lay phrasing. It shares no vocabulary with the note it is asking
        // about — "chest infection, right lower lobe" — so a purely lexical retrieval
        // returns nothing and the assistant truthfully says it does not know, about a
        // record sitting right there. That is what the recency fallback fixes.
        var reply = await patient.PostJsonAsync("/v1/assistant/chat",
            new { message = "What did the doctor say was wrong with me?" });

        var sources = reply.GetProperty("sources").EnumerateArray().ToList();
        Assert.NotEmpty(sources);
        Assert.All(sources, s => Assert.Contains("note", s.GetProperty("citation").GetString()!));
    }

    [Fact]
    public async Task An_urgent_message_from_a_patient_is_escalated_not_answered()
    {
        var patient = await host.AsPatientAsync();

        var reply = await patient.PostJsonAsync("/v1/assistant/chat",
            new { message = "I have crushing chest pain and my left arm is numb" });

        // Escalation is decided before the model is consulted, so this holds whether the
        // model plane is live or the deterministic local one.
        Assert.True(reply.GetProperty("escalated").GetBoolean());
        Assert.Contains("108", reply.GetProperty("text").GetString()!);
    }
}
