using Aria.Domain.Encounters;

namespace Aria.Infrastructure.Seed;

/// <summary>
/// The scripted consultation behind Demo Mode (plan.md §14.1).
///
/// This is not a mock — it is played through the real pipeline: real diarisation shape, real
/// entity extraction, real allergy checker, real scribe agent, real Note Review screen. Only the
/// audio source is substituted. That makes it simultaneously the clinician onboarding, the sales
/// demo, and the end-to-end smoke test that runs in every environment.
///
/// The content deliberately reproduces wireframe S-03/S-04 exactly, including the penicillin
/// conflict that must fire *during* the conversation rather than after it.
/// </summary>
public static class DemoEncounterScript
{
    public sealed record Line(string Speaker, string Text, long StartMs, long EndMs, double Confidence = 0.97);

    /// <summary>Roughly six minutes of consultation, compressed to about 75 seconds of playback.</summary>
    public static IReadOnlyList<Line> Lines =>
    [
        new("Dr.", "Good morning John, come in. Tell me what's been happening.", 0, 4_200),
        new("Pt.", "Morning doctor. I've had a fever for about three days now, and a dry cough.", 4_500, 10_800),
        new("Pt.", "Since yesterday I get breathless climbing the stairs to my flat.", 11_000, 15_600),
        new("Dr.", "Any chest pain with that? Any travel recently?", 16_000, 19_400),
        new("Pt.", "No pain. No travel.", 19_800, 21_900),
        new("Dr.", "Has anything like this happened before?", 22_200, 24_600),
        // Deliberately low ASR confidence — this becomes the low-confidence span in Note Review
        // and is what the provenance panel replays.
        new("Pt.", "The cough is dry mostly, though this morning it was a little productive of something.",
            25_000, 31_500, 0.61),
        new("Dr.", "Let me listen to your chest. Take a deep breath for me.", 32_000, 35_800),
        new("Dr.", "There are some crackles at the right base. Your temperature is thirty-eight point four.",
            36_200, 42_500),
        new("Dr.", "Oxygen saturation is ninety-four percent on room air, heart rate ninety-six, blood pressure one twenty-two over seventy-eight.",
            42_800, 50_400),
        new("Dr.", "I think this is a chest infection, most likely in the right lower lobe.", 50_800, 55_600),
        new("Dr.", "I want a chest X-ray today, PA view, and bloods — full blood count and CRP.", 56_000, 62_200),
        new("Dr.", "Let's start paracetamol five hundred milligrams, twice a day, for five days, for the fever.",
            62_600, 69_000),
        // The moment the whole safety story hangs on: the doctor reaches for amoxicillin out loud.
        new("Dr.", "And for the infection I'll start you on amoxicillin five hundred—", 69_400, 74_200),
        new("Pt.", "Doctor, I think I'm allergic to penicillin. It's on my file.", 74_500, 79_000),
        new("Dr.", "You're quite right, thank you. We'll use azithromycin instead — five hundred milligrams once daily for three days.",
            79_400, 87_200),
        new("Dr.", "Come back and see me in three days, sooner if the breathing gets worse.", 87_600, 92_800),
        new("Pt.", "Thank you doctor.", 93_000, 94_500),
    ];

    public static IEnumerable<TranscriptSegment> ToSegments(string encounterId) =>
        Lines.Select((l, i) => new TranscriptSegment
        {
            Id = $"{encounterId}-seg-{i:D3}",
            EncounterId = encounterId,
            Speaker = l.Speaker,
            Text = l.Text,
            StartMs = l.StartMs,
            EndMs = l.EndMs,
            Confidence = l.Confidence,
            IsFinal = true,
        });

    public static string FullText() => string.Join("\n", Lines.Select(l => $"[{l.StartMs}] {l.Speaker} {l.Text}"));

    /// <summary>Playback speed multiplier so a demo does not take a real consultation's worth of time.</summary>
    public const double PlaybackSpeed = 1.25;
}
