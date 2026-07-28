using Aria.Agents.Models;
using Aria.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Safety;

/// <summary>
/// The optional classifier stage of <see cref="RedFlagDetector"/>.
///
/// Deliberately the narrowest thing that could work: no tools, no conversation, no free text —
/// one question, one word back. It exists only to WIDEN the deterministic keyword net, and the
/// detector treats a failure or timeout here as a positive, so a broken classifier can only ever
/// cause an extra escalation, never a missed one.
///
/// Note the direction of the dependency: Aria.Safety declares the interface; this implementation
/// lives here in Aria.Agents. Safety cannot see the model stack, which is Invariant 2 in code.
/// </summary>
public sealed class ModelRedFlagClassifier(
    IModelRouter router,
    ILogger<ModelRedFlagClassifier> logger) : IRedFlagClassifier
{
    private const string Instructions = """
        You classify a single patient message for clinical urgency. Answer with exactly one word.

        Answer URGENT if the message describes, or could reasonably describe, any of:
        chest pain or tightness; difficulty breathing; sudden weakness, numbness or slurred speech;
        severe or uncontrolled bleeding; loss of consciousness or seizure; severe abdominal pain;
        swelling of face, throat or tongue; pregnancy complications; thoughts of self-harm or
        suicide; overdose; or any wording that suggests the person believes they are in danger.

        Answer ROUTINE only if you are confident none of the above applies.

        When you are unsure, answer URGENT. A false alarm costs a clinician thirty seconds.
        A missed emergency is unacceptable. Bias every borderline case toward URGENT.

        Answer with one word: URGENT or ROUTINE.
        """;

    public async Task<bool> IsRedFlagAsync(string text, CancellationToken ct)
    {
        try
        {
            var client = router.GetChatClient(ModelTask.Classification);

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, Instructions),
                    new ChatMessage(ChatRole.User, text),
                ],
                // Deliberately generous, not the 4 tokens a one-word answer "needs".
                //
                // Reasoning models spend the output budget on reasoning before they
                // emit anything, so a tight cap returns an EMPTY string — which this
                // classifier is required to read as URGENT. The fail-safe is correct,
                // but a cap that triggers it on every routine message turns the
                // escalation banner into noise, and a banner the clinic ignores is
                // worse than no banner. Give it room to actually answer; a genuinely
                // unparseable reply is then a real anomaly and still escalates.
                new ChatOptions { Temperature = 0f, MaxOutputTokens = 512 },
                ct).ConfigureAwait(false);

            var answer = response.Text?.Trim().ToUpperInvariant() ?? string.Empty;

            // Anything that is not an unambiguous ROUTINE is treated as urgent. An unparseable
            // answer is uncertainty, and uncertainty escalates.
            var urgent = !answer.StartsWith("ROUTINE", StringComparison.Ordinal);

            if (urgent)
                logger.LogInformation("Red-flag classifier returned '{Answer}' — treating as urgent.", answer);

            return urgent;
        }
        catch (OperationCanceledException)
        {
            throw;   // the detector's own fail-safe handles this and counts it as positive
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Red-flag classifier failed.");
            throw;   // same: the detector escalates rather than assuming safety
        }
    }
}
