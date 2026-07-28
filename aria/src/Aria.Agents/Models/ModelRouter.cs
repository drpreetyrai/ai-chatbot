using Aria.Shared.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Agents.Models;

public sealed class ModelRouter : IModelRouter, IDisposable
{
    private readonly AriaOptions _options;
    private readonly Dictionary<ModelTask, IChatClient> _clients = [];
    private readonly IChatClient? _shared;
    private readonly ILogger<ModelRouter> _logger;

    public bool IsLive { get; }
    public string ModeDescription { get; }

    public ModelRouter(AriaOptions options, ILoggerFactory loggerFactory, IChatClient? liveClient)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<ModelRouter>();
        IsLive = liveClient is not null;

        if (liveClient is not null)
        {
            _shared = liveClient;
            ModeDescription = options.Foundry.UsesOpenAi
                ? $"OpenAI · {options.Foundry.ReasoningDeployment} / {options.Foundry.FastDeployment}"
                : $"Microsoft Foundry · {options.Foundry.ReasoningDeployment} / {options.Foundry.FastDeployment}";
            _logger.LogInformation("Model plane: LIVE ({Mode})", ModeDescription);
        }
        else
        {
            _shared = new LocalDemoChatClient();
            ModeDescription = "LOCAL STUB — deterministic clinical model, no Foundry endpoint configured";
            _logger.LogWarning(
                "Model plane: LOCAL STUB. Set FOUNDRY_PROJECT_ENDPOINT in .env to use real models. " +
                "All guardrails, memory, tools and evaluation still run.");
        }

        foreach (var task in Enum.GetValues<ModelTask>()) _clients[task] = _shared!;
    }

    public IChatClient GetChatClient(ModelTask task) => _clients[task];

    public string DeploymentFor(ModelTask task) => task switch
    {
        ModelTask.NoteSynthesis or ModelTask.ChartQa or ModelTask.ClinicalEvidence
            => _options.Foundry.ReasoningDeployment,
        ModelTask.Classification => _options.Foundry.ClassifyDeployment,
        _ => _options.Foundry.FastDeployment,
    };

    public TimeSpan TimeoutFor(ModelTask task) => task switch
    {
        ModelTask.NoteSynthesis    => TimeSpan.FromSeconds(_options.Foundry.ReasoningTimeoutSeconds),
        ModelTask.ChartQa          => TimeSpan.FromSeconds(Math.Min(15, _options.Foundry.ReasoningTimeoutSeconds)),
        ModelTask.ClinicalEvidence => TimeSpan.FromSeconds(Math.Min(20, _options.Foundry.ReasoningTimeoutSeconds)),
        ModelTask.Classification   => TimeSpan.FromSeconds(_options.Foundry.ClassifyTimeoutSeconds),
        _                          => TimeSpan.FromSeconds(_options.Foundry.FastTimeoutSeconds),
    };

    public void Dispose() => _shared?.Dispose();
}
