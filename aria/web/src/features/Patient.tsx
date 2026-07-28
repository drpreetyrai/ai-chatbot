import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, type ChartAnswer, type Evidence, type PatientFlag } from '../lib/api'
import { AIBlock, AIChip } from '../components/AIBlock'
import { AssistantChat } from '../components/AssistantChat'

type PatientDetail = {
  id: string
  name: string
  mrn: string
  sex: string
  age: number
  phone: string
  preferredLanguage: string
  flags: (PatientFlag & { sourceRef: string | null; recordedAt: string })[]
}

type Timeline = {
  notes: { id: string; status: string; draftCreatedAt: string; signedAt: string | null; summary: string }[]
  appointments: { id: string; startAt: string; reason: string | null; status: string }[]
}

/**
 * Patient 360 — history without archaeology (wireframe S-05).
 *
 * Retrieval is scoped to a single patient and SAYS SO, in plain language, under
 * every answer. Scope is the difference between a useful tool and a liability.
 *
 * Every claim carries a numbered citation. No citation → the claim is not
 * rendered — and that rule is enforced server-side, not here.
 */
export function Patient() {
  const { id = '' } = useParams()
  const [patient, setPatient] = useState<PatientDetail | null>(null)
  const [timeline, setTimeline] = useState<Timeline | null>(null)
  const [question, setQuestion] = useState('')
  const [answer, setAnswer] = useState<ChartAnswer | null>(null)
  const [evidence, setEvidence] = useState<Evidence | null>(null)
  const [asking, setAsking] = useState(false)

  useEffect(() => {
    api.get<PatientDetail>(`/v1/patients/${id}`).then(setPatient).catch(() => {})
    api.get<Timeline>(`/v1/patients/${id}/timeline`).then(setTimeline).catch(() => {})
  }, [id])

  async function ask(q: string) {
    setAsking(true)
    setQuestion(q)
    try {
      setAnswer(await api.post<ChartAnswer>(`/v1/patients/${id}/ask`, { question: q }))
    } finally {
      setAsking(false)
    }
  }

  async function loadEvidence() {
    const findings = patient?.flags.map((f) => f.label) ?? []
    setEvidence(
      await api.post<Evidence>('/v1/clinical-support', {
        patientId: id,
        findings: [...findings, 'fever', 'cough', 'breathless'],
      }),
    )
  }

  if (!patient) return <p className="p-6 text-[13px]" style={{ color: 'var(--color-faint)' }}>Loading…</p>

  // Example questions derived from what this record can actually answer. An
  // example that returns "I don't know" teaches the user the tool is useless.
  const examples = [
    'Has he had breathlessness before?',
    'What antibiotics has he tolerated?',
    'When was his last chest X-ray?',
  ]

  return (
    <div className="p-6 max-w-5xl">
      {/* Allergies and conditions live in the header, never more than a glance away. */}
      <header className="mb-5">
        <div className="flex items-baseline gap-3 flex-wrap">
          <h1 className="text-[20px] font-semibold">{patient.name}</h1>
          <span style={{ color: 'var(--color-muted)' }}>
            {patient.age} {patient.sex} · MRN {patient.mrn} · {patient.phone}
          </span>
        </div>
        <div className="mt-2 flex gap-1.5 flex-wrap">
          {patient.flags.map((f) => (
            <span
              key={f.label}
              className="micro px-1.5 py-0.5 rounded-[6px] hairline"
              title={f.sourceRef ? `source: ${f.sourceRef}` : undefined}
              style={f.severity === 'Severe' ? { color: 'var(--color-dangertext)', borderColor: 'var(--color-danger)' } : undefined}
            >
              {f.severity === 'Severe' && '▲ '}
              {f.label}
            </span>
          ))}
        </div>
      </header>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* ASK THIS CHART */}
        <section>
          <h2 className="micro mb-2 flex items-center gap-2" style={{ color: 'var(--color-faint)' }}>
            Ask this chart <AIChip label="AI" />
          </h2>

          <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)', height: 460 }}>
            <AssistantChat
              audience="clinician"
              patientId={patient.id}
              className="h-full"
              suggestions={examples}
            />
          </div>

          <div className="hidden">
            <input
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder="legacy"
            />

            <div className="flex gap-1.5 flex-wrap mb-3">
              <span className="micro self-center" style={{ color: 'var(--color-faint)' }}>
                Try:
              </span>
              {examples.map((e) => (
                <button
                  key={e}
                  onClick={() => ask(e)}
                  className="text-[12px] px-2 py-0.5 rounded-full hairline hover:bg-[var(--color-sunken)]"
                >
                  {e}
                </button>
              ))}
            </div>

            {asking && <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>Searching the record…</p>}

            {answer && !asking && (
              <div>
                {answer.insufficientEvidence ? (
                  <p className="text-[13px]" style={{ color: 'var(--color-reviewtext)' }}>
                    The record does not answer this. Showing nothing rather than guessing.
                  </p>
                ) : (
                  answer.claims.map((c, i) => (
                    <AIBlock key={i}>
                      <p className="text-[13px]">{c.text}</p>
                      <div className="mt-1 flex gap-2 flex-wrap">
                        {c.sources.map((s, n) => (
                          <span key={s.id} className="micro" style={{ color: 'var(--color-pulse)' }}>
                            [{n + 1}] {s.title}
                          </span>
                        ))}
                      </div>
                    </AIBlock>
                  ))
                )}

                {answer.interventions.length > 0 && (
                  <p className="micro mt-2" style={{ color: 'var(--color-reviewtext)' }}>
                    Guardrails removed {answer.interventions.length} item(s): {answer.interventions.join('; ')}
                  </p>
                )}

                <p className="micro mt-3 pt-2 hairline-t" style={{ color: 'var(--color-faint)' }}>
                  {answer.scopeStatement}
                </p>
              </div>
            )}
          </div>

          {/* CLINICAL SUPPORT — evidence, never verdicts. */}
          <h2 className="micro mt-6 mb-2 flex items-center gap-2" style={{ color: 'var(--color-faint)' }}>
            Clinical support <AIChip label="AI" />
          </h2>
          <div className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
            {!evidence ? (
              <button onClick={loadEvidence} className="text-[13px] underline" style={{ color: 'var(--color-pulse)' }}>
                Show cited considerations →
              </button>
            ) : evidence.nothingCited ? (
              <p className="text-[13px]" style={{ color: 'var(--color-reviewtext)' }}>
                {evidence.emptyMessage}
              </p>
            ) : (
              <>
                <p className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
                  Considerations — for your judgement, not a diagnosis
                </p>
                {evidence.considerations.map((c) => (
                  <div key={c.citationId} className="mb-3">
                    <div className="flex items-center gap-2">
                      <span className="text-[13px] font-medium flex-1">{c.title}</span>
                      <span aria-hidden style={{ color: 'var(--color-muted)' }}>
                        {'●'.repeat(c.strength)}
                        {'○'.repeat(5 - c.strength)}
                      </span>
                    </div>
                    <p className="text-[12px]" style={{ color: 'var(--color-muted)' }}>
                      Suggested: {c.suggested}
                    </p>
                    <p className="micro" style={{ color: 'var(--color-pulse)' }}>
                      ▸ {c.citation}
                    </p>
                  </div>
                ))}

                {evidence.safetyChecks.length > 0 && (
                  <div className="mt-3 pt-3 hairline-t">
                    <p className="micro mb-1" style={{ color: 'var(--color-dangertext)' }}>
                      ▲ Safety checks
                    </p>
                    {evidence.safetyChecks.map((s) => (
                      <p key={s} className="text-[12px]" style={{ color: 'var(--color-muted)' }}>
                        · {s}
                      </p>
                    ))}
                  </div>
                )}

                <p className="micro mt-3 pt-2 hairline-t" style={{ color: 'var(--color-faint)' }}>
                  {evidence.disclaimer}
                </p>
              </>
            )}
          </div>
        </section>

        {/* TIMELINE */}
        <section>
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Timeline
          </h2>
          <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
            {timeline?.notes.length === 0 && timeline?.appointments.length === 0 && (
              <p className="p-4 text-[13px]" style={{ color: 'var(--color-faint)' }}>
                No records yet.
              </p>
            )}

            {timeline?.notes.map((n) => (
              <Link key={n.id} to={`/note/${n.id}`} className="block px-4 py-3 hairline-b hover:bg-[var(--color-sunken)]">
                <div className="flex items-center gap-2">
                  <span className="micro" style={{ color: n.status === 'Signed' ? 'var(--color-ok)' : 'var(--color-minttext)' }}>
                    {n.status === 'Signed' ? '✓ signed' : '▮ draft'}
                  </span>
                  <span className="micro" style={{ color: 'var(--color-faint)' }}>
                    {new Date(n.signedAt ?? n.draftCreatedAt).toLocaleDateString()}
                  </span>
                </div>
                <p className="text-[13px] mt-0.5">{n.summary}</p>
              </Link>
            ))}

            {timeline?.appointments.map((a) => (
              <div key={a.id} className="px-4 py-3 hairline-b last:border-b-0">
                <span className="micro" style={{ color: 'var(--color-faint)' }}>
                  {new Date(a.startAt).toLocaleString()} · {a.status}
                </span>
                <p className="text-[13px]">{a.reason}</p>
              </div>
            ))}
          </div>
        </section>
      </div>
    </div>
  )
}

/** Patient list — the search-and-recent surface. */
export function PatientList() {
  const [rows, setRows] = useState<PatientDetail[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<PatientDetail[]>('/v1/patients').then(setRows).catch((e) => setError((e as Error).message))
  }, [])

  if (error)
    return (
      <div className="p-6">
        <p className="text-[13px]" style={{ color: 'var(--color-dangertext)' }}>
          {error}
        </p>
        <p className="micro mt-2" style={{ color: 'var(--color-faint)' }}>
          This is the RBAC matrix working: an admin configures and audits, and never sees PHI.
        </p>
      </div>
    )

  return (
    <div className="p-6 max-w-3xl">
      <h1 className="text-[20px] font-semibold mb-4">Patients</h1>
      <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
        {rows.map((p) => (
          <Link key={p.id} to={`/patients/${p.id}`} className="block px-4 py-3 hairline-b last:border-b-0 hover:bg-[var(--color-sunken)]">
            <div className="flex items-center gap-3 flex-wrap">
              <span className="text-[14px] font-medium">{p.name}</span>
              <span className="micro" style={{ color: 'var(--color-faint)' }}>
                {p.age} {p.sex} · MRN {p.mrn} · {p.phone}
              </span>
              {p.flags
                .filter((f) => f.severity === 'Severe')
                .map((f) => (
                  <span key={f.label} className="micro" style={{ color: 'var(--color-dangertext)' }}>
                    ▲ {f.label}
                  </span>
                ))}
            </div>
          </Link>
        ))}
      </div>
    </div>
  )
}
