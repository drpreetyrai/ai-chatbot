import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api, type Note, type Segment, type Span } from '../lib/api'
import { AIBlock, AIChip, ProvenanceLink, fmt } from '../components/AIBlock'

/**
 * Note Review & Sign — the trust screen (wireframe S-04).
 *
 * The most important screen in the product. If review is fast and provenance is
 * obvious, clinicians sign. If it isn't, they abandon, and the entire value
 * chain collapses.
 *
 * Nothing is committed until signature: the prescription, the orders, the
 * calendar event and the WhatsApp message are checkboxes on THIS screen. One
 * signature updates five systems — and one place stops all five.
 */
export function NoteReview() {
  const { id = '' } = useParams()
  const navigate = useNavigate()

  const [note, setNote] = useState<Note | null>(null)
  const [transcript, setTranscript] = useState<Segment[]>([])
  const [selected, setSelected] = useState<Span | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [draftText, setDraftText] = useState('')
  const [signing, setSigning] = useState(false)
  const [signed, setSigned] = useState<{ queuedActions: string[]; skippedActions: string[] } | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function load() {
    const n = await api.get<Note>(`/v1/notes/${id}`)
    setNote(n)
    if (n.status === 'Signed') setSigned((s) => s ?? { queuedActions: [], skippedActions: [] })
  }

  useEffect(() => {
    load().catch((e) => setError((e as Error).message))
  }, [id])

  useEffect(() => {
    if (!note) return
    api
      .get<Segment[]>(`/v1/encounters/${note.encounterId}/transcript`)
      .then(setTranscript)
      .catch(() => {})
  }, [note?.id])

  // ⌘↵ signs. The 40-second target is only reachable keyboard-first.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'Enter' && note?.signable && !signed) {
        e.preventDefault()
        sign()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [note?.signable, signed])

  async function decide(span: Span, decision: 'accept' | 'reject') {
    await api.post(`/v1/notes/${id}/spans/${span.id}/${decision}`)
    await load()
  }

  async function saveEdit(span: Span) {
    await api.patch(`/v1/notes/${id}/spans/${span.id}`, { text: draftText })
    setEditing(null)
    await load()
  }

  async function toggleAction(actionId: string, enabled: boolean) {
    try {
      await api.patch(`/v1/notes/${id}/actions/${actionId}`, { enabled })
      await load()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function sign() {
    setSigning(true)
    setError(null)
    try {
      const result = await api.post<{ queuedActions: string[]; skippedActions: string[] }>(`/v1/notes/${id}/sign`)
      setSigned(result)
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSigning(false)
    }
  }

  async function report(span: Span) {
    await api.post('/v1/feedback', {
      surface: 'note_span',
      targetId: span.id,
      reason: 'clinician_reported',
      detail: span.text,
    })
    setError('Reported. This goes to clinical review and into the evaluation set.')
  }

  if (error && !note) return <Message text={error} tone="danger" />
  if (!note) return <Message text="Loading…" />

  const isSigned = note.status === 'Signed'

  return (
    <div className="flex h-full min-h-0">
      <div className="flex-1 min-w-0 overflow-auto">
        {/* Header — the state of the artefact, unmistakably. */}
        <div className="px-6 py-3 hairline-b flex items-center gap-3 flex-wrap" style={{ background: 'var(--color-surface)' }}>
          <button onClick={() => navigate('/today')} className="micro">
            ‹ Back
          </button>
          <span className="font-semibold">{note.patient.name}</span>
          <span style={{ color: 'var(--color-muted)' }}>
            {note.patient.age} {note.patient.sex} · MRN {note.patient.mrn}
          </span>

          {note.patient.flags
            .filter((f) => f.severity === 'Severe')
            .map((f) => (
              <span key={f.label} className="micro px-1.5 py-0.5 rounded-[6px]" style={{ color: 'var(--color-dangertext)', border: '1px solid var(--color-danger)' }}>
                ▲ {f.label}
              </span>
            ))}

          <div className="flex-1" />

          {isSigned ? (
            <span className="micro" style={{ color: 'var(--color-ok)' }}>
              ✓ SIGNED {note.signedAt && new Date(note.signedAt).toLocaleTimeString()} · {note.signedBy}
            </span>
          ) : (
            <span className="micro" style={{ color: 'var(--color-minttext)' }}>
              ▮ AI DRAFT — UNSIGNED
            </span>
          )}
        </div>

        <div className="px-6 py-2 hairline-b micro flex gap-4 flex-wrap" style={{ color: 'var(--color-faint)' }}>
          <span>Template: {note.templateId}</span>
          <span>Model: {note.modelVersion}</span>
          <span>Prompt: {note.promptVersion}</span>
          {isSigned && <span>Edit distance {note.editDistance}%</span>}
          <span className="ml-auto">⌘↵ sign</span>
        </div>

        {note.draftUnavailable && (
          <div className="mx-6 mt-4 p-3 rounded-[10px] text-[13px]" style={{ background: 'color-mix(in srgb, var(--color-review) 10%, transparent)', color: 'var(--color-reviewtext)' }}>
            Draft unavailable — the model could not be reached. The transcript and extracted
            findings are still here; dictate or type the note manually.
          </div>
        )}

        {/* THE NOTE. Serif body — it reads as a document, which changes how
            carefully people read it. */}
        <div className="px-6 py-5 max-w-3xl">
          {note.sections.map((section) => (
            <section key={section.id} className="mb-6">
              <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
                {section.kind}
              </h2>

              {section.spans.map((span) => (
                <AIBlock
                  key={span.id}
                  state={isSigned ? 'signed' : 'draft'}
                  confidence={span.confidence}
                  flagReason={span.flagReason}
                  provenance={
                    <ProvenanceLink
                      startMs={span.transcriptStartMs}
                      endMs={span.transcriptEndMs}
                      onOpen={() => setSelected(span)}
                    />
                  }
                  accepted={span.acceptedByHuman}
                  onAccept={() => decide(span, 'accept')}
                  onRewrite={() => {
                    setEditing(span.id)
                    setDraftText(span.text)
                  }}
                  onReport={isSigned ? undefined : () => report(span)}
                >
                  {editing === span.id ? (
                    <div>
                      <textarea
                        autoFocus
                        value={draftText}
                        onChange={(e) => setDraftText(e.target.value)}
                        className="note-body w-full p-2 rounded-[8px] hairline bg-transparent"
                        rows={3}
                      />
                      <div className="flex gap-2 mt-1">
                        <button onClick={() => saveEdit(span)} className="micro px-2 py-0.5 rounded-[6px] text-white" style={{ background: 'var(--color-pulse)' }}>
                          Save
                        </button>
                        <button onClick={() => setEditing(null)} className="micro px-2 py-0.5 rounded-[6px] hairline">
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : (
                    <p
                      className="note-body"
                      style={span.text.startsWith('⚠') ? { color: 'var(--color-dangertext)' } : undefined}
                    >
                      {span.text}
                      {span.editedByHuman && (
                        <span className="micro ml-2" style={{ color: 'var(--color-faint)' }}>
                          edited
                        </span>
                      )}
                    </p>
                  )}
                </AIBlock>
              ))}
            </section>
          ))}

          {note.codes.length > 0 && (
            <section className="mb-6">
              <h2 className="micro mb-2 flex items-center gap-2" style={{ color: 'var(--color-faint)' }}>
                Coding <AIChip label="AI" />
              </h2>
              <div className="flex gap-1.5 flex-wrap">
                {note.codes.map((c) => (
                  <span key={c.code} className="text-[12px] px-2 py-0.5 rounded-full hairline" title={c.display}>
                    {c.code}
                  </span>
                ))}
              </div>
            </section>
          )}
        </div>
      </div>

      {/* RIGHT RAIL — provenance, then the commit point. */}
      <aside className="w-[320px] shrink-0 overflow-auto hidden xl:block" style={{ borderLeft: '1px solid var(--color-hairline)', background: 'var(--color-surface)' }}>
        <div className="p-4 hairline-b">
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Provenance
          </h2>
          {selected ? (
            <>
              <p className="text-[13px] mb-2">“{selected.text}”</p>
              <p className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
                Transcript {fmt(selected.transcriptStartMs ?? 0)} → {fmt(selected.transcriptEndMs ?? 0)}
              </p>
              <div className="rounded-[8px] p-2 text-[12px] leading-5" style={{ background: 'var(--color-sunken)' }}>
                {transcript
                  .filter(
                    (s) =>
                      s.endMs >= (selected.transcriptStartMs ?? 0) &&
                      s.startMs <= (selected.transcriptEndMs ?? 0),
                  )
                  .map((s) => (
                    <p key={s.id} className="mb-1">
                      <span className="micro mr-1" style={{ color: 'var(--color-faint)' }}>
                        {s.speaker}
                      </span>
                      {s.text}
                    </p>
                  )) || <span style={{ color: 'var(--color-faint)' }}>No matching transcript window.</span>}
              </div>
            </>
          ) : (
            <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
              Click “play” under any line to see exactly what was said.
            </p>
          )}
        </div>

        {/* ATTACHED ACTIONS — these fire on signature, and only on signature. */}
        <div className="p-4">
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Attached actions — fire on signature
          </h2>

          {note.attachedActions.map((a) => (
            <label key={a.id} className="flex items-start gap-2 mb-2 text-[13px]">
              <input
                type="checkbox"
                checked={a.enabled && !a.blockedReason}
                disabled={isSigned || !!a.blockedReason}
                onChange={(e) => toggleAction(a.id, e.target.checked)}
                className="mt-0.5"
              />
              <span className="flex-1">
                {a.description}
                {a.blockedReason && (
                  <span className="block micro mt-0.5" style={{ color: 'var(--color-dangertext)' }}>
                    ⛔ {a.blockedReason}
                  </span>
                )}
              </span>
            </label>
          ))}

          <div className="mt-4 pt-3 hairline-t">
            {signed ? (
              <div className="text-[13px]">
                <p className="mb-2" style={{ color: 'var(--color-ok)' }}>
                  ✓ Signed and released.
                </p>
                {signed.queuedActions.length > 0 && (
                  <p className="micro mb-1" style={{ color: 'var(--color-faint)' }}>
                    Queued: {signed.queuedActions.join(', ')}
                  </p>
                )}
                {signed.skippedActions.length > 0 && (
                  <p className="micro" style={{ color: 'var(--color-reviewtext)' }}>
                    Skipped: {signed.skippedActions.join(' · ')}
                  </p>
                )}
                <button onClick={() => navigate('/admin')} className="micro underline mt-2">
                  See it in the outbox →
                </button>
              </div>
            ) : (
              <>
                {note.blocker && (
                  <p className="text-[12px] mb-2" style={{ color: 'var(--color-reviewtext)' }}>
                    ▲ {note.blocker}
                  </p>
                )}
                <button
                  onClick={sign}
                  disabled={!note.signable || signing}
                  className="w-full py-2 rounded-[10px] text-[14px] text-white disabled:opacity-40"
                  style={{ background: 'var(--color-pulse)' }}
                >
                  {signing ? 'Signing…' : 'Review & sign'}
                </button>
                <p className="micro mt-2 text-center" style={{ color: 'var(--color-faint)' }}>
                  Nothing leaves this screen until you sign.
                </p>
              </>
            )}
          </div>

          {error && (
            <p className="mt-3 text-[12px]" style={{ color: 'var(--color-dangertext)' }}>
              {error}
            </p>
          )}
        </div>
      </aside>
    </div>
  )
}

function Message({ text, tone }: { text: string; tone?: 'danger' }) {
  return (
    <p className="p-6 text-[13px]" style={{ color: tone === 'danger' ? 'var(--color-danger)' : 'var(--color-faint)' }}>
      {text}
    </p>
  )
}
