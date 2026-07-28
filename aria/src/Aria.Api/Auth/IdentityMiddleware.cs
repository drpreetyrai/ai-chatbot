using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Auth;

/// <summary>
/// Resolves the bearer token to a <see cref="ClinicianIdentity"/> once per request.
///
/// Endpoints never read a role, doctor id, tenant id or patient id from the request
/// body — they take it from here. That is what makes scoping enforceable rather than
/// aspirational: a caller cannot name a tenant or a patient they do not belong to,
/// because there is nowhere in the request to say it.
/// </summary>
public sealed class IdentityMiddleware(RequestDelegate next)
{
    public const string ItemKey = "aria:identity";
    public const string AccountKey = "aria:account";

    public async Task InvokeAsync(HttpContext context, AccountService accounts, AriaDbContext db)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            var account = await accounts.ResolveAsync(token, context.RequestAborted);

            if (account is not null)
            {
                context.Items[AccountKey] = account;
                context.Items[ItemKey] = await ToIdentityAsync(account, db, context.RequestAborted);
                context.Items["aria:token"] = token;
            }
        }

        await next(context);
    }

    /// <summary>
    /// Projects an account onto the clinical identity tuple the rest of the system uses.
    ///
    /// A clinician borrows their linked clinician record, so department, calendar and
    /// messaging-sender all follow from one link. A patient gets an identity scoped to
    /// exactly one patient id and nothing else.
    /// </summary>
    private static async Task<ClinicianIdentity> ToIdentityAsync(
        UserAccount account, AriaDbContext db, CancellationToken ct)
    {
        if (account.Role is UserRole.Clinician && account.LinkedDoctorId is { } doctorId)
        {
            var record = await db.Clinicians.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DoctorId == doctorId, ct);

            if (record is not null) return record.ToIdentity();
        }

        return new ClinicianIdentity(
            account.TenantId,
            // Non-clinicians key on their account id: they have no clinician record, and
            // inventing a fake doctor id would make the audit log lie about who acted.
            account.LinkedDoctorId ?? account.Id,
            account.DisplayName,
            account.Email,
            account.Department ?? account.Role.ToString(),
            account.Role)
        {
            PatientId = account.LinkedPatientId,
        };
    }
}

public static class HttpContextIdentityExtensions
{
    public static ClinicianIdentity? Identity(this HttpContext context) =>
        context.Items.TryGetValue(IdentityMiddleware.ItemKey, out var v) ? v as ClinicianIdentity : null;

    public static UserAccount? Account(this HttpContext context) =>
        context.Items.TryGetValue(IdentityMiddleware.AccountKey, out var v) ? v as UserAccount : null;

    public static string? BearerToken(this HttpContext context) =>
        context.Items.TryGetValue("aria:token", out var v) ? v as string : null;

    public static string FacilityId(this HttpContext context) => "northbridge-main";

    /// <summary>
    /// Every protected endpoint starts with this. Returning 401 rather than falling back
    /// to a default identity is the whole point — there is no anonymous clinical path.
    /// </summary>
    public static bool TryIdentity(this HttpContext context, out ClinicianIdentity identity)
    {
        var found = context.Identity();
        identity = found!;
        return found is not null;
    }

    /// <summary>
    /// An authorisation denial, as a clean 403 with a reason.
    ///
    /// Results.Forbid() defers to an authentication handler, and with none registered it
    /// throws — turning "you may not see PHI" into a 500. A permission boundary that
    /// surfaces as a server error is indistinguishable from a bug.
    /// </summary>
    public static IResult Denied(this ClinicianIdentity identity, string what) =>
        Results.Json(new
        {
            error = $"Your role ({identity.Role}) is not permitted to {what}.",
            role = identity.Role.ToString(),
        }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// The single place "may this caller see this patient?" is answered.
    ///
    /// A patient may see exactly their own record. Scattering this check across endpoints
    /// is how one gets forgotten, and one forgotten check is a cross-patient leak.
    /// </summary>
    public static IResult? GuardPatientAccess(this ClinicianIdentity identity, string patientId)
    {
        if (!identity.MayViewPhi) return identity.Denied("view patient data");
        if (!identity.MayAccessPatient(patientId)) return identity.Denied("view another patient's record");
        return null;
    }
}
