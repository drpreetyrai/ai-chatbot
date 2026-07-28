import { useEffect, useState } from 'react'
import { api, type AuditRow, type AutonomyRow, type Insights, type Integration, type OutboxRow } from '../lib/api'

/**
 * Admin — team, integrations, autonomy, audit (wireframe S-10).
 *
 * Two things here are deliberately different from a normal settings screen:
 *
 *   · The red-flag escalation dial is rendered NON-INTERACTIVE. Some settings
 *     should be visibly impossible to change, and the API refuses the write too.
 *
 *   · The outbox is on screen. That is how an operator proves the write barrier
 *     holds: every row carries a note id, and no row exists without a signature.
 */
export function Admin() {
  const [tab, setTab] = useState<'integrations' | 'autonomy' | 'audit' | 'outbox'>('integrations')

  const tabs = [
    ['integrations', 'Integrations'],
    ['autonomy', 'Automation autonomy'],
    ['audit', 'Audit log'],
    ['outbox', 'Outbox'],
  ] as const

  return (
    <div className="p-6 max-w-5xl">
      <h1 className="text-[20px] font-semibold mb-4">Admin</h1>

      <div className="flex gap-1 mb-5 flex-wrap">
        {tabs.map(([key, label]) => (
          <button
            key={key}
            onClick={() => setTab(key)}
            className="px-3 py-1.5 rounded-[8px] text-[13px]"
            style={{
              background: tab === key ? 'var(--color-sunken)' : 'transparent',
              fontWeight: tab === key ? 600 : 400,
            }}
          >
            {label}
          </button>
        ))}
      </div>

      {tab === 'integrations' && <Integrations />}
      {tab === 'autonomy' && <Autonomy />}
      {tab === 'audit' && <Audit />}
      {tab === 'outbox' && <Outbox />}
    </div>
  )
}

function Integrations() {
  const [rows, setRows] = useState<Integration[]>([])
  useEffect(() => {
    api.get<Integration[]>('/v1/admin/integrations').then(setRows).catch(() => {})
  }, [])

  return (
    <>
      <p className="text-[13px] mb-3" style={{ color: 'var(--color-muted)' }}>
        Every STUB below is a working local implementation. Guardrails, memory, tool authority,
        audit and evaluation run identically either way — fill in the matching section of{' '}
        <code className="mono">.env</code> to switch one to LIVE.
      </p>
      <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
        {rows.map((r) => (
          <div key={r.name} className="px-4 py-3 hairline-b last:border-b-0 flex items-center gap-3">
            <span
              className="micro w-12 shrink-0"
              style={{ color: r.live ? 'var(--color-ok)' : 'var(--color-reviewtext)' }}
            >
              {r.live ? '● LIVE' : '○ STUB'}
            </span>
            <span className="text-[13px] w-32 shrink-0">{r.name}</span>
            <span className="micro" style={{ color: 'var(--color-faint)' }}>
              {r.detail}
            </span>
          </div>
        ))}
      </div>
    </>
  )
}

function Autonomy() {
  const [rows, setRows] = useState<AutonomyRow[]>([])
  const [notice, setNotice] = useState<string | null>(null)

  const load = () => api.get<AutonomyRow[]>('/v1/admin/autonomy').then(setRows).catch(() => {})
  useEffect(() => {
    load()
  }, [])

  async function change(intent: string, mode: string, row: AutonomyRow) {
    setNotice(null)
    try {
      await api.put(`/v1/admin/autonomy/${intent}`, {
        mode,
        scopeKind: row.scopeKind,
        scopeId: row.scopeId,
      })
      await load()
    } catch (e) {
      setNotice((e as Error).message)
    }
  }

  return (
    <>
      <p className="text-[13px] mb-3" style={{ color: 'var(--color-muted)' }}>
        Autonomy is a per-department dial, not a global switch. Promotion to <b>auto</b> is
        time-boxed to 180 days and auto-reverts; demotion to <b>draft</b> is instant and never
        gated — making something safer must not require approval.
      </p>

      <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
        {rows.map((r) => (
          <div key={r.id} className="px-4 py-3 hairline-b last:border-b-0 flex items-center gap-3 flex-wrap">
            <span className="text-[13px] flex-1 min-w-[180px]">
              {r.intent.replace(/_/g, ' ')}
              <span className="micro ml-2" style={{ color: 'var(--color-faint)' }}>
                {r.scopeKind}: {r.scopeId}
              </span>
            </span>

            {r.immutable ? (
              <span className="micro px-2 py-1 rounded-[6px]" style={{ background: 'var(--color-sunken)', color: 'var(--color-faint)' }} title="Hard-wired to human. The API returns 422 on any attempt to change this.">
                ● always human · cannot be changed
              </span>
            ) : (
              <span className="flex gap-1">
                {['Draft', 'Auto'].map((mode) => (
                  <button
                    key={mode}
                    onClick={() => change(r.intent, mode, r)}
                    className="micro px-2 py-1 rounded-[6px] hairline"
                    style={
                      r.mode === mode
                        ? { background: 'var(--color-pulse)', color: '#fff', borderColor: 'transparent' }
                        : undefined
                    }
                  >
                    {mode.toLowerCase()}
                  </button>
                ))}
              </span>
            )}

            {r.expiresAt && (
              <span className="micro w-full" style={{ color: 'var(--color-faint)' }}>
                expires {new Date(r.expiresAt).toLocaleDateString()} · approved by {r.approvedBy}
              </span>
            )}
          </div>
        ))}
      </div>

      {notice && (
        <p className="mt-3 text-[13px]" style={{ color: 'var(--color-dangertext)' }}>
          {notice}
        </p>
      )}
    </>
  )
}

function Audit() {
  const [rows, setRows] = useState<AuditRow[]>([])
  const [chain, setChain] = useState<{ intact: boolean; message: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<AuditRow[]>('/v1/admin/audit?take=60').then(setRows).catch((e) => setError((e as Error).message))
    api
      .get<{ intact: boolean; message: string }>('/v1/admin/audit/verify')
      .then(setChain)
      .catch(() => {})
  }, [])

  return (
    <>
      {chain && (
        <div
          className="rounded-[10px] p-3 mb-3 text-[13px]"
          style={{
            background: chain.intact
              ? 'color-mix(in srgb, var(--color-ok) 8%, transparent)'
              : 'color-mix(in srgb, var(--color-danger) 10%, transparent)',
            color: chain.intact ? 'var(--color-ok)' : 'var(--color-danger)',
          }}
        >
          {chain.intact ? '✓ ' : '▲ '}
          {chain.message}
        </div>
      )}

      {error && (
        <p className="text-[13px] mb-3" style={{ color: 'var(--color-reviewtext)' }}>
          {error}
        </p>
      )}

      <div className="rounded-[14px] hairline overflow-x-auto" style={{ background: 'var(--color-surface)' }}>
        <table className="w-full mono text-left">
          <thead>
            <tr className="hairline-b">
              {['time', 'actor', 'action', 'target', 'model', 'edits', 'hash'].map((h) => (
                <th key={h} className="micro px-3 py-2 font-medium" style={{ color: 'var(--color-faint)' }}>
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="hairline-b last:border-b-0">
                <td className="px-3 py-1.5 whitespace-nowrap">{new Date(r.timestamp).toLocaleTimeString()}</td>
                <td className="px-3 py-1.5">{r.actorId}</td>
                <td className="px-3 py-1.5" style={{ color: r.outcome === 'refused' ? 'var(--color-danger)' : undefined }}>
                  {r.action}
                </td>
                <td className="px-3 py-1.5">{r.targetKind ?? '—'}</td>
                <td className="px-3 py-1.5">{r.modelVersion ?? '—'}</td>
                <td className="px-3 py-1.5">{r.humanEdits ?? '—'}</td>
                <td className="px-3 py-1.5" style={{ color: 'var(--color-faint)' }}>
                  {r.rowHash}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

function Outbox() {
  const [rows, setRows] = useState<OutboxRow[]>([])
  useEffect(() => {
    api.get<OutboxRow[]>('/v1/admin/outbox').then(setRows).catch(() => {})
  }, [])

  return (
    <>
      <p className="text-[13px] mb-3" style={{ color: 'var(--color-muted)' }}>
        Every external effect the product has flows through here, and every row carries the id of
        the note whose signature released it. There is no row without a signature — the database
        enforces it with a constraint, and the API assembly cannot even reference an adapter.
      </p>

      <div className="rounded-[14px] hairline overflow-x-auto" style={{ background: 'var(--color-surface)' }}>
        {rows.length === 0 ? (
          <p className="p-4 text-[13px]" style={{ color: 'var(--color-faint)' }}>
            Empty. Sign a note and five entries appear here — not before.
          </p>
        ) : (
          <table className="w-full mono text-left">
            <thead>
              <tr className="hairline-b">
                {['action', 'note', 'status', 'attempts', 'external ref'].map((h) => (
                  <th key={h} className="micro px-3 py-2 font-medium" style={{ color: 'var(--color-faint)' }}>
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id} className="hairline-b last:border-b-0">
                  <td className="px-3 py-1.5">{r.actionType}</td>
                  <td className="px-3 py-1.5">{r.noteId}</td>
                  <td
                    className="px-3 py-1.5"
                    style={{
                      color:
                        r.status === 'Succeeded'
                          ? 'var(--color-ok)'
                          : r.status === 'DeadLettered'
                            ? 'var(--color-danger)'
                            : undefined,
                    }}
                  >
                    {r.status}
                  </td>
                  <td className="px-3 py-1.5">{r.attempts}</td>
                  <td className="px-3 py-1.5" style={{ color: 'var(--color-faint)' }}>
                    {r.externalRef ?? r.lastError ?? '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}

/**
 * Insights (wireframe S-09).
 *
 * The design decision that makes the rest of the page believable: HIGH
 * acceptance is displayed as a RISK, not a win. A dashboard that only
 * celebrates is a dashboard nobody trusts.
 */
export function InsightsScreen() {
  const [data, setData] = useState<Insights | null>(null)
  useEffect(() => {
    api.get<Insights>('/v1/insights').then(setData).catch(() => {})
  }, [])

  if (!data) return <p className="p-6 text-[13px]" style={{ color: 'var(--color-faint)' }}>Loading…</p>

  const { trust, safety } = data
  const acceptance = trust.acceptanceRate

  return (
    <div className="p-6 max-w-5xl">
      <h1 className="text-[20px] font-semibold mb-1">Insights</h1>
      <p className="micro mb-5" style={{ color: 'var(--color-faint)' }}>
        Adoption, quality, trust and safety are four separate boards. A product that only watches
        adoption will eventually ship something unsafe.
      </p>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Board title="Adoption" rows={Object.entries(data.adoption)} />
        <Board title="Quality" rows={Object.entries(data.quality)} />

        <section className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
          <h2 className="micro mb-3" style={{ color: 'var(--color-faint)' }}>
            Trust
          </h2>

          <div className="mb-3">
            <div className="text-[28px] font-semibold">
              {acceptance === null ? '—' : `${Math.round(acceptance * 100)}%`}
            </div>
            <div className="micro" style={{ color: 'var(--color-faint)' }}>
              AI acceptance · healthy band {trust.healthyBand.low * 100}–{trust.healthyBand.high * 100}%
            </div>
          </div>

          {trust.overTrustAlarm && (
            <p className="text-[13px] p-2 rounded-[8px]" style={{ background: 'color-mix(in srgb, var(--color-review) 12%, transparent)', color: 'var(--color-reviewtext)' }}>
              ▲ Acceptance is above 90% — that reads as rubber-stamping, not quality. A sampling
              audit has been raised.
            </p>
          )}
          {trust.underTrustAlarm && (
            <p className="text-[13px] p-2 rounded-[8px]" style={{ background: 'color-mix(in srgb, var(--color-review) 12%, transparent)', color: 'var(--color-reviewtext)' }}>
              ▲ Acceptance is below 55% — the drafts are not good enough to be useful.
            </p>
          )}

          <dl className="mt-3 space-y-1">
            <Row label="Accepted" value={trust.acceptedCount} />
            <Row label="Rejected" value={trust.rejectedCount} />
            <Row label="Provenance opened" value={trust.provenanceOpened} />
            <Row label="Bad-suggestion reports" value={trust.badSuggestionReports} />
          </dl>
        </section>

        <section className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
          <h2 className="micro mb-3" style={{ color: 'var(--color-faint)' }}>
            Safety
          </h2>
          <dl className="space-y-1">
            <Row label="Escalations raised" value={safety.escalationsRaised} />
            <Row label="Acknowledged" value={safety.escalationsAcknowledged} />
            <Row label="Outstanding" value={safety.escalationsOutstanding} danger={safety.escalationsOutstanding > 0} />
            <Row label="SLA breaches" value={safety.slaBreaches} danger={safety.slaBreaches > 0} />
            <Row label="Median ack (s)" value={safety.medianAckSeconds ?? '—'} />
            <Row label="Uncited claims rendered" value={safety.uncitedClaimsRendered} danger={safety.uncitedClaimsRendered > 0} />
          </dl>

          {Object.keys(safety.guardrailInterventions).length > 0 && (
            <>
              <h3 className="micro mt-3 mb-1" style={{ color: 'var(--color-faint)' }}>
                Guardrail interventions
              </h3>
              <dl className="space-y-1">
                {Object.entries(safety.guardrailInterventions).map(([k, v]) => (
                  <Row key={k} label={k.replace(/_/g, ' ')} value={v} />
                ))}
              </dl>
            </>
          )}
        </section>
      </div>
    </div>
  )
}

function Board({ title, rows }: { title: string; rows: [string, number][] }) {
  return (
    <section className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
      <h2 className="micro mb-3" style={{ color: 'var(--color-faint)' }}>
        {title}
      </h2>
      <dl className="space-y-1">
        {rows.map(([k, v]) => (
          <Row key={k} label={k.replace(/([A-Z])/g, ' $1').toLowerCase()} value={v} />
        ))}
      </dl>
    </section>
  )
}

function Row({ label, value, danger }: { label: string; value: number | string; danger?: boolean }) {
  return (
    <div className="flex justify-between text-[13px]">
      <dt style={{ color: 'var(--color-muted)' }}>{label}</dt>
      <dd className="mono" style={{ color: danger ? 'var(--color-danger)' : undefined }}>
        {value}
      </dd>
    </div>
  )
}
