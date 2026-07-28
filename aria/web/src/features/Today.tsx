import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, type Identity, type QueueEntry } from '../lib/api'
import { AIChip } from '../components/AIBlock'

type DraftRow = { noteId: string; patient: string; lowConfidence: number; createdAt: string }

/**
 * Today — the command centre (wireframe S-02).
 *
 * It answers three questions in under two seconds: who's next, what's blocked on
 * me, what's on fire. Action Required sits ABOVE Next Up, because unsigned work
 * is the only thing that compounds — the schedule will announce itself anyway.
 */
export function Today({ me }: { me: Identity }) {
  const [queue, setQueue] = useState<QueueEntry[]>([])
  const [drafts, setDrafts] = useState<DraftRow[]>([])
  const [showDemo, setShowDemo] = useState(() => !localStorage.getItem('aria.demoSeen'))
  const navigate = useNavigate()

  useEffect(() => {
    api.get<QueueEntry[]>('/v1/encounters/today').then(setQueue).catch(() => {})
  }, [])

  // Unsigned drafts across today's patients — the "blocked on me" list.
  useEffect(() => {
    if (queue.length === 0) return
    Promise.all(
      queue.map(async (e) => {
        try {
          const timeline = await api.get<{ notes: { id: string; status: string; draftCreatedAt: string }[] }>(
            `/v1/patients/${e.patient.id}/timeline`,
          )
          return timeline.notes
            .filter((n) => n.status === 'Draft')
            .map((n) => ({ noteId: n.id, patient: e.patient.name, lowConfidence: 0, createdAt: n.draftCreatedAt }))
        } catch {
          return []
        }
      }),
    ).then((rows) => {
      // De-duplicate by note id. A patient with two encounters today appears twice in
      // the queue, so their timeline is fetched twice and the same draft comes back
      // twice — which React renders as duplicate keys and may drop silently.
      const byNote = new Map<string, DraftRow>()
      for (const row of rows.flat()) byNote.set(row.noteId, row)
      setDrafts([...byNote.values()])
    })
  }, [queue])

  const now = queue.find((e) => e.state === 'CheckedIn' || e.state === 'Recording')
  const next = queue.filter((e) => e !== now)

  // "Start a walk-in" from wireframe S-02. A scheduled encounter cannot begin
  // capture — the state machine requires a check-in first — so without this the
  // only startable encounter is whichever one the seed happened to check in.
  async function startWalkIn(patientId: string) {
    const created = await api.post<{ id: string }>('/v1/encounters', {
      patientId,
      chiefComplaint: 'Walk-in',
      room: 'Room 1',
    })
    navigate(`/encounter/${created.id}`)
  }

  return (
    <div className="p-6 max-w-5xl">
      <header className="flex items-baseline justify-between mb-6 flex-wrap gap-2">
        <div>
          <h1 className="text-[20px] font-semibold">
            {new Date().toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })}
          </h1>
          <p className="micro" style={{ color: 'var(--color-faint)' }}>
            {me.department} · Northbridge
          </p>
        </div>
      </header>

      {/* First run: a real 90-second walkthrough, not a video (plan.md §14.1). */}
      {showDemo && (
        <div className="rounded-[14px] hairline p-4 mb-6" style={{ background: 'var(--color-surface)' }}>
          <h2 className="text-[15px] font-semibold mb-1">▸ Try Aria in 90 seconds — with a demo patient, not a real one</h2>
          <p className="text-[13px] mb-3" style={{ color: 'var(--color-muted)' }}>
            We'll play a recorded consultation. You'll watch the note write itself, see the allergy
            conflict fire while the patient is still in the room, then review and sign it. Every
            guardrail is real; only the audio is pre-recorded.
          </p>
          <div className="flex gap-2">
            <button
              onClick={() => {
                localStorage.setItem('aria.demoSeen', '1')
                navigate('/encounter/enc-john')
              }}
              className="px-3 py-1.5 rounded-[8px] text-[13px] text-white"
              style={{ background: 'var(--color-pulse)' }}
            >
              Start the demo
            </button>
            <button
              onClick={() => {
                localStorage.setItem('aria.demoSeen', '1')
                setShowDemo(false)
              }}
              className="px-3 py-1.5 rounded-[8px] text-[13px] hairline"
            >
              Skip — I'll learn as I go
            </button>
          </div>
        </div>
      )}

      {/* NOW */}
      {now && (
        <section className="mb-6">
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Now
          </h2>
          <div className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
            <div className="flex items-start justify-between gap-4 flex-wrap">
              <div>
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-[16px] font-semibold">{now.patient.name}</span>
                  <span style={{ color: 'var(--color-muted)' }}>
                    {now.patient.age} {now.patient.sex} · MRN {now.patient.mrn}
                  </span>
                  {now.room && <span className="micro hairline px-1.5 py-0.5 rounded-[6px]">{now.room}</span>}
                </div>
                <p className="mt-1 text-[13px]" style={{ color: 'var(--color-muted)' }}>
                  {now.chiefComplaint}
                </p>
                <div className="mt-2 flex gap-1.5 flex-wrap">
                  {now.patient.flags.map((f) => (
                    <span
                      key={f.label}
                      className="micro px-1.5 py-0.5 rounded-[6px] hairline"
                      style={
                        f.severity === 'Severe'
                          ? { color: 'var(--color-dangertext)', borderColor: 'var(--color-danger)' }
                          : undefined
                      }
                    >
                      {f.severity === 'Severe' && '▲ '}
                      {f.label}
                    </span>
                  ))}
                </div>
              </div>
              <Link
                to={`/encounter/${now.id}`}
                className="px-3 py-1.5 rounded-[8px] text-[13px] text-white shrink-0"
                style={{ background: 'var(--color-pulse)' }}
              >
                ◉ Start encounter
              </Link>
            </div>
          </div>
        </section>
      )}

      {/* ACTION REQUIRED — above Next Up, always. */}
      <section className="mb-6">
        <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
          Action required {drafts.length > 0 && `· ${drafts.length}`}
        </h2>
        <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
          {drafts.length === 0 ? (
            <p className="p-4 text-[13px]" style={{ color: 'var(--color-faint)' }}>
              Nothing waiting on you. Unsigned notes and message approvals appear here.
            </p>
          ) : (
            drafts.map((d) => (
              <Link
                key={d.noteId}
                to={`/note/${d.noteId}`}
                className="flex items-center gap-3 px-4 py-3 hairline-b last:border-b-0 hover:bg-[var(--color-sunken)]"
              >
                <AIChip />
                <span className="text-[13px] flex-1">
                  {d.patient} · consultation note
                </span>
                {/* An estimated time converts "later" into "now" — the single biggest
                    driver of note lag (wireframe S-02). */}
                <span className="micro px-2 py-0.5 rounded-[6px]" style={{ background: 'var(--color-sunken)' }}>
                  Review · 40s
                </span>
              </Link>
            ))
          )}
        </div>
      </section>

      {/* NEXT UP */}
      <section>
        <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
          Next up
        </h2>
        <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
          {next.length === 0 ? (
            <p className="p-4 text-[13px]" style={{ color: 'var(--color-faint)' }}>
              No patients checked in. <Link to="/schedule" className="underline">Open the schedule →</Link>
            </p>
          ) : (
            next.map((e) => (
              <div key={e.id} className="flex items-center gap-3 px-4 py-3 hairline-b last:border-b-0">
                <span className="text-[13px] flex-1">
                  {e.patient.name}
                  <span style={{ color: 'var(--color-faint)' }}> · {e.chiefComplaint}</span>
                </span>
                <button onClick={() => startWalkIn(e.patient.id)} className="micro underline">
                  Check in &amp; start
                </button>
                <Link to={`/patients/${e.patient.id}`} className="micro underline">
                  Chart
                </Link>
              </div>
            ))
          )}
        </div>
      </section>
    </div>
  )
}
