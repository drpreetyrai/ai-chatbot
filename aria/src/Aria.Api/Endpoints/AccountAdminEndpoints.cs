using Aria.Api.Auth;
using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

/// <summary>
/// The approval queue — the admin's core job, and the gate everything else waits behind.
///
/// Approving is not a rubber stamp: it is where a registration is BOUND to a clinical
/// record. A patient typing "my MRN is 44192" proves nothing; the admin checks and
/// links, and only then does the account see anything.
/// </summary>
public static class AccountAdminEndpoints
{
    public static void MapAccountAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/admin/accounts");

        group.MapGet("/pending", async (
            HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("review registrations");

            var pending = await accounts.PendingAsync(me.TenantId, ct);
            return Results.Ok(pending.Select(AccountEndpointHelpers.Describe));
        });

        group.MapGet("/", async (
            string? status, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("manage accounts");

            var query = db.Accounts.AsNoTracking().Where(a => a.TenantId == me.TenantId);

            if (Enum.TryParse<AccountStatus>(status, true, out var parsed))
                query = query.Where(a => a.Status == parsed);

            var rows = await query.OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
            return Results.Ok(rows.Select(AccountEndpointHelpers.Describe));
        });

        // The two lists an admin needs in front of them while deciding what to link a
        // registration to. Returned together so the approval dialog is one round trip.
        group.MapGet("/linkable", async (
            HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("manage accounts");

            var linkedDoctors = await db.Accounts.AsNoTracking()
                .Where(a => a.LinkedDoctorId != null).Select(a => a.LinkedDoctorId!).ToListAsync(ct);
            var linkedPatients = await db.Accounts.AsNoTracking()
                .Where(a => a.LinkedPatientId != null).Select(a => a.LinkedPatientId!).ToListAsync(ct);

            var clinicians = await db.Clinicians.AsNoTracking()
                .Where(c => c.TenantId == me.TenantId && c.Status == "active")
                .Select(c => new { c.DoctorId, c.Name, c.Department, Role = c.Role.ToString() })
                .ToListAsync(ct);

            var patients = await db.Patients.AsNoTracking()
                .Where(p => p.TenantId == me.TenantId)
                .Select(p => new { p.Id, p.Name, p.Mrn, p.DateOfBirth })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                // Already-claimed records are flagged rather than hidden, so an admin can
                // see that a second person is claiming an identity someone already has.
                Clinicians = clinicians.Select(c => new
                {
                    c.DoctorId, c.Name, c.Department, c.Role,
                    AlreadyLinked = linkedDoctors.Contains(c.DoctorId),
                }),
                Patients = patients.Select(p => new
                {
                    p.Id, p.Name, p.Mrn, p.DateOfBirth,
                    AlreadyLinked = linkedPatients.Contains(p.Id),
                }),
            });
        });

        group.MapPost("/{id}/approve", async (
            string id, ApproveAccountRequest request, HttpContext http,
            AccountService accounts, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("approve registrations");

            var result = await accounts.ApproveAsync(
                me, id, request.LinkedDoctorId, request.LinkedPatientId, request.Note, ct);

            return result.Success
                ? Results.Ok(AccountEndpointHelpers.Describe(result.Account!))
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/{id}/reject", async (
            string id, RejectAccountRequest request, HttpContext http,
            AccountService accounts, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("reject registrations");

            var result = await accounts.RejectAsync(me, id, request.Note, ct);

            return result.Success
                ? Results.Ok(AccountEndpointHelpers.Describe(result.Account!))
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/{id}/status", async (
            string id, AccountStatusRequest request, HttpContext http,
            AccountService accounts, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("change account status");

            if (!Enum.TryParse<AccountStatus>(request.Status, true, out var status))
                return Results.BadRequest(new { error = $"Unknown status '{request.Status}'." });

            // Suspending revokes live sessions too, so it takes effect immediately rather
            // than whenever the current token happens to expire.
            await accounts.SetStatusAsync(me, id, status, ct);
            return Results.Ok(new { id, status = status.ToString() });
        });
    }
}

public sealed record ApproveAccountRequest(string? LinkedDoctorId, string? LinkedPatientId, string? Note);
public sealed record RejectAccountRequest(string? Note);
public sealed record AccountStatusRequest(string Status);
