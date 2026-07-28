using Aria.Agents.Middleware;
using Aria.Agents.Models;
using Aria.Agents.Prompts;
using Aria.Api.Auth;
using Aria.Domain;
using Aria.Domain.Governance;
using Aria.Infrastructure.Audit;
using Aria.Infrastructure.Persistence;
using Aria.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Aria.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/admin");

        // ── The audit log, written for the auditor (wireframe S-10). ──
        group.MapGet("/audit", async (
            int? take, string? patientId, HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not (UserRole.Admin or UserRole.Auditor or UserRole.ClinicalSafetyOfficer or UserRole.Clinician))
                return me.Denied("read the audit log");

            var query = db.AuditLog.AsNoTracking().Where(a => a.TenantId == me.TenantId);

            // A clinician may audit their own actions; only an auditor sees everyone's.
            if (me.Role is UserRole.Clinician) query = query.Where(a => a.ActorId == me.DoctorId);
            if (patientId is not null) query = query.Where(a => a.PatientId == patientId);

            var rows = await query
                .OrderByDescending(a => a.Timestamp)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .ToListAsync(ct);

            return Results.Ok(rows.Select(a => new
            {
                a.Id, a.Timestamp, a.ActorId, ActorKind = a.ActorKind.ToString(), a.Action,
                a.TargetKind, a.TargetId, a.PatientId, a.ModelVersion, a.PromptVersion,
                a.HumanEdits, a.Outcome, a.DetailJson,
                RowHash = a.RowHash[..12],
            }));
        });

        // Proves the chain, and says WHERE it broke rather than just that it did.
        group.MapGet("/audit/verify", async (
            HttpContext http, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not (UserRole.Admin or UserRole.Auditor)) return me.Denied("verify the audit chain");

            var (intact, breakAt) = await audit.VerifyChainAsync(me.TenantId, ct);

            return Results.Ok(new
            {
                intact,
                breakAt,
                message = intact
                    ? "Hash chain verified. No row has been altered or removed."
                    : $"Chain broken at row {breakAt}. Every row from this point is suspect.",
            });
        });

        // ── Autonomy dials (wireframe S-10). ──
        group.MapGet("/autonomy", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var settings = await db.AutonomySettings.AsNoTracking()
                .Where(a => a.TenantId == me.TenantId)
                .ToListAsync(ct);

            var rows = settings.Select(s => new
            {
                s.Id, s.ScopeKind, s.ScopeId, s.Intent, Mode = s.Mode.ToString(),
                s.ApprovedBy, s.ExpiresAt, Immutable = false,
            }).ToList<object>();

            // Rendered non-interactive in the UI, and the API refuses to change it. Some settings
            // should be visibly impossible to change.
            rows.Add(new
            {
                Id = "immutable-red-flag",
                ScopeKind = "tenant", ScopeId = me.TenantId,
                Intent = AutonomyPolicy.RedFlagEscalationIntent,
                Mode = AutonomyMode.AlwaysHuman.ToString(),
                ApprovedBy = (string?)null, ExpiresAt = (DateTimeOffset?)null,
                Immutable = true,
            });

            return Results.Ok(rows);
        });

        group.MapPut("/autonomy/{intent}", async (
            string intent, AutonomyChangeRequest request, HttpContext http,
            AriaDbContext db, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("change configuration");

            if (!Enum.TryParse<AutonomyMode>(request.Mode, true, out var mode))
                return Results.BadRequest(new { error = $"Unknown mode '{request.Mode}'." });

            try
            {
                AutonomyPolicy.GuardChange(intent, mode);
            }
            catch (AutonomyImmutableException ex)
            {
                await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Admin,
                    AuditActions.AutonomyRefused, "autonomy", intent, outcome: "refused",
                    detail: new { requested = request.Mode, reason = "permanently human-in-the-loop" }, ct: ct);

                // 422 rather than 403: the request is well-formed and the caller is authorised —
                // the change itself is simply not a thing this system permits.
                return Results.UnprocessableEntity(new { error = ex.Message, intent = ex.Intent });
            }

            var setting = await db.AutonomySettings.FirstOrDefaultAsync(
                a => a.TenantId == me.TenantId && a.Intent == intent && a.ScopeId == request.ScopeId, ct);

            if (setting is null)
            {
                setting = new AutonomySetting
                {
                    Id = Guid.NewGuid().ToString("n")[..12],
                    TenantId = me.TenantId, ScopeKind = request.ScopeKind, ScopeId = request.ScopeId,
                    Intent = intent, Mode = mode,
                };
                db.AutonomySettings.Add(setting);
            }
            else
            {
                setting.Mode = mode;
            }

            // Promotions are time-boxed and auto-revert. Demotion toward Draft is never gated —
            // making something safer must not require approval (plan.md §10.4).
            setting.ApprovedBy = mode is AutonomyMode.Auto ? me.DoctorId : null;
            setting.ExpiresAt = mode is AutonomyMode.Auto ? DateTimeOffset.UtcNow.AddDays(180) : null;

            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Admin,
                AuditActions.AutonomyChanged, "autonomy", intent,
                detail: new { mode = mode.ToString(), setting.ScopeKind, setting.ScopeId, setting.ExpiresAt }, ct: ct);

            return Results.Ok(new { setting.Intent, Mode = setting.Mode.ToString(), setting.ExpiresAt });
        });

        // ── Which services are live, which are stubs. The operator should never have to guess. ──
        group.MapGet("/integrations", (HttpContext http, AriaOptions options, IModelRouter router,
            Aria.Agents.Safety.IPromptShield shield) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();

            return Results.Ok(new[]
            {
                new { name = "Model plane",   live = router.IsLive,                    detail = router.ModeDescription },
                new { name = "Prompt shield", live = options.ContentSafety.IsConfigured, detail = shield.Name },
                new { name = "Speech",        live = options.Speech.IsConfigured,      detail = options.Speech.IsConfigured ? "Azure AI Speech" : "scripted consultation (Demo Mode)" },
                new { name = "Clinical NLP",  live = options.Language.IsConfigured,    detail = options.Language.IsConfigured ? "Text Analytics for Health" : "built-in clinical lexicon" },
                new { name = "Retrieval",     live = options.Search.IsConfigured,      detail = options.Search.IsConfigured ? "Azure AI Search" : "in-process hybrid index" },
                new { name = "Calendar",      live = options.Google.IsConfigured,      detail = options.Google.IsConfigured ? "Google Calendar" : "in-memory clinic week" },
                new { name = "Messaging",     live = options.WhatsApp.IsConfigured,    detail = options.WhatsApp.IsConfigured ? "WhatsApp Business" : "simulated thread" },
                new { name = "EHR",           live = options.Fhir.IsConfigured,        detail = options.Fhir.IsConfigured ? "FHIR R4" : "local FHIR store" },
                new { name = "Identity",      live = options.Identity.IsConfigured,    detail = options.Identity.IsConfigured ? "Microsoft Entra ID" : "local dev sign-in" },
            });
        });

        // ── Kill switches. Flipping one degrades to the manual path, never to an error. ──
        group.MapGet("/features", (HttpContext http, IFeatureSwitches features) =>
            http.TryIdentity(out _) ? Results.Ok(features.Snapshot()) : Results.Unauthorized());

        group.MapPut("/features/{key}", async (
            string key, FeatureToggleRequest request, HttpContext http,
            IFeatureSwitches features, IAuditService audit, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();
            if (me.Role is not UserRole.Admin) return me.Denied("change configuration");

            features.Set(key, request.Enabled);

            await audit.WriteAsync(me.TenantId, me.DoctorId, ActorKind.Admin,
                "FEATURE_TOGGLED", "feature", key, detail: new { request.Enabled }, ct: ct);

            return Results.Ok(new { key, request.Enabled });
        });

        // ── Model cards. An agent without a current one cannot be enabled in production. ──
        group.MapGet("/model-cards", (HttpContext http, IPromptRegistry prompts, IModelRouter router) =>
        {
            if (!http.TryIdentity(out _)) return Results.Unauthorized();

            return Results.Ok(prompts.All().Select(p => new
            {
                AgentId = p.Id,
                PromptVersion = p.Version,
                PromptHash = p.Hash,
                Reference = p.Reference,
                ModelPlane = router.ModeDescription,
                HumanOversight = p.Id switch
                {
                    "aria-scribe"            => "Clinician signs every note. Nothing reaches the record unsigned.",
                    "aria-patient-comms"     => "Human approves every message unless an autonomy dial permits otherwise.",
                    "aria-clinical-evidence" => "Decision support only. Uncited items are deleted before render.",
                    "aria-chart-qa"          => "Read-only. Every claim carries a resolvable citation.",
                    _                        => "Read-only, no external effect.",
                },
            }));
        });

        // The outbox, visible. This is how you prove the write barrier: nothing here has a null
        // note id, and nothing appears before a signature.
        group.MapGet("/outbox", async (HttpContext http, AriaDbContext db, CancellationToken ct) =>
        {
            if (!http.TryIdentity(out var me)) return Results.Unauthorized();

            var items = await db.Outbox.AsNoTracking()
                .Where(o => o.TenantId == me.TenantId)
                .OrderByDescending(o => o.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            return Results.Ok(items.Select(o => new
            {
                o.Id, o.NoteId, ActionType = o.ActionType.ToString(), Status = o.Status.ToString(),
                o.Attempts, o.LastError, o.ExternalRef, o.CreatedAt, o.VisibleAfter, o.CompletedAt,
                o.IdempotencyKey,
            }));
        });
    }
}

public sealed record AutonomyChangeRequest(string Mode, string ScopeKind, string ScopeId);
public sealed record FeatureToggleRequest(bool Enabled);
