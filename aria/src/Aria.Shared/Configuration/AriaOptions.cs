using System.ComponentModel.DataAnnotations;

namespace Aria.Shared.Configuration;

/// <summary>
/// Root configuration. Bound and validated at startup with <c>ValidateOnStart()</c> so a missing
/// or malformed value throws before the app serves a single request. A missing secret must never
/// be discovered by a patient (plan.md §15.1).
/// </summary>
public sealed class AriaOptions
{
    public const string SectionName = "Aria";

    [Required] public string Environment { get; set; } = "Development";
    [Required] public string RegionCode { get; set; } = "centralindia";

    /// <summary>Hard switch. Asserted false outside Production at startup — dev stamps never hold PHI.</summary>
    public bool AllowPhi { get; set; }

    public string SqlitePath { get; set; } = "./aria.db";

    /// <summary>
    /// How fast Demo Mode replays the scripted consultation. 1.0 is real time.
    ///
    /// Exposed because the same code path serves three audiences with different needs: a
    /// clinician being onboarded wants realistic pacing, a sales demo wants it brisk, and CI
    /// wants it instant. Without this the integration suite spends four minutes watching a
    /// simulated consultation, which is four minutes nobody will keep paying.
    /// </summary>
    [Range(0.1, 1000)] public double DemoPlaybackSpeed { get; set; } = 1.25;
    public string? PostgresConnection { get; set; }

    public FoundryOptions Foundry { get; set; } = new();
    public ContentSafetyOptions ContentSafety { get; set; } = new();
    public SpeechOptions Speech { get; set; } = new();
    public LanguageOptions Language { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public GoogleOptions Google { get; set; } = new();
    public WhatsAppOptions WhatsApp { get; set; } = new();
    public FhirOptions Fhir { get; set; } = new();
    public IdentityOptions Identity { get; set; } = new();
    public SafetyDials Safety { get; set; } = new();
    public ObservabilityOptions Observability { get; set; } = new();

    public bool IsProduction => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);
}

public sealed class FoundryOptions
{
    public string? ProjectEndpoint { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>
    /// An OpenAI API key, as an alternative to a Foundry deployment.
    ///
    /// Takes precedence when both are present. The reason it exists is practical: a fresh
    /// Foundry project often has one router deployment pointing at a slow reasoning model,
    /// which is the wrong shape for live extraction and classification. Being able to point
    /// at gpt-4o-mini directly makes the fast path actually fast.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    [Required] public string ReasoningDeployment { get; set; } = "aria-reasoning";
    [Required] public string FastDeployment { get; set; } = "aria-fast";
    [Required] public string ClassifyDeployment { get; set; } = "aria-classify";
    [Required] public string EmbedDeployment { get; set; } = "aria-embed";

    [Range(1, 300)] public int ReasoningTimeoutSeconds { get; set; } = 25;
    [Range(1, 60)]  public int FastTimeoutSeconds { get; set; } = 6;
    [Range(1, 30)]  public int ClassifyTimeoutSeconds { get; set; } = 2;

    /// <summary>False means every model call is served by the deterministic local stub.</summary>
    public bool IsConfigured => UsesOpenAi || !string.IsNullOrWhiteSpace(ProjectEndpoint);

    public bool UsesOpenAi => !string.IsNullOrWhiteSpace(OpenAiApiKey);
}

public sealed class ContentSafetyOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>block | audit. Anything other than "audit" is treated as block — fail closed.</summary>
    public string ShieldMode { get; set; } = "block";

    [Range(0.0, 1.0)] public double GroundednessThreshold { get; set; } = 0.75;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
    public bool BlockOnDetection => !string.Equals(ShieldMode, "audit", StringComparison.OrdinalIgnoreCase);
}

public sealed class SpeechOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string Region { get; set; } = "centralindia";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// The region actually used, preferring the one embedded in the endpoint.
    ///
    /// SPEECH_ENDPOINT and SPEECH_REGION are two fields that must agree, and when they
    /// disagree the failure is a bare 401 that says nothing about which one is wrong.
    /// The endpoint is the value people paste from the portal, so it wins.
    /// </summary>
    public string ResolvedRegion
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Endpoint)) return Region;

            if (Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri))
            {
                // https://<region>.api.cognitive.microsoft.com  |  <name>.cognitiveservices.azure.com
                var first = uri.Host.Split('.')[0];
                if (uri.Host.Contains(".api.cognitive.microsoft.com", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(first))
                    return first;
            }

            return Region;
        }
    }
}

public sealed class LanguageOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

public sealed class SearchOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string GuidelinesIndex { get; set; } = "guidelines-v1";
    public string PatientsIndex { get; set; } = "patient-records";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

public sealed class GoogleOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string RedirectUri { get; set; } = "https://localhost:7001/v1/integrations/google/callback";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class WhatsAppOptions
{
    public string? PhoneNumberId { get; set; }
    public string? BusinessAccountId { get; set; }
    public string? AccessToken { get; set; }
    public string? AppSecret { get; set; }
    public string WebhookVerifyToken { get; set; } = "aria-local-verify-token";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed class FhirOptions
{
    public string? BaseUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed class IdentityOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Audience { get; set; } = "api://aria";

    /// <summary>Local dev issuer signing key. Ignored entirely when Entra is configured.</summary>
    [MinLength(32)] public string DevSigningKey { get; set; } = "dev-only-signing-key-change-me-0123456789abcdef";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class SafetyDials
{
    [Range(0.0, 1.0)] public double ConfidenceLow { get; set; } = 0.65;
    [Range(0.0, 1.0)] public double ConfidenceHigh { get; set; } = 0.85;
    [Range(10, 3600)] public int EscalationAckSlaSeconds { get; set; } = 120;
    [Range(0, 365)]   public int AudioRetentionDays { get; set; }
    [Range(0, 300)]   public int MessageUndoSeconds { get; set; } = 30;

    /// <summary>
    /// How long the red-flag classifier gets before the detector gives up on it.
    ///
    /// The original 800 ms assumed a co-located small model. Over the public internet
    /// even a fast model rarely answers that quickly, so every call timed out, every
    /// timeout correctly failed safe, and every routine message escalated. Configurable
    /// because the right number depends entirely on where the model is.
    /// </summary>
    [Range(200, 30_000)] public int RedFlagClassifierTimeoutMs { get; set; } = 2_500;

    public string AutonomyAppointmentReminder { get; set; } = "auto";
    public string AutonomyPostVisitSummary { get; set; } = "draft";
    public string AutonomyRescheduleOffers { get; set; } = "draft";
    public string AutonomyClinicalQaReplies { get; set; } = "draft";
}

public sealed class ObservabilityOptions
{
    public string? ApplicationInsightsConnectionString { get; set; }
    public string ServiceName { get; set; } = "aria-api";

    /// <summary>Prompts and completions contain PHI. This must never be true in production.</summary>
    public bool CaptureContent { get; set; }
}
