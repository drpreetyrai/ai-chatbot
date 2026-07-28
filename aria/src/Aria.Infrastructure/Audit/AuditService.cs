using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Domain;
using Aria.Domain.Governance;
using Aria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aria.Infrastructure.Audit;

public interface IAuditService
{
    Task<AuditEntry> WriteAsync(
        string tenantId, string actorId, ActorKind actorKind, string action,
        string? targetKind = null, string? targetId = null, string? patientId = null,
        string? modelVersion = null, string? promptVersion = null, int? humanEdits = null,
        string outcome = "ok", object? detail = null, CancellationToken ct = default);

    Task<(bool Intact, string? BreakAt)> VerifyChainAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Append-only, hash-chained audit log.
///
/// Each row carries the SHA-256 of the row before it, so removing or editing any historical row
/// breaks the chain visibly at that point. This is what makes the nightly export something a
/// regulator can rely on rather than a text file we assert is complete.
///
/// The application's database role has no UPDATE or DELETE grant on this table in production.
/// </summary>
public sealed class AuditService(AriaDbContext db) : IAuditService
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Serialises chain appends. The chain is only meaningful if writes are ordered.</summary>
    private static readonly SemaphoreSlim ChainLock = new(1, 1);

    public async Task<AuditEntry> WriteAsync(
        string tenantId, string actorId, ActorKind actorKind, string action,
        string? targetKind = null, string? targetId = null, string? patientId = null,
        string? modelVersion = null, string? promptVersion = null, int? humanEdits = null,
        string outcome = "ok", object? detail = null, CancellationToken ct = default)
    {
        await ChainLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = await db.AuditLog
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
                .Select(a => a.RowHash)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            var entry = new AuditEntry
            {
                Id = Guid.NewGuid().ToString("n"),
                TenantId = tenantId,
                ActorId = actorId,
                ActorKind = actorKind,
                Action = action,
                TargetKind = targetKind,
                TargetId = targetId,
                PatientId = patientId,
                ModelVersion = modelVersion,
                PromptVersion = promptVersion,
                HumanEdits = humanEdits,
                Outcome = outcome,
                DetailJson = detail is null ? "{}" : JsonSerializer.Serialize(detail),
                PrevHash = previous ?? GenesisHash,
            };

            entry.RowHash = Hash(entry.CanonicalPayload());

            db.AuditLog.Add(entry);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entry;
        }
        finally { ChainLock.Release(); }
    }

    /// <summary>
    /// Recomputes the whole chain. Returns the id of the first row that does not verify, which is
    /// exactly what an auditor asks for: not "is it broken" but "where".
    /// </summary>
    public async Task<(bool Intact, string? BreakAt)> VerifyChainAsync(string tenantId, CancellationToken ct = default)
    {
        var rows = await db.AuditLog.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var expectedPrev = GenesisHash;
        foreach (var row in rows)
        {
            if (row.PrevHash != expectedPrev) return (false, row.Id);
            if (Hash(row.CanonicalPayload()) != row.RowHash) return (false, row.Id);
            expectedPrev = row.RowHash;
        }

        return (true, null);
    }

    private static string Hash(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
