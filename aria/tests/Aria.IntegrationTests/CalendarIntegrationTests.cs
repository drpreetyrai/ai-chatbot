using System.Net;

namespace Aria.IntegrationTests;

/// <summary>
/// Google Calendar consent.
///
/// These run with Google unconfigured — which is the state every test machine and every
/// CI runner is in, and the state a first-time developer is in. What matters then is that
/// the app says so plainly instead of offering a button that cannot work.
/// </summary>
[Collection(AriaCollection.Name)]
public sealed class CalendarIntegrationTests(AriaTestHost host)
{
    [Fact]
    public async Task Status_reports_unconfigured_with_a_reason_a_person_can_act_on()
    {
        var doctor = await host.AsClinicianAsync();

        var status = await doctor.GetJsonAsync("/v1/integrations/google/status");

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("connected").GetBoolean());

        // The reason has to name the thing to change, not merely report a negative.
        var reason = status.GetProperty("reason").GetString()!;
        Assert.Contains("GOOGLE_CLIENT_ID", reason);
    }

    [Fact]
    public async Task Status_returns_the_redirect_uri_that_must_be_registered()
    {
        var doctor = await host.AsClinicianAsync();

        var status = await doctor.GetJsonAsync("/v1/integrations/google/status");

        // Google reports an unregistered callback as an opaque "Access blocked" page. The
        // exact string it wants is worth surfacing on our side of that wall.
        Assert.Contains("/v1/integrations/google/callback",
            status.GetProperty("redirectUri").GetString()!);
    }

    [Fact]
    public async Task Connect_is_refused_when_google_is_not_configured()
    {
        var doctor = await host.AsClinicianAsync();

        var connect = await doctor.GetAsync("/v1/integrations/google/connect");

        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
    }

    [Fact]
    public async Task A_patient_cannot_connect_a_calendar()
    {
        var patient = await host.AsPatientAsync();

        var connect = await patient.GetAsync("/v1/integrations/google/connect");

        Assert.Equal(HttpStatusCode.Forbidden, connect.StatusCode);
    }

    [Fact]
    public async Task The_callback_refuses_a_request_with_no_authorisation_code()
    {
        var client = host.CreateClient();

        // Unauthenticated by design — Google is the caller — so it must be robust to
        // being poked directly, and must never act on a request it cannot attribute.
        var response = await client.GetAsync("/v1/integrations/google/callback?state=northbridge%7CDR-1042");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Could not connect", body);
    }

    [Fact]
    public async Task The_callback_refuses_a_malformed_state()
    {
        var client = host.CreateClient();

        var response = await client.GetAsync("/v1/integrations/google/callback?code=abc&state=garbage");
        var body = await response.Content.ReadAsStringAsync();

        // The state carries the tenant and doctor. Trusting a malformed one would mean
        // guessing whose calendar a stranger's authorisation belongs to.
        Assert.Contains("Malformed state", body);
    }
}
