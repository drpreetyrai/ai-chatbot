using System.Reflection;
using Aria.Domain;
using NetArchTest.Rules;
using Xunit;

namespace Aria.Tests;

/// <summary>
/// The two invariants from plan.md §1.2, as executable rules.
///
/// These are not style checks. Each one fails the build if the architectural property it protects
/// is broken, which is the only way a property like "signature is the only write barrier" survives
/// contact with a team and a deadline.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(ClinicianIdentity).Assembly;
    private static readonly Assembly Safety = typeof(Aria.Safety.RedFlagDetector).Assembly;
    private static readonly Assembly Agents = typeof(Aria.Agents.Runtime.AgentContext).Assembly;
    private static readonly Assembly Api = typeof(Aria.Api.Services.SignatureService).Assembly;
    private static readonly Assembly Workers = typeof(Aria.Workers.OutboxDispatcher).Assembly;
    private static readonly Assembly Integrations = typeof(Aria.Integrations.IEhrAdapter).Assembly;

    // ─────────────────────────────────────────────────────────────────────────
    //  INVARIANT 1 — Signature is the only write barrier.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Agents_cannot_reference_Integrations()
    {
        // If an agent could resolve IEhrAdapter, a prompt injection could in principle reach the
        // EHR. It cannot, because the type is not in its closure at all.
        var referenced = Agents.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(Integrations.GetName().Name, referenced);
    }

    [Fact]
    public void Api_cannot_reference_Integrations()
    {
        // The API surfaces the signature endpoint but must not be able to perform an external
        // write itself — it may only enqueue one.
        var referenced = Api.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(Integrations.GetName().Name, referenced);
    }

    [Fact]
    public void Only_Workers_references_Integrations()
    {
        var referenced = Workers.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains(Integrations.GetName().Name, referenced);
    }

    [Fact]
    public void No_agent_can_even_see_the_outbox()
    {
        // An agent that could reference an outbox item could, in principle, be talked into
        // enqueuing one. The strongest version of the rule is that the type is not reachable.
        var offenders = Types.InAssembly(Agents)
            .That().HaveDependencyOn("Aria.Domain.Governance.OutboxItem")
            .GetTypes()
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "No type in Aria.Agents may touch the outbox. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_the_signature_path_touches_the_outbox_in_the_api()
    {
        // SignatureService fills the outbox; AdminEndpoints lists it read-only so an operator can
        // prove the barrier holds. Any THIRD type is a second write path, which is the thing the
        // invariant exists to prevent — so the allow-list is deliberately short and explicit.
        var allowed = new[]
        {
            typeof(Aria.Api.Services.SignatureService).FullName,
            typeof(Aria.Api.Endpoints.AdminEndpoints).FullName,
        };

        var offenders = Types.InAssembly(Api)
            .That().HaveDependencyOn("Aria.Domain.Governance.OutboxItem")
            .GetTypes()
            .Select(t => t.FullName)
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Only SignatureService may write the outbox (AdminEndpoints reads it). Offenders: "
            + string.Join(", ", offenders));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  INVARIANT 2 — Escalation never depends on a probabilistic system.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Safety_cannot_reference_Agents()
    {
        // The whole point: no prompt, model, or agent bug can influence escalation, because the
        // escalation code cannot see any of them.
        var referenced = Safety.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(Agents.GetName().Name, referenced);
    }

    [Fact]
    public void Safety_cannot_reference_the_agent_framework()
    {
        var referenced = Safety.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Microsoft.Agents.AI", referenced);
        Assert.DoesNotContain("Microsoft.Agents.AI.Abstractions", referenced);
    }

    [Fact]
    public void Safety_cannot_reference_persistence()
    {
        // Escalation must not be able to block on a database round trip either. It decides from
        // the message text alone.
        var referenced = Safety.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Aria.Infrastructure", referenced);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", referenced);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Supporting structure
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Domain_depends_on_nothing_of_ours()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(referenced, name => name is not null && name.StartsWith("Aria.", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_tool_in_the_catalogue_declares_an_authority_and_at_least_one_role()
    {
        foreach (var (name, descriptor) in Aria.Agents.Tools.ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Purpose), $"Tool '{name}' has no stated purpose.");
            Assert.True(descriptor.AllowedRoles.Length > 0, $"Tool '{name}' permits no role, so it can never be called.");
            Assert.True(Enum.IsDefined(descriptor.Authority), $"Tool '{name}' has an undefined authority.");
        }
    }

    [Fact]
    public void Commit_tools_are_never_legal_before_signature()
    {
        var commitTools = Aria.Agents.Tools.ToolCatalog.All.Values
            .Where(t => t.Authority is ToolAuthority.Commit)
            .ToList();

        Assert.NotEmpty(commitTools);   // guards against the test passing because the list is empty

        foreach (var tool in commitTools)
            Assert.False(
                Aria.Agents.Tools.ToolCatalog.IsLegalAt(tool.Authority, Aria.Agents.Runtime.AgentLifecycle.PreSignature),
                $"Commit tool '{tool.Name}' must not be callable before signature.");
    }
}

public class ModelAccessTests
{
    /// <summary>
    /// Only these types may call <c>IModelRouter.GetChatClient</c>.
    ///
    /// The point of <c>GuardedAgentRunner</c> is that guardrails cannot be forgotten, and that
    /// only holds while it is the way to reach a model. Two exceptions exist and are justified
    /// in the runner's own documentation; this test makes adding a third a deliberate act with a
    /// reviewer attached, rather than something that happens quietly in a feature branch.
    /// </summary>
    [Fact]
    public void Only_named_types_may_reach_a_model_directly()
    {
        string[] permitted =
        [
            "GuardedAgentRunner.cs",        // the pipeline itself
            "ModelRedFlagClassifier.cs",    // one word back, no tools, failure counts as URGENT
            "AssistantService.cs",          // conversational; shields and emits explicitly
            "IModelRouter.cs",              // the interface that declares it
            "ModelRouter.cs",               // the router's own implementation
            "AgentServiceCollectionExtensions.cs",   // composition root
        ];

        var agentsRoot = Path.Combine(RepositoryRoot(), "src", "Aria.Agents");

        var offenders = Directory
            .EnumerateFiles(agentsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("GetChatClient", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(f => !permitted.Contains(f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These types reach a model without the guardrail pipeline: " + string.Join(", ", offenders) +
            ". Either run through GuardedAgentRunner, or add the type to the permitted list here " +
            "with a comment saying why it is safe.");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Aria.Agents")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
