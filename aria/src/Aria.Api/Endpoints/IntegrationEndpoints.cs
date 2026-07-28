using System.Net.Http.Json;
using System.Text.Json;
using Aria.Api.Auth;
using Aria.Domain;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

/// <summary>
/// Google Calendar consent.
///
/// The calendar Aria writes to is the one the clinician themselves authorised — never
/// a shared service account. That matters for two reasons: the events appear under
/// their own identity, and revoking access is something they can do unilaterally from
/// their Google account without anyone's help.
///
/// Refresh tokens are stored per clinician. In production they belong in Key Vault
/// (plan.md §15); here they are a row, and the row is never returned by any endpoint.
/// </summary>
public static class IntegrationEndpoints
{
    private const string Scopes = "https://www.googleapis.com/auth/calendar.events " +
                                  "https://www.googleapis.com/auth/calendar.readonly";

    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/integrations/google");

        group.MapGet("/status", async (
            HttpContext http, AriaDbContext db, AriaOptions options, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var connection = await db.CalendarConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DoctorId == me.DoctorId, ct);

            return Results.Ok(new
            {
                configured = options.Google.IsConfigured,
                connected = connection is not null,
                calendarId = connection?.CalendarId,
                connectedAt = connection?.ConnectedAt,
                // Returned so the one failure everybody hits — Google's redirect_uri_mismatch,
                // which it presents as an opaque "Access blocked" page — is diagnosable from
                // our own screen rather than from Google's.
                redirectUri = options.Google.RedirectUri,
                reason = options.Google.IsConfigured
                    ? null
                    : "Google Calendar is not configured. Set GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET in .env.",
            });
        });

        // Step 1 — hand the clinician a consent URL.
        //
        // access_type=offline plus prompt=consent is what actually yields a refresh
        // token. Without both, the first authorisation works and every later one
        // silently returns only an access token that expires in an hour.
        group.MapGet("/connect", (HttpContext http, AriaOptions options) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (!me.IsClinician) return me.Denied("connect a calendar");

            if (!options.Google.IsConfigured)
                return Results.BadRequest(new { error = "Google Calendar is not configured." });

            // The doctor id travels in `state` so the callback knows whose calendar this
            // is without trusting anything the browser sends back.
            var state = Uri.EscapeDataString($"{me.TenantId}|{me.DoctorId}");

            var url =
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(options.Google.ClientId!)}" +
                $"&redirect_uri={Uri.EscapeDataString(options.Google.RedirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scopes)}" +
                "&access_type=offline" +
                "&prompt=consent" +
                $"&state={state}";

            return Results.Ok(new { url });
        });

        // Step 2 — Google redirects here with a code. Exchange it and store the refresh token.
        group.MapGet("/callback", async (
            string? code, string? state, string? error,
            AriaDbContext db, AriaOptions options, IAuditService audit,
            IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
                return Results.Content(Page($"Google returned an error: {error}"), "text/html");

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return Results.Content(Page("Missing authorisation code."), "text/html");

            var parts = Uri.UnescapeDataString(state).Split('|');
            if (parts.Length != 2) return Results.Content(Page("Malformed state."), "text/html");

            var (tenantId, doctorId) = (parts[0], parts[1]);

            var client = factory.CreateClient();
            using var response = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = options.Google.ClientId!,
                    ["client_secret"] = options.Google.ClientSecret!,
                    ["redirect_uri"] = options.Google.RedirectUri,
                    ["grant_type"] = "authorization_code",
                }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return Results.Content(Page($"Token exchange failed ({(int)response.StatusCode}). {Truncate(body)}"), "text/html");

            using var payload = JsonDocument.Parse(body);
            var refreshToken = payload.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            if (string.IsNullOrWhiteSpace(refreshToken))
                return Results.Content(Page(
                    "Google did not return a refresh token. Remove Aria from your Google account's " +
                    "third-party access and try again — Google only issues one on first consent."), "text/html");

            // Ask Google which calendar this actually is, rather than assuming the
            // clinician's email address is their calendar id.
            var accessToken = payload.RootElement.GetProperty("access_token").GetString();
            var calendarId = await PrimaryCalendarAsync(client, accessToken!, ct) ?? "primary";

            var existing = await db.CalendarConnections.FirstOrDefaultAsync(c => c.DoctorId == doctorId, ct);

            if (existing is null)
            {
                db.CalendarConnections.Add(new CalendarConnection
                {
                    DoctorId = doctorId, TenantId = tenantId,
                    CalendarId = calendarId, RefreshToken = refreshToken,
                });
            }
            else
            {
                existing.CalendarId = calendarId;
                existing.RefreshToken = refreshToken;
                existing.ConnectedAt = DateTimeOffset.UtcNow;
            }

            // Point the clinician's bookings at the calendar they just authorised. Without this
            // the connection exists but every appointment still goes to whatever was seeded.
            var clinician = await db.Clinicians.FirstOrDefaultAsync(c => c.DoctorId == doctorId, ct);
            if (clinician is not null)
            {
                clinician.CalendarConnected = true;
                clinician.GoogleCalendarId = calendarId;
            }

            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(tenantId, doctorId, ActorKind.Clinician, "CALENDAR_CONNECTED",
                "integration", calendarId, detail: new { provider = "google" }, ct: ct);

            return Results.Content(Page($"Calendar connected: {calendarId}. You can close this tab.", ok: true), "text/html");
        });

        group.MapPost("/disconnect", async (
            HttpContext http, AriaDbContext db, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.DoctorId == me.DoctorId, ct);
            if (connection is not null) db.CalendarConnections.Remove(connection);

            var clinician = await db.Clinicians.FirstOrDefaultAsync(c => c.DoctorId == me.DoctorId, ct);
            if (clinician is not null) clinician.CalendarConnected = false;
            // GoogleCalendarId is left in place: it is the historical target of already-booked
            // events, and clearing it would make past outbox rows unexplainable.

            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Clinician, "CALENDAR_DISCONNECTED",
                "integration", me.DoctorId, ct: ct);

            return Results.Ok(new { disconnected = true });
        });
    }

    private static async Task<string?> PrimaryCalendarAsync(HttpClient client, string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://www.googleapis.com/calendar/v3/users/me/calendarList/primary");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return payload.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>A plain confirmation page — this tab is opened by Google, not by our SPA.</summary>
    private static string Page(string message, bool ok = false) => $"""
        <!doctype html><meta charset="utf-8">
        <title>Aria · Google Calendar</title>
        <body style="font-family:Inter,system-ui,sans-serif;background:#f7f8fa;color:#0b1220;
                     display:grid;place-items:center;height:100vh;margin:0">
          <div style="max-width:32rem;padding:1.5rem;background:#fff;border:1px solid #e3e7ee;border-radius:14px">
            <h1 style="font-size:15px;margin:0 0 .5rem;color:{(ok ? "#117c59" : "#c2373c")}">
              {(ok ? "✓ Connected" : "Could not connect")}
            </h1>
            <p style="font-size:13px;line-height:1.5;color:#5a6779;margin:0">{message}</p>
          </div>
        </body>
        """;

    private static string Truncate(string s) => s.Length <= 200 ? s : string.Concat(s.AsSpan(0, 199), "…");
}
