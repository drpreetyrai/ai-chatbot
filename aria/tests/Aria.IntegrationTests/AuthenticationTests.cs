using System.Net;
using System.Net.Http.Json;

namespace Aria.IntegrationTests;

/// <summary>
/// The approval gate.
///
/// The rule this suite exists to protect: registering creates a request, not an account.
/// Nobody reaches a patient record until an administrator has looked at their claim and
/// tied it to a real clinician or a real patient. Every other access control in the
/// system is downstream of that link being correct.
/// </summary>
[Collection(AriaCollection.Name)]
public sealed class AuthenticationTests(AriaTestHost host)
{
    private static object SignUp(string email, string role, string? mrn = null) => new
    {
        email,
        password = "Str0ng!Passw0rd",
        displayName = "Test Person",
        role,
        department = "Cardiology",
        phone = "+919000000000",
        reason = "Automated test registration.",
        mrn,
    };

    [Fact]
    public async Task A_new_registration_cannot_sign_in_until_it_is_approved()
    {
        var client = host.CreateClient();
        var email = $"pending-{Guid.NewGuid():n}@northbridge.health";

        var signup = await client.PostAsJsonAsync("/v1/auth/signup", SignUp(email, "Clinician"));
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);

        var attempt = await client.PostAsJsonAsync("/v1/auth/signin",
            new { email, password = "Str0ng!Passw0rd" });

        // 403, not 401: the credentials were right. Telling them the password was wrong
        // would send them round the reset loop forever.
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Contains("approval", (await attempt.JsonAsync()).GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approving_a_clinician_without_linking_a_record_is_refused()
    {
        var admin = await host.AsAdminAsync();
        var client = host.CreateClient();
        var email = $"unlinked-{Guid.NewGuid():n}@northbridge.health";

        await client.PostAsJsonAsync("/v1/auth/signup", SignUp(email, "Clinician"));

        var id = await FindAccountIdAsync(admin, email);

        // An approved clinician with no linked record would carry an identity that maps to
        // no clinician at all — which is precisely the state where authorisation checks
        // start returning surprising answers.
        var approve = await admin.PostAsJsonAsync($"/v1/admin/accounts/{id}/approve",
            new { linkedDoctorId = (string?)null, linkedPatientId = (string?)null, note = "no link" });

        Assert.Equal(HttpStatusCode.BadRequest, approve.StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_can_approve()
    {
        var doctor = await host.AsClinicianAsync();
        var client = host.CreateClient();
        var email = $"selfapprove-{Guid.NewGuid():n}@northbridge.health";

        await client.PostAsJsonAsync("/v1/auth/signup", SignUp(email, "Clinician"));

        var admin = await host.AsAdminAsync();
        var id = await FindAccountIdAsync(admin, email);

        var attempt = await doctor.PostAsJsonAsync($"/v1/admin/accounts/{id}/approve",
            new { linkedDoctorId = "DR-1058", note = "a colleague vouches for them" });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task Signing_out_kills_the_token_immediately()
    {
        // Ensures the account is approved and linked, then takes a session of its own:
        // this test destroys what it signs in with, and the shared clinician client is
        // used by every other test in the suite.
        await host.AsClinicianAsync();
        var doctor = await host.FreshSessionAsync("maya.rao@northbridge.health");

        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync("/v1/auth/me")).StatusCode);

        await doctor.PostAsync("/v1/auth/signout", null);

        // The session row is revoked rather than the browser merely forgetting the token,
        // so a stolen bearer token stops working the moment its owner signs out.
        Assert.Equal(HttpStatusCode.Unauthorized, (await doctor.GetAsync("/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Suspending_an_account_revokes_its_live_sessions()
    {
        var client = host.CreateClient();
        var email = $"suspend-{Guid.NewGuid():n}@northbridge.health";
        await client.PostAsJsonAsync("/v1/auth/signup", SignUp(email, "Clinician"));

        var admin = await host.AsAdminAsync();
        var id = await FindAccountIdAsync(admin, email);

        await admin.PostAsJsonAsync($"/v1/admin/accounts/{id}/approve",
            new { linkedDoctorId = "DR-1058", note = "verified" });

        var signin = await client.PostAsJsonAsync("/v1/auth/signin",
            new { email, password = "Str0ng!Passw0rd" });
        var token = (await signin.JsonAsync()).GetProperty("token").GetString();

        var session = host.CreateClient();
        session.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/v1/auth/me")).StatusCode);

        // Suspension has to take effect now, not at token expiry. The reason an account is
        // suspended is usually that someone should already have stopped using it.
        await admin.PostAsJsonAsync($"/v1/admin/accounts/{id}/status", new { status = "Suspended" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_address_and_a_wrong_password_are_indistinguishable()
    {
        var client = host.CreateClient();

        var unknown = await client.PostAsJsonAsync("/v1/auth/signin",
            new { email = "nobody@northbridge.health", password = "whatever" });

        var wrong = await client.PostAsJsonAsync("/v1/auth/signin",
            new { email = AriaTestHost.AdminEmail, password = "definitely-not-it" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Same status, same words. A different message for a valid address is a free
        // list of everyone who works here.
        Assert.Equal(
            (await unknown.JsonAsync()).GetProperty("error").GetString(),
            (await wrong.JsonAsync()).GetProperty("error").GetString());
    }

    private static async Task<string> FindAccountIdAsync(HttpClient admin, string email)
    {
        var accounts = await admin.GetJsonAsync("/v1/admin/accounts");

        return accounts.EnumerateArray()
            .First(a => a.GetProperty("email").GetString() == email)
            .GetProperty("id").GetString()!;
    }
}
