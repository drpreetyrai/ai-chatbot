using Aria.Infrastructure.Persistence;

namespace Aria.Infrastructure.Seed;

/// <summary>
/// A small, real-shaped guideline pack. Every clinical suggestion the product makes must cite a
/// section from here by id; the citation-enforcement middleware verifies the id resolves and
/// deletes the item if it does not. That is the rule that makes the evidence drawer defensible.
///
/// Text is paraphrased for the demo. In production these are ingested from signed publisher
/// packs, version-pinned per tenant, and old versions stay queryable forever.
/// </summary>
public static class GuidelinePack
{
    public const string Version = "guidelines-v1";

    public static IEnumerable<GuidelineDocument> Sections() =>
    [
        new() { Id = "bts-cap-2023-4.2", PackVersion = Version, Publisher = "BTS", Specialty = "respiratory",
            Title = "Community-acquired pneumonia — initial assessment", Section = "§4.2",
            Citation = "BTS CAP guideline 2023, §4.2", Url = "https://example.org/bts/cap-2023#4.2",
            Text = "Patients presenting with fever, cough and focal chest signs should have a chest radiograph " +
                   "(PA view) to confirm consolidation. Baseline investigations include full blood count and CRP. " +
                   "Assess severity with CURB-65 before deciding on admission." },

        new() { Id = "bts-cap-2023-6.1", PackVersion = Version, Publisher = "BTS", Specialty = "respiratory",
            Title = "Community-acquired pneumonia — antibiotic choice", Section = "§6.1",
            Citation = "BTS CAP guideline 2023, §6.1", Url = "https://example.org/bts/cap-2023#6.1",
            Text = "First-line therapy for low-severity community-acquired pneumonia is amoxicillin. " +
                   "In patients with documented penicillin allergy, a macrolide such as azithromycin or " +
                   "clarithromycin, or doxycycline, is an appropriate alternative." },

        new() { Id = "curb65-2023", PackVersion = Version, Publisher = "BTS", Specialty = "respiratory",
            Title = "CURB-65 severity score", Section = "Box 3",
            Citation = "BTS CAP guideline 2023, Box 3", Url = "https://example.org/bts/cap-2023#box3",
            Text = "One point each for Confusion, Urea > 7 mmol/L, Respiratory rate ≥ 30, Blood pressure " +
                   "(systolic < 90 or diastolic ≤ 60), age ≥ 65. Score 0–1 suggests suitability for home " +
                   "treatment; consider admission at 2 or more. Oxygen saturation below 92% on room air " +
                   "warrants assessment for admission irrespective of score." },

        new() { Id = "nice-ng191-covid", PackVersion = Version, Publisher = "NICE", Specialty = "respiratory",
            Title = "COVID-19 and viral lower respiratory tract infection", Section = "NG191",
            Citation = "NICE NG191", Url = "https://example.org/nice/ng191",
            Text = "Consider SARS-CoV-2 testing in patients presenting with new cough, fever or breathlessness, " +
                   "particularly where there is a known exposure. Viral lower respiratory tract infection may " +
                   "present indistinguishably from bacterial pneumonia in the early phase." },

        new() { Id = "gina-2024-4.3", PackVersion = Version, Publisher = "GINA", Specialty = "respiratory",
            Title = "Asthma exacerbation — recognition", Section = "Box 4-3",
            Citation = "GINA 2024, Box 4-3", Url = "https://example.org/gina/2024#box4-3",
            Text = "Asthma exacerbation typically presents with wheeze, chest tightness and increased " +
                   "reliever use. Absence of wheeze in a known asthmatic with respiratory distress is a " +
                   "concerning sign and does not exclude severe exacerbation." },

        new() { Id = "nice-cg127-htn", PackVersion = Version, Publisher = "NICE", Specialty = "cardiology",
            Title = "Hypertension — treatment and monitoring", Section = "CG127 §1.4",
            Citation = "NICE CG127, §1.4", Url = "https://example.org/nice/cg127#1.4",
            Text = "Patients on antihypertensive therapy should continue their usual morning medication before " +
                   "a routine clinic appointment unless specifically instructed otherwise, so that clinic " +
                   "readings reflect treated blood pressure." },

        new() { Id = "lab-fasting-2024", PackVersion = Version, Publisher = "Northbridge SOP", Specialty = "general",
            Title = "Pre-test fasting requirements", Section = "SOP-LAB-04",
            Citation = "Northbridge SOP-LAB-04 (2024)", Url = "https://example.org/northbridge/sop-lab-04",
            Text = "Full blood count and CRP require no fasting. Lipid panel requires a 9–12 hour fast. " +
                   "Fasting glucose requires an 8 hour fast. Patients should continue prescribed medication " +
                   "with water unless the requesting clinician states otherwise." },

        new() { Id = "anaphylaxis-red-flags", PackVersion = Version, Publisher = "Northbridge SOP", Specialty = "general",
            Title = "Symptoms requiring immediate clinical review", Section = "SOP-SAFE-01",
            Citation = "Northbridge SOP-SAFE-01", Url = "https://example.org/northbridge/sop-safe-01",
            Text = "Chest pain or tightness, acute breathlessness, facial or throat swelling, sudden weakness " +
                   "or slurred speech, heavy bleeding, and any expression of self-harm must be routed to a " +
                   "human clinician immediately. Automated systems must not attempt to triage these." },
    ];
}
