using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria.Evals;

/// <summary>
/// The evaluation harness (plan.md §9).
///
/// These are not ordinary tests. They are release gates: several of the metrics
/// below are specified at 100% or 0 with no override, which means a regression
/// here is not "a failing test to triage" — it is a product that must not ship.
///
/// They run as xUnit facts deliberately. An evaluation suite that lives in a
/// separate tool someone has to remember to run is an evaluation suite that
/// stops running. Wiring the gates into `dotnet test` makes them unavoidable.
/// </summary>
public static class Dataset
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Walks up from the test binary to find the repository root.
    ///
    /// Datasets live in `evals/datasets/` next to the source, not as embedded
    /// resources, so a clinician reviewer can open, read and edit them without a
    /// build — which is the whole point of a golden set.
    /// </summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "evals", "datasets")))
                dir = dir.Parent;

            return dir is null
                ? throw new DirectoryNotFoundException(
                    "Could not locate evals/datasets. The evaluation gates cannot run without their golden sets.")
                : Path.Combine(dir.FullName, "evals", "datasets");
        }
    }

    public static IReadOnlyList<T> Load<T>(string fileName)
    {
        var path = Path.Combine(Root, fileName);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Golden set '{fileName}' is missing. A gate cannot pass by having no cases to check.", path);

        var rows = File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<T>(line, Options)!)
            .ToList();

        if (rows.Count == 0)
            throw new InvalidDataException(
                $"Golden set '{fileName}' is empty. An empty dataset makes every gate vacuously true.");

        return rows;
    }
}

// ── Dataset row shapes ──────────────────────────────────────────────────────

public sealed record RedFlagCase(
    string Text,
    bool ExpectRedFlag,
    [property: JsonPropertyName("expectedTrigger")] string? ExpectedTrigger);

public sealed record AllergyCase(string AllergyCode, string ProposedDrug, bool ExpectConflict);

public sealed record InjectionCase(string Family, string Channel, string Payload, bool IsAttack);

/// <summary>
/// A scored evaluation result, in the shape the plan's §9.3 table describes:
/// a metric, a gate, and a verdict — plus the specific cases that failed, because
/// "recall dropped to 96%" is useless without knowing which four it missed.
/// </summary>
public sealed record EvalResult(string Metric, double Score, double Gate, IReadOnlyList<string> Failures)
{
    public bool Passed => Score >= Gate;

    public string Report()
    {
        var header = $"{Metric}: {Score:P1} (gate {Gate:P0}) — {(Passed ? "PASS" : "FAIL")}";
        if (Passed || Failures.Count == 0) return header;

        var shown = Failures.Take(12).Select(f => $"    · {f}");
        var more = Failures.Count > 12 ? $"\n    … and {Failures.Count - 12} more" : "";

        return $"{header}\n{string.Join("\n", shown)}{more}";
    }
}
