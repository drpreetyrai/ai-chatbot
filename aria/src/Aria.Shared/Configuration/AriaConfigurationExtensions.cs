using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aria.Shared.Configuration;

/// <summary>
/// Maps the flat .env variable names onto the nested options tree, then binds and validates.
///
/// The flat names are deliberate: an operator handed a list of keys to paste into Key Vault should
/// not have to learn our object graph. The mapping lives here, in one readable table.
/// </summary>
public static class AriaConfigurationExtensions
{
    /// <summary>.env key -> configuration path. One line per setting, no cleverness.</summary>
    private static readonly (string Env, string Path)[] Map =
    [
        ("ARIA_ENVIRONMENT",                      "Aria:Environment"),
        ("ARIA_REGION_CODE",                      "Aria:RegionCode"),
        ("ARIA_ALLOW_PHI",                        "Aria:AllowPhi"),
        ("ARIA_SQLITE_PATH",                      "Aria:SqlitePath"),
        ("ARIA_DEMO_PLAYBACK_SPEED",              "Aria:DemoPlaybackSpeed"),
        ("POSTGRES_CONNECTION",                   "Aria:PostgresConnection"),

        ("FOUNDRY_PROJECT_ENDPOINT",              "Aria:Foundry:ProjectEndpoint"),
        ("FOUNDRY_API_KEY",                       "Aria:Foundry:ApiKey"),
        ("OPENAI_API_KEY",                        "Aria:Foundry:OpenAiApiKey"),
        ("MODEL_REASONING_DEPLOYMENT",            "Aria:Foundry:ReasoningDeployment"),
        ("MODEL_FAST_DEPLOYMENT",                 "Aria:Foundry:FastDeployment"),
        ("MODEL_CLASSIFY_DEPLOYMENT",             "Aria:Foundry:ClassifyDeployment"),
        ("MODEL_EMBED_DEPLOYMENT",                "Aria:Foundry:EmbedDeployment"),
        ("MODEL_TIMEOUT_REASONING_SECONDS",       "Aria:Foundry:ReasoningTimeoutSeconds"),
        ("MODEL_TIMEOUT_FAST_SECONDS",            "Aria:Foundry:FastTimeoutSeconds"),
        ("MODEL_TIMEOUT_CLASSIFY_SECONDS",        "Aria:Foundry:ClassifyTimeoutSeconds"),

        ("CONTENT_SAFETY_ENDPOINT",               "Aria:ContentSafety:Endpoint"),
        ("CONTENT_SAFETY_KEY",                    "Aria:ContentSafety:ApiKey"),
        ("ARIA_PROMPT_SHIELD_MODE",               "Aria:ContentSafety:ShieldMode"),
        ("ARIA_GROUNDEDNESS_THRESHOLD",           "Aria:ContentSafety:GroundednessThreshold"),

        ("SPEECH_ENDPOINT",                       "Aria:Speech:Endpoint"),
        ("SPEECH_KEY",                            "Aria:Speech:ApiKey"),
        ("SPEECH_REGION",                         "Aria:Speech:Region"),

        ("LANGUAGE_ENDPOINT",                     "Aria:Language:Endpoint"),
        ("LANGUAGE_KEY",                          "Aria:Language:ApiKey"),

        ("SEARCH_ENDPOINT",                       "Aria:Search:Endpoint"),
        ("SEARCH_KEY",                            "Aria:Search:ApiKey"),
        ("SEARCH_INDEX_GUIDELINES",               "Aria:Search:GuidelinesIndex"),
        ("SEARCH_INDEX_PATIENTS",                 "Aria:Search:PatientsIndex"),

        ("GOOGLE_CLIENT_ID",                      "Aria:Google:ClientId"),
        ("GOOGLE_CLIENT_SECRET",                  "Aria:Google:ClientSecret"),
        ("GOOGLE_REDIRECT_URI",                   "Aria:Google:RedirectUri"),

        ("WHATSAPP_PHONE_NUMBER_ID",              "Aria:WhatsApp:PhoneNumberId"),
        ("WHATSAPP_BUSINESS_ACCOUNT_ID",          "Aria:WhatsApp:BusinessAccountId"),
        ("WHATSAPP_ACCESS_TOKEN",                 "Aria:WhatsApp:AccessToken"),
        ("WHATSAPP_APP_SECRET",                   "Aria:WhatsApp:AppSecret"),
        ("WHATSAPP_WEBHOOK_VERIFY_TOKEN",         "Aria:WhatsApp:WebhookVerifyToken"),

        ("FHIR_BASE_URL",                         "Aria:Fhir:BaseUrl"),
        ("FHIR_CLIENT_ID",                        "Aria:Fhir:ClientId"),
        ("FHIR_CLIENT_SECRET",                    "Aria:Fhir:ClientSecret"),

        ("AZURE_TENANT_ID",                       "Aria:Identity:TenantId"),
        ("AZURE_CLIENT_ID",                       "Aria:Identity:ClientId"),
        ("AZURE_CLIENT_SECRET",                   "Aria:Identity:ClientSecret"),
        ("ARIA_API_AUDIENCE",                     "Aria:Identity:Audience"),
        ("ARIA_DEV_JWT_SIGNING_KEY",              "Aria:Identity:DevSigningKey"),

        ("ARIA_CONFIDENCE_LOW",                   "Aria:Safety:ConfidenceLow"),
        ("ARIA_CONFIDENCE_HIGH",                  "Aria:Safety:ConfidenceHigh"),
        ("ARIA_ESCALATION_ACK_SLA_SECONDS",       "Aria:Safety:EscalationAckSlaSeconds"),
        ("ARIA_AUDIO_RETENTION_DAYS",             "Aria:Safety:AudioRetentionDays"),
        ("ARIA_MESSAGE_UNDO_SECONDS",             "Aria:Safety:MessageUndoSeconds"),
        ("ARIA_REDFLAG_CLASSIFIER_TIMEOUT_MS",    "Aria:Safety:RedFlagClassifierTimeoutMs"),
        ("ARIA_AUTONOMY_APPOINTMENT_REMINDER",    "Aria:Safety:AutonomyAppointmentReminder"),
        ("ARIA_AUTONOMY_POST_VISIT_SUMMARY",      "Aria:Safety:AutonomyPostVisitSummary"),
        ("ARIA_AUTONOMY_RESCHEDULE_OFFERS",       "Aria:Safety:AutonomyRescheduleOffers"),
        ("ARIA_AUTONOMY_CLINICAL_QA_REPLIES",     "Aria:Safety:AutonomyClinicalQaReplies"),

        ("APPLICATIONINSIGHTS_CONNECTION_STRING", "Aria:Observability:ApplicationInsightsConnectionString"),
        ("OTEL_SERVICE_NAME",                     "Aria:Observability:ServiceName"),
        ("ARIA_OTEL_CAPTURE_CONTENT",             "Aria:Observability:CaptureContent"),
    ];

    /// <summary>Loads .env (dev only) and projects the flat keys into the Aria: configuration section.</summary>
    public static IConfigurationBuilder AddAriaEnvironment(this IConfigurationBuilder builder, string contentRoot)
    {
        string? solutionRoot = null;

        // ARIA_IGNORE_DOTENV skips the file entirely.
        //
        // Test harnesses and containers need to state their configuration exactly, with
        // nothing inherited. Without this, a developer who has filled in real Azure
        // credentials cannot run the E2E suite — it would sign in against their real
        // tenant, write to their real FHIR server, and message real numbers. The suite
        // must be isolated from the developer's environment, not fighting it.
        var ignoreDotEnv = string.Equals(
            System.Environment.GetEnvironmentVariable("ARIA_IGNORE_DOTENV"), "true",
            StringComparison.OrdinalIgnoreCase);

        foreach (var candidate in ignoreDotEnv ? [] : new[]
                 {
                     Path.Combine(contentRoot, ".env"),
                     Path.Combine(contentRoot, "..", "..", ".env"),
                     Path.Combine(contentRoot, "..", "..", "..", ".env"),
                 })
        {
            if (!File.Exists(candidate)) continue;

            // clobberExistingVars: false — a real environment variable always beats the
            // .env file. The default is the opposite, which means a stray .env silently
            // overrides what the container, the CI job or the test harness set. That is
            // the wrong precedence everywhere, and it is invisible when it bites.
            DotNetEnv.Env.Load(candidate, new DotNetEnv.LoadOptions(
                setEnvVars: true, clobberExistingVars: false, onlyExactPath: true));

            solutionRoot = Path.GetDirectoryName(Path.GetFullPath(candidate));
            break;
        }

        var projected = new Dictionary<string, string?>();
        foreach (var (env, path) in Map)
        {
            var value = System.Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value)) projected[path] = value.Trim();
        }

        // The local database path is anchored to the directory holding .env, not to each
        // project's own content root.
        //
        // Without this, the API resolves "./aria.db" under src/Aria.Api and the workers resolve
        // it under src/Aria.Workers — two separate databases. The signature would write to one
        // outbox and the dispatcher would poll a different, permanently empty one, and the whole
        // post-signature fan-out would look "fine" while silently doing nothing.
        if (solutionRoot is not null)
        {
            var configured = projected.GetValueOrDefault("Aria:SqlitePath") ?? "./aria.db";
            if (!Path.IsPathRooted(configured))
                projected["Aria:SqlitePath"] = Path.GetFullPath(Path.Combine(solutionRoot, configured));
        }

        return builder.AddInMemoryCollection(projected);
    }

    public static IServiceCollection AddAriaOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AriaOptions>()
                .Bind(config.GetSection(AriaOptions.SectionName))
                .ValidateDataAnnotations()
                .Validate(o => !(o.AllowPhi && !o.IsProduction),
                    "ARIA_ALLOW_PHI must be false outside a Production stamp.")
                .Validate(o => !(o.IsProduction && o.Observability.CaptureContent),
                    "ARIA_OTEL_CAPTURE_CONTENT must be false in production — prompts and completions contain PHI.")
                .Validate(o => o.Safety.ConfidenceLow < o.Safety.ConfidenceHigh,
                    "ARIA_CONFIDENCE_LOW must be below ARIA_CONFIDENCE_HIGH.")
                .Validate(o => !(o.IsProduction && !o.ContentSafety.IsConfigured),
                    "Content Safety must be configured in production — the local heuristic shield is not production-grade.")
                .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AriaOptions>>().Value);
        return services;
    }
}
