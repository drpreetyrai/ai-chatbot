using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Aria.IntegrationTests;

/// <summary>
/// Spins the real API up in-process against a throwaway database.
///
/// These tests exercise the genuine HTTP surface — real routing, real identity
/// middleware, real EF Core, real guardrail pipeline, real audit chain. The only
/// thing substituted is the database file, so each test class gets a clean clinic
/// and they can run in parallel without interfering with each other.
///
/// Nothing here mocks the safety layer. A test that passes because the dangerous
/// path was stubbed out would be worse than no test at all.
/// </summary>
public sealed class AriaTestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"aria-test-{Guid.NewGuid():n}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // Ignore the developer's .env completely. A machine with real Azure
        // credentials must run the same tests, against the same stubs, as a machine
        // with none — otherwise the suite is green for different reasons on different
        // laptops, and red on CI for reasons nobody can reproduce.
        System.Environment.SetEnvironmentVariable("ARIA_IGNORE_DOTENV", "true");

        // The database path has to arrive as an environment variable, not through the
        // configuration callback below.
        //
        // Program resolves AriaOptions and registers the DbContext BEFORE the host is built,
        // and WebApplicationFactory's configuration sources are only merged during the build.
        // Setting it here meant every test silently shared ./aria.db in the test project's
        // own directory — a file that survived between runs, so the seeder saw an existing
        // clinic and skipped it, and the failures looked like broken sign-in rather than a
        // stale fixture.
        System.Environment.SetEnvironmentVariable("ARIA_SQLITE_PATH", _dbPath);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Applied last so it wins over anything a developer's local .env set.
            // Tests must never depend on — or write to — the machine's real database,
            // and must never pick up a real Azure endpoint by accident.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aria:SqlitePath"] = _dbPath,
                ["Aria:PostgresConnection"] = null,
                ["Aria:Environment"] = "Development",
                ["Aria:AllowPhi"] = "false",
                ["Aria:Foundry:ProjectEndpoint"] = null,      // deterministic local model
                ["Aria:ContentSafety:Endpoint"] = null,       // local heuristic shield
                ["Aria:Identity:TenantId"] = null,            // dev sign-in path
                ["Aria:Safety:MessageUndoSeconds"] = "30",
                ["Aria:DemoPlaybackSpeed"] = "500",   // the consultation replays instantly
            });
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var suffix in new[] { "", "-shm", "-wal" })
            TryDelete(_dbPath + suffix);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a held handle on CI is not worth failing a green run over */ }
    }

    private readonly SemaphoreSlim _signInLock = new(1, 1);
    private readonly Dictionary<string, string> _tokens = [];

    /// <summary>
    /// Signs in through the real credential flow — the same one a person uses.
    ///
    /// There is deliberately no test-only back door. A shortcut that mints a token
    /// without going through approval would leave the single most important rule in the
    /// system — that nobody signs in until an administrator has linked their account to a
    /// real record — untested by every test that uses it.
    ///
    /// Tokens are cached per email because approval is idempotent but not free: PBKDF2 at
    /// 210k iterations twice per test would dominate the suite's runtime.
    /// </summary>
    public async Task<HttpClient> SignInAsync(string email, string password = BootstrapPassword)
    {
        await _signInLock.WaitAsync();
        try
        {
            if (!_tokens.TryGetValue(email, out var token))
            {
                token = await AuthenticateAsync(email, password);
                _tokens[email] = token;
            }

            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        finally
        {
            _signInLock.Release();
        }
    }

    private async Task<string> AuthenticateAsync(string email, string password)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("/v1/auth/signin", new { email, password });
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Sign-in failed for {email}: {(int)response.StatusCode} {detail}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// A session of its own, outside the shared cache.
    ///
    /// For tests that revoke what they sign in with. Signing out of the cached clinician
    /// token would take every other test's client down with it — and the failure would
    /// land in whichever test happened to run next, which is the worst kind of flake.
    /// </summary>
    public async Task<HttpClient> FreshSessionAsync(string email, string password = BootstrapPassword)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await AuthenticateAsync(email, password));

        return client;
    }

    /// <summary>The one account seeded already approved — everyone else goes through them.</summary>
    public Task<HttpClient> AsAdminAsync() => SignInAsync(AdminEmail);

    public Task<HttpClient> AsClinicianAsync() =>
        ApprovedAsync("maya.rao@northbridge.health", linkedDoctorId: "DR-1042");

    public Task<HttpClient> AsPatientAsync() =>
        ApprovedAsync("john.abraham@example.com", linkedPatientId: "pt-john");

    public async Task<HttpClient> AsCoordinatorAsync()
    {
        // No coordinator is seeded, so this exercises the full journey: register, wait,
        // be approved, then sign in. Which is the journey most accounts actually take.
        const string email = "ravi.k@northbridge.health";

        await RegisterAsync(email, "Ravi Kumar", "Coordinator", "Front desk");
        return await ApprovedAsync(email, linkedDoctorId: "ST-2210");
    }

    /// <summary>Registers an account if it does not already exist. Safe to call repeatedly.</summary>
    private async Task RegisterAsync(string email, string name, string role, string? department = null)
    {
        var client = CreateClient();

        await client.PostAsJsonAsync("/v1/auth/signup", new
        {
            email,
            password = BootstrapPassword,
            displayName = name,
            role,
            department,
            reason = "Integration test account.",
        });
    }

    /// <summary>Has the administrator approve and link an account, then signs in as it.</summary>
    private async Task<HttpClient> ApprovedAsync(
        string email, string? linkedDoctorId = null, string? linkedPatientId = null)
    {
        if (!_tokens.ContainsKey(email))
        {
            var admin = await AsAdminAsync();

            var accounts = await admin.GetJsonAsync("/v1/admin/accounts");
            var account = accounts.EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("email").GetString() == email);

            if (account.ValueKind is JsonValueKind.Undefined)
                throw new InvalidOperationException($"No account exists for {email}.");

            if (account.GetProperty("status").GetString() != "Approved")
            {
                var id = account.GetProperty("id").GetString();
                var approve = await admin.PostAsJsonAsync($"/v1/admin/accounts/{id}/approve",
                    new { linkedDoctorId, linkedPatientId, note = "Verified by the integration suite." });

                approve.EnsureSuccessStatusCode();
            }
        }

        return await SignInAsync(email);
    }

    public const string AdminEmail = "admin@northbridge.health";

    /// <summary>Mirrors the seeder's one well-known credential (Aria.Infrastructure DemoSeeder).</summary>
    public const string BootstrapPassword = "AriaAdmin!2026";
}

/// <summary>Shared host per test class — seeding a clinic per test would be wasteful.</summary>
[CollectionDefinition(Name)]
public sealed class AriaCollection : ICollectionFixture<AriaTestHost>
{
    public const string Name = "aria";
}

public static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement;
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.JsonAsync();
    }

    public static async Task<JsonElement> PostJsonAsync(this HttpClient client, string path, object? body = null)
    {
        var response = body is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, body);

        response.EnsureSuccessStatusCode();
        return await response.JsonAsync();
    }

    /// <summary>Reads a full SSE stream to completion and returns every payload.</summary>
    public static async Task<List<JsonElement>> StreamAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var events = new List<JsonElement>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line[6..];
            if (string.IsNullOrWhiteSpace(payload) || payload == "{}") continue;

            events.Add(JsonDocument.Parse(payload).RootElement.Clone());
        }

        return events;
    }
}
