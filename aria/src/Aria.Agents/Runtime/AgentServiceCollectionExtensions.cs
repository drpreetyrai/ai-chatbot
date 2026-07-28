using System.ClientModel;
using Aria.Agents.Agents;
using Aria.Agents.Memory;
using Aria.Agents.Middleware;
using Aria.Agents.Models;
using Aria.Agents.Prompts;
using Aria.Agents.Safety;
using Aria.Agents.Tools;
using Aria.Infrastructure.Retrieval;
using Aria.Safety;
using Aria.Shared.Configuration;
using OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Runtime;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Wires the agent stack.
    ///
    /// Every live-service registration is paired with a working local fallback, so the product
    /// runs with an empty .env and each Azure service can be switched on independently. That is
    /// not a convenience — it is what makes Demo Mode, the CI smoke test, and the chaos test
    /// ("can the clinic finish the day with every model down?") the same code path.
    /// </summary>
    public static IServiceCollection AddAriaAgents(this IServiceCollection services, AriaOptions options)
    {
        // ── Model plane. Registered only when a real endpoint exists, so GetService<IChatClient>()
        //    returning null is the unambiguous signal that we are on the local stub. ──
        if (options.Foundry.IsConfigured)
        {
            services.AddSingleton<IChatClient>(sp =>
            {
                // ── OpenAI directly, when a key is supplied. ──
                if (options.Foundry.UsesOpenAi)
                {
                    return new OpenAIClient(new ApiKeyCredential(options.Foundry.OpenAiApiKey!))
                        .GetChatClient(options.Foundry.ReasoningDeployment)
                        .AsIChatClient()
                        .AsBuilder()
                        .UseFunctionInvocation()
                        .Build();
                }

                // Foundry's OpenAI-COMPATIBLE route, not the classic Azure OpenAI one.
                //
                // AzureOpenAIClient pins an api-version, and a services.ai.azure.com
                // Foundry endpoint rejects the SDK's default with a bare
                // "API version not supported" — which surfaces as every agent
                // degrading for no visible reason. The /openai/v1 route is
                // version-less and is what Foundry documents going forward, so this
                // cannot drift out of support again.
                var baseUrl = options.Foundry.ProjectEndpoint!.TrimEnd('/');
                if (!baseUrl.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
                    baseUrl += "/openai/v1";

                var client = new OpenAIClient(
                    new ApiKeyCredential(options.Foundry.ApiKey ?? string.Empty),
                    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

                return client
                    .GetChatClient(options.Foundry.ReasoningDeployment)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()  // the agent must be able to actually call its tools
                    .Build();
            });
        }

        services.AddSingleton<IModelRouter>(sp => new ModelRouter(
            options,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<IChatClient>()));

        // ── Prompt shield: real service when configured, honest heuristic otherwise ──
        services.AddSingleton<LocalHeuristicShield>();
        services.AddHttpClient<AzureContentSafetyShield>();

        services.AddSingleton<IPromptShield>(sp => options.ContentSafety.IsConfigured
            ? sp.GetRequiredService<AzureContentSafetyShield>()
            : sp.GetRequiredService<LocalHeuristicShield>());

        // ── Guardrails ──
        services.AddSingleton<IFeatureSwitches, InMemoryFeatureSwitches>();
        services.AddSingleton<OutputGuards>();
        services.AddSingleton<ToolAuthorizationMiddleware>();

        // ── Deterministic safety. Note: registered here, but the types live in Aria.Safety,
        //    which cannot reference this assembly. Invariant 2 holds at the project level.
        services.AddSingleton<KeywordNet>();
        services.AddSingleton<AllergyConflictChecker>();

        // The classifier is a WIDENER over the keyword net, and only a real model can widen
        // anything. Registering the stub here would be worse than useless: the detector treats an
        // unparseable answer as urgent, so every routine message would escalate and the clinic
        // would learn to ignore the banner. With no model configured, the deterministic net
        // decides alone — which is exactly what Invariant 2 says must always be sufficient.
        if (options.Foundry.IsConfigured)
            services.AddSingleton<IRedFlagClassifier, ModelRedFlagClassifier>();

        services.AddSingleton(sp => new RedFlagDetector(
            sp.GetRequiredService<KeywordNet>(),
            sp.GetRequiredService<ILogger<RedFlagDetector>>(),
            sp.GetService<IRedFlagClassifier>(),
            RedFlagClassifierPolicy.Default with
            {
                Budget = TimeSpan.FromMilliseconds(options.Safety.RedFlagClassifierTimeoutMs),
            }));

        // ── Retrieval and memory ──
        services.AddScoped<ISearchIndex, InProcessSearchIndex>();
        services.AddSingleton<IClinicianPreferenceStore, InMemoryPreferenceStore>();
        services.AddScoped<MemoryWriteGate>();

        // ── Prompts, tools, runner ──
        services.AddSingleton<IPromptRegistry, PromptRegistry>();
        services.AddScoped<ClinicalToolFactory>();
        services.AddScoped<GuardedAgentRunner>();

        // ── The agents themselves ──
        services.AddScoped<ScribeService>();
        services.AddScoped<ExtractionService>();
        services.AddScoped<ChartQaService>();
        services.AddScoped<ClinicalEvidenceService>();
        services.AddScoped<PatientCommsService>();
        services.AddScoped<AssistantService>();

        return services;
    }
}
