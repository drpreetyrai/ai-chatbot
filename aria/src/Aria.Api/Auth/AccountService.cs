using System.Security.Cryptography;
using System.Text;
using Aria.Domain;
using Aria.Domain.Accounts;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Auth;

public sealed record SignUpRequest(
    string Email, string Password, string DisplayName, string Role,
    string? Department, string? Phone, string? Reason, string? Mrn);

public sealed record AuthOutcome(bool Success, string? Token, string? Error, UserAccount? Account);

/// <summary>
/// Registration, approval and sessions.
///
/// The shape mirrors the rest of the product: anyone may ask, nothing takes effect
/// until a human approves. A clinical system that lets a stranger self-serve into
/// patient data has no meaningful access control, however good its RBAC matrix is.
/// </summary>
public sealed class AccountService(
    AriaDbContext db,
    IAuditService audit,
    ILogger<AccountService> logger)
{
    private const int Iterations = 210_000;          // OWASP guidance for PBKDF2-SHA256
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);   // a clinic day

    // ── Registration ─────────────────────────────────────────────────────────

    public async Task<AuthOutcome> SignUpAsync(SignUpRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (!IsPlausibleEmail(email))
            return new AuthOutcome(false, null, "Enter a valid email address.", null);

        if (request.Password.Length < 10)
            return new AuthOutcome(false, null, "Choose a password of at least 10 characters.", null);

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role)
            || role is not (UserRole.Patient or UserRole.Clinician or UserRole.Coordinator))
            return new AuthOutcome(false, null, "Register as a patient, doctor or coordinator.", null);

        if (await db.Accounts.AnyAsync(a => a.Email == email, ct))
        {
            // Deliberately the same wording whether or not the address exists: a
            // different message here is a free account-enumeration oracle.
            logger.LogInformation("Sign-up attempted for an address already registered.");
            return new AuthOutcome(false, null,
                "If that address can be registered, an administrator will review it.", null);
        }

        var salt = RandomNumberGenerator.GetBytes(16);

        var account = new UserAccount
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            TenantId = DemoSeeder.TenantId,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Hash(request.Password, salt),
            Role = role,
            Status = AccountStatus.Pending,
            Department = request.Department?.Trim(),
            Phone = request.Phone?.Trim(),
            RequestedReason = BuildReason(request),
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(account.TenantId, account.Id, ActorKind.System, "ACCOUNT_REGISTERED",
            "account", account.Id, detail: new { account.Email, Role = role.ToString() }, ct: ct);

        logger.LogInformation("Registration pending approval: {Email} as {Role}", email, role);

        return new AuthOutcome(true, null, null, account);
    }

    /// <summary>A patient's stated MRN is a claim to be verified by the approver, never a key.</summary>
    private static string? BuildReason(SignUpRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Reason)) parts.Add(request.Reason.Trim());
        if (!string.IsNullOrWhiteSpace(request.Mrn)) parts.Add($"Claims MRN {request.Mrn.Trim()} — verify before linking.");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // ── Sign-in / sign-out ───────────────────────────────────────────────────

    public async Task<AuthOutcome> SignInAsync(
        string email, string password, string? userAgent, CancellationToken ct = default)
    {
        var normalised = email.Trim().ToLowerInvariant();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == normalised, ct);

        // Verify against a dummy hash when the account is missing, so a wrong address
        // and a wrong password take the same time. Timing is an oracle too.
        if (account is null)
        {
            _ = Hash(password, RandomNumberGenerator.GetBytes(16));
            return new AuthOutcome(false, null, "Email or password is incorrect.", null);
        }

        if (!Verify(password, account))
        {
            logger.LogWarning("Failed sign-in for {Email}", normalised);
            await audit.WriteAsync(account.TenantId, account.Id, ActorKind.System, "SIGN_IN_FAILED",
                "account", account.Id, outcome: "denied", ct: ct);

            return new AuthOutcome(false, null, "Email or password is incorrect.", null);
        }

        // Correct credentials but not approved: say so plainly. Pretending the password
        // was wrong would just send them round the reset loop forever.
        if (!account.CanSignIn(out var blocked))
            return new AuthOutcome(false, null, blocked, account);

        var token = IssueToken();
        db.Sessions.Add(new UserSession
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            AccountId = account.Id,
            TokenHash = HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime),
            UserAgent = userAgent,
        });

        account.LastSignInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(account.TenantId, account.Id, ActorKind.System, AuditActions.SignIn,
            "account", account.Id, detail: new { Role = account.Role.ToString() }, ct: ct);

        return new AuthOutcome(true, token, null, account);
    }

    /// <summary>
    /// Revokes the session row, so the token stops working on the very next request.
    /// This is the whole reason sessions are rows rather than self-contained tokens.
    /// </summary>
    public async Task SignOutAsync(string token, CancellationToken ct = default)
    {
        var hash = HashToken(token);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, ct);
        if (session is null) return;

        session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == session.AccountId, ct);
        if (account is not null)
            await audit.WriteAsync(account.TenantId, account.Id, ActorKind.System, "SIGN_OUT",
                "account", account.Id, ct: ct);
    }

    /// <summary>Resolves a bearer token to its account, or null if it is unknown, expired or revoked.</summary>
    public async Task<UserAccount?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var hash = HashToken(token);
        var now = DateTimeOffset.UtcNow;

        var session = await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);

        if (session is null || !session.IsActive(now)) return null;

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == session.AccountId, ct);

        // Re-check status on every request. An account suspended mid-session must lose
        // access immediately, not at the end of an eight-hour token.
        return account is { Status: AccountStatus.Approved } ? account : null;
    }

    // ── Approval ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserAccount>> PendingAsync(string tenantId, CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == AccountStatus.Pending)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task<AuthOutcome> ApproveAsync(
        ClinicianIdentity approver, string accountId,
        string? linkedDoctorId, string? linkedPatientId, string? note,
        CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => a.Id == accountId && a.TenantId == approver.TenantId, ct);

        if (account is null) return new AuthOutcome(false, null, "Registration not found.", null);

        try
        {
            account.Approve(approver.DoctorId, linkedDoctorId, linkedPatientId, note);
        }
        catch (InvalidOperationException ex)
        {
            return new AuthOutcome(false, null, ex.Message, account);
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(approver.TenantId, approver.DoctorId, ActorKind.Admin, "ACCOUNT_APPROVED",
            "account", account.Id, linkedPatientId,
            detail: new { account.Email, Role = account.Role.ToString(), linkedDoctorId, linkedPatientId, note }, ct: ct);

        logger.LogInformation("Account {Email} approved by {Approver}", account.Email, approver.DoctorId);
        return new AuthOutcome(true, null, null, account);
    }

    public async Task<AuthOutcome> RejectAsync(
        ClinicianIdentity approver, string accountId, string? note, CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => a.Id == accountId && a.TenantId == approver.TenantId, ct);

        if (account is null) return new AuthOutcome(false, null, "Registration not found.", null);

        account.Reject(approver.DoctorId, note);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(approver.TenantId, approver.DoctorId, ActorKind.Admin, "ACCOUNT_REJECTED",
            "account", account.Id, outcome: "rejected", detail: new { account.Email, note }, ct: ct);

        return new AuthOutcome(true, null, null, account);
    }

    /// <summary>
    /// Suspending revokes every live session as well as barring future sign-ins.
    /// Leaving existing sessions alive would make suspension take up to eight hours.
    /// </summary>
    public async Task SetStatusAsync(
        ClinicianIdentity actor, string accountId, AccountStatus status, CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(
            a => a.Id == accountId && a.TenantId == actor.TenantId, ct);

        if (account is null) return;

        account.Status = status;

        if (status is not AccountStatus.Approved)
        {
            var live = await db.Sessions.Where(s => s.AccountId == accountId && s.RevokedAt == null).ToListAsync(ct);
            foreach (var session in live) session.RevokedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actor.TenantId, actor.DoctorId, ActorKind.Admin, "ACCOUNT_STATUS_CHANGED",
            "account", accountId, detail: new { status = status.ToString() }, ct: ct);
    }

    // ── Crypto ───────────────────────────────────────────────────────────────

    private static string Hash(string password, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32));

    private static bool Verify(string password, UserAccount account)
    {
        var salt = Convert.FromBase64String(account.PasswordSalt);
        var candidate = Hash(password, salt);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(account.PasswordHash));
    }

    private static string IssueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool IsPlausibleEmail(string email) =>
        email.Length is > 4 and < 254
        && email.Count(c => c == '@') == 1
        && email.IndexOf('@') > 0
        && email.LastIndexOf('.') > email.IndexOf('@') + 1
        && !email.EndsWith('.');
}
