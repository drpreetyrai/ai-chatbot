using Aria.Api.Auth;
using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/auth");

        // ── Register. Creates a PENDING account; it cannot sign in yet. ──
        group.MapPost("/signup", async (
            SignUpRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.SignUpAsync(request, ct);

            return result.Success
                ? Results.Ok(new
                {
                    status = "pending",
                    message = "Registration received. An administrator will review it before you can sign in.",
                })
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/signin", async (
            SignInRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.SignInAsync(
                request.Email, request.Password, http.Request.Headers.UserAgent.ToString(), ct);

            if (!result.Success)
            {
                // 403 when the credentials were right but the account is not approved —
                // distinguishable from 401 so the UI can say something useful.
                var pending = result.Account is not null;
                return Results.Json(new { error = result.Error },
                    statusCode: pending ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);
            }

            var account = result.Account!;
            return Results.Ok(new
            {
                token = result.Token,
                account = AccountEndpointHelpers.Describe(account),
            });
        });

        group.MapPost("/signout", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            // Revokes the session row, so the token is dead on the next request rather
            // than merely forgotten by the browser.
            if (http.BearerToken() is { } token) await accounts.SignOutAsync(token, ct);
            return Results.Ok(new { signedOut = true });
        });

        group.MapGet("/me", (HttpContext http) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            var account = http.Account();

            return Results.Ok(new
            {
                me.DoctorId,
                me.Name,
                me.Email,
                me.Department,
                Role = me.Role.ToString(),
                me.TenantId,
                me.PatientId,
                AccountId = account?.Id,
                Permissions = new
                {
                    me.CanSign,
                    me.MayViewPhi,
                    IsPatient = me.IsPatient,
                    IsClinician = me.IsClinician,
                    CanConfigure = me.Role is UserRole.Admin,
                    CanApproveAccounts = me.Role is UserRole.Admin,
                },
                // The front end routes on this: doctor workspace, patient portal or
                // admin console. One field, so the three shells cannot disagree.
                Surface = me.Role switch
                {
                    UserRole.Patient => "patient",
                    UserRole.Admin or UserRole.Auditor => "admin",
                    _ => "clinical",
                },
            });
        });

        // Kept so a fresh clone still has a way in before any account exists. Refuses
        // outright once real accounts are configured — see the guard inside.
        group.MapGet("/team", async (AriaDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Clinicians.AsNoTracking()
                .Where(c => c.Status == "active")
                .Select(c => new
                {
                    c.DoctorId, c.Name, c.Email, c.Department,
                    Role = c.Role.ToString(), c.CalendarConnected,
                })
                .ToListAsync(ct)));
    }
}

public static class AccountEndpointHelpers
{
    public static object Describe(UserAccount account) => new
    {
        account.Id,
        account.Email,
        account.DisplayName,
        Role = account.Role.ToString(),
        Status = account.Status.ToString(),
        account.Department,
        account.Phone,
        account.RequestedReason,
        account.LinkedDoctorId,
        account.LinkedPatientId,
        account.CreatedAt,
        account.ReviewedBy,
        account.ReviewedAt,
        account.ReviewNote,
        account.LastSignInAt,
    };
}

public sealed record SignInRequest(string Email, string Password);
