using Aria.Agents.Models;
using Aria.Agents.Runtime;
using Aria.Api.Auth;
using Aria.Api.Endpoints;
using Aria.Api.Services;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Seed;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
//  Configuration. .env is loaded for development only; production resolves the
//  same keys from Key Vault via managed identity (plan.md §15).
// ─────────────────────────────────────────────────────────────────────────────
builder.Configuration.AddAriaEnvironment(builder.Environment.ContentRootPath);
builder.Services.AddAriaOptions(builder.Configuration);

var options = new AriaOptions();
builder.Configuration.GetSection(AriaOptions.SectionName).Bind(options);

// ─────────────────────────────────────────────────────────────────────────────
//  Persistence. SQLite locally so the product runs with no infrastructure at
//  all; Postgres when a connection string is present.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AriaDbContext>(o =>
{
    if (!string.IsNullOrWhiteSpace(options.PostgresConnection))
        o.UseNpgsql(options.PostgresConnection);
    else
        o.UseSqlite($"Data Source={options.SqlitePath}");
});

builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddSingleton<IAriaEventSink>(new InMemoryEventSink());

// ── Agents, guardrails, memory, retrieval ──
builder.Services.AddAriaAgents(options);

// ── Application services ──
builder.Services.AddScoped<AccountService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<SignatureService>();
builder.Services.AddScoped<EncounterService>();
builder.Services.AddScoped<EscalationService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<InboxService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://localhost:4173", "http://127.0.0.1:5173")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ─────────────────────────────────────────────────────────────────────────────
//  Observability. Traces and metrics always flow; they go to Azure Monitor when
//  a connection string exists and to the console otherwise, so instrumentation
//  is verifiable on day one rather than after an ingestion delay.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(options.Observability.ServiceName, serviceVersion: "1.0.0"))
    .WithTracing(t =>
    {
        t.AddSource(AriaDiagnostics.ActivitySourceName)
         .AddSource("Microsoft.Agents.AI")
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(AriaDiagnostics.MeterName)
         .AddAspNetCoreInstrumentation();
    });

var app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
//  Startup: create the schema, seed the clinic, and tell the operator exactly
//  which services are live and which are on a local stub. Never make them guess
//  which brain they are talking to.
// ─────────────────────────────────────────────────────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AriaDbContext>();

    // ARIA_RESET_DATABASE lets a test harness start from a clean clinic.
    //
    // The database resets ITSELF, here, once, before it serves a request. The
    // obvious alternative — having the harness delete the file — races the running
    // server: the API recreates an empty file on the next write and every query
    // fails with "no such table", which looks exactly like a product bug.
    if (string.Equals(Environment.GetEnvironmentVariable("ARIA_RESET_DATABASE"), "true",
                      StringComparison.OrdinalIgnoreCase))
    {
        if (options.IsProduction)
            throw new InvalidOperationException("ARIA_RESET_DATABASE is refused in production.");

        await db.Database.EnsureDeletedAsync();
    }

    await db.Database.EnsureCreatedAsync();

    // EnsureCreated is all-or-nothing, so a database made before a new entity shipped is
    // missing its table. Create just the new ones rather than telling people to delete
    // the database they have accounts in. Development only — production uses migrations.
    await DevSchema.EnsureNewTablesAsync(db, app.Services.GetRequiredService<ILogger<Program>>());

    await DemoSeeder.SeedAsync(db);

    var router = scope.ServiceProvider.GetRequiredService<IModelRouter>();
    var shield = scope.ServiceProvider.GetRequiredService<Aria.Agents.Safety.IPromptShield>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();

    static string Row(string label, bool live, string liveText, string stubText) =>
        $"  {label,-16} {(live ? "LIVE" : "STUB")}  ·  {(live ? liveText : stubText)}";

    log.LogInformation(
        "\n\n  ARIA · Ambient AI Healthcare Assistant\n" +
        "  ─────────────────────────────────────────────────────────────────────────\n" +
        string.Join('\n',
        [
            Row("Model plane", router.IsLive, router.ModeDescription, "deterministic clinical model"),
            Row("Prompt shield", options.ContentSafety.IsConfigured, "Azure AI Content Safety", shield.Name),
            Row("Speech", options.Speech.IsConfigured, "Azure AI Speech", "scripted consultation (Demo Mode)"),
            Row("Clinical NLP", options.Language.IsConfigured, "Text Analytics for Health", "built-in clinical lexicon"),
            Row("Retrieval", options.Search.IsConfigured, "Azure AI Search", "in-process hybrid index"),
            Row("Calendar", options.Google.IsConfigured, "Google Calendar", "in-memory clinic week"),
            Row("Messaging", options.WhatsApp.IsConfigured, "WhatsApp Business", "simulated thread"),
            Row("EHR", options.Fhir.IsConfigured, "FHIR R4", "local FHIR store"),
            // Sign-in is always local accounts with administrator approval in this build.
            // The Entra credentials, when present, authenticate SERVICES — the FHIR server,
            // Key Vault — not people. Labelling this row "Entra ID · LIVE" because those
            // exist would be the banner lying about the one thing it is here to be honest
            // about, and a reviewer would reasonably conclude SSO had been wired up.
            Row("Sign-in", false, "", "local accounts · administrator approval"),
            Row("Service auth", options.Identity.IsConfigured, "Entra ID app registration", "none configured"),
        ]) +
        "\n  ─────────────────────────────────────────────────────────────────────────\n" +
        "  Every STUB is a working local implementation. Guardrails, memory, tool\n" +
        "  authority, audit and evaluation run identically either way. Fill in the\n" +
        "  matching section of .env to switch one to LIVE.\n");
}

app.UseCors();

// Resolves the caller once per request and stashes the identity tuple for endpoints.
app.UseMiddleware<IdentityMiddleware>();

app.MapAuthEndpoints();
app.MapAccountAdminEndpoints();
app.MapPortalEndpoints();
app.MapAssistantEndpoints();
app.MapSpeechEndpoints();
app.MapIntegrationEndpoints();
app.MapEncounterEndpoints();
app.MapNoteEndpoints();
app.MapPatientEndpoints();
app.MapInboxEndpoints();
app.MapScheduleEndpoints();
app.MapAdminEndpoints();
app.MapInsightsEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));

app.Run();

/// <summary>Exposed so integration tests can spin the real host up in-process.</summary>
public partial class Program;
