using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Integrations;
using Aria.Shared.Configuration;
using Aria.Shared.Telemetry;
using System.Text.Json;
using Aria.Workers;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddAriaEnvironment(builder.Environment.ContentRootPath);
builder.Services.AddAriaOptions(builder.Configuration);

var options = new AriaOptions();
builder.Configuration.GetSection(AriaOptions.SectionName).Bind(options);

builder.Services.AddDbContext<AriaDbContext>(o =>
{
    if (!string.IsNullOrWhiteSpace(options.PostgresConnection))
        o.UseNpgsql(options.PostgresConnection);
    else
        o.UseSqlite($"Data Source={options.SqlitePath}");
});

builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddSingleton<IAriaEventSink>(new InMemoryEventSink());

// ─────────────────────────────────────────────────────────────────────────────
//  Adapters. Each one is independently live-or-local, so a clinic can switch on
//  the EHR without also having to configure WhatsApp. The local implementations
//  record everything they would have sent, which is how the write barrier is
//  demonstrated without any external account at all.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<LocalIntegrationInbox>();

if (options.Fhir.IsConfigured)
    builder.Services.AddHttpClient<IEhrAdapter, FhirEhrAdapter>();
else
    builder.Services.AddSingleton<IEhrAdapter, LocalEhrAdapter>();

if (options.Google.IsConfigured)
{
    builder.Services.AddSingleton<IGoogleTokenProvider, ConfiguredGoogleTokenProvider>();
    builder.Services.AddHttpClient<ICalendarAdapter, GoogleCalendarAdapter>();
}
else
{
    builder.Services.AddSingleton<ICalendarAdapter, LocalCalendarAdapter>();
}

if (options.WhatsApp.IsConfigured)
    builder.Services.AddHttpClient<IMessagingAdapter, WhatsAppAdapter>();
else
    builder.Services.AddSingleton<IMessagingAdapter, LocalMessagingAdapter>();

builder.Services.AddHostedService<OutboxDispatcher>();

// Mirrors each connected clinician's real calendar into the projection the scheduler reads.
// Only a worker may call Google; the API reads what this leaves behind.
builder.Services.AddHostedService<CalendarSyncWorker>();

var host = builder.Build();

host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "\n  ARIA workers\n" +
    "  ─────────────────────────────────────────────────────────────\n" +
    "  EHR        {Ehr}\n" +
    "  Calendar   {Calendar}\n" +
    "  Messaging  {Messaging}\n" +
    "  ─────────────────────────────────────────────────────────────\n" +
    "  This is the only process that talks to the outside world, and it\n" +
    "  only ever drains the outbox — which only a signature can fill.\n",
    host.Services.GetRequiredService<IEhrAdapter>().Name,
    host.Services.GetRequiredService<ICalendarAdapter>().Name,
    host.Services.GetRequiredService<IMessagingAdapter>().Name);

host.Run();

/// <summary>
/// Reads the per-doctor Google refresh token and exchanges it for an access token.
///
/// Tokens are per clinician, never a shared service account: the calendar Aria writes to is the
/// one the doctor themselves authorised, which is also what makes revocation meaningful.
/// </summary>
internal sealed class ConfiguredGoogleTokenProvider(
    AriaOptions options, IHttpClientFactory factory, IServiceScopeFactory scopes)
    : IGoogleTokenProvider
{
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = [];

    public async Task<string> GetAccessTokenAsync(string calendarId, CancellationToken ct = default)
    {
        // Cached until shortly before expiry: the dispatcher can fire several bookings in a
        // burst, and Google rate-limits token exchange far harder than the calendar API itself.
        if (_cache.TryGetValue(calendarId, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
            return cached.Token;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AriaDbContext>();

        var connection = await db.CalendarConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CalendarId == calendarId, ct);

        // Say what the human has to do, not that a token is missing. This message ends up in
        // the outbox failure reason, which is read by whoever is wondering why nothing booked.
        if (connection is null)
            throw new InvalidOperationException(
                $"No clinician has connected the calendar '{calendarId}'. " +
                "In Aria, open Schedule and choose 'Connect Google Calendar'.");

        var client = factory.CreateClient();
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.Google.ClientId!,
                ["client_secret"] = options.Google.ClientSecret!,
                ["refresh_token"] = connection.RefreshToken,
                ["grant_type"] = "refresh_token",
            }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Google refused the stored refresh token for '{calendarId}' ({(int)response.StatusCode}). " +
                "The clinician has probably revoked Aria's access; they need to reconnect.");

        using var payload = JsonDocument.Parse(body);
        var token = payload.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = payload.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        _cache[calendarId] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }
}
