import { useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api, type Extraction, type Segment } from '../lib/api'
import { DOCTOR, PATIENT, startTranscription } from '../lib/speech'
import { AIChip, fmt } from '../components/AIBlock'

/**
 * Live Encounter — ambient capture (wireframe S-03).
 *
 * Designed to be GLANCEABLE, not readable. The doctor is looking at the patient;
 * this screen is for the corner of their eye. The left column is the transcript,
 * the right is extraction — chips, not prose, because chips are cheap to scan
 * and cheap to dismiss.
 *
 * The allergy conflict fires DURING the conversation, not after it. Catching it
 * while the patient is still in the room is worth more than a perfect note.
 */
export function Encounter() {
  const { id = '' } = useParams()
  const navigate = useNavigate()

  const [consent, setConsent] = useState<{ granted: boolean; capturedAt: string; retentionStatement: string } | null>(null)
  const [recording, setRecording] = useState(false)
  const [segments, setSegments] = useState<Segment[]>([])
  const [extraction, setExtraction] = useState<Extraction | null>(null)
  const [elapsed, setElapsed] = useState(0)
  const [drafting, setDrafting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [interim, setInterim] = useState('')

  const abort = useRef<AbortController | null>(null)
  const scroller = useRef<HTMLDivElement>(null)
  const mic = useRef<{ stop: () => Promise<void>; swapSpeakers: () => void } | null>(null)
  const [mode, setMode] = useState<'idle' | 'live' | 'demo'>('idle')

  // When the consultation actually began, so each line can carry a real clock time
  // rather than an offset only a developer can read.
  const [startedAt, setStartedAt] = useState<string | null>(null)

  const latestMs = segments.length ? segments[segments.length - 1].endMs : 0

  // Extraction polls on the transcript's own clock, not a wall clock, so it stays
  // in step with what has actually been heard.
  //
  // Note this does NOT gate on `recording`. It used to, which meant that whenever
  // the stream finished faster than the debounce, the final window — the one
  // carrying the medication that triggers the allergy check — was never extracted.
  // The conflict silently never appeared.
  useEffect(() => {
    if (latestMs === 0) return
    const timer = setTimeout(() => {
      api
        .get<Extraction>(`/v1/encounters/${id}/entities?uptoMs=${latestMs}`)
        .then(setExtraction)
        .catch(() => {})
    }, 400)
    return () => clearTimeout(timer)
  }, [id, latestMs])

  useEffect(() => {
    if (!recording) return
    const timer = setInterval(() => setElapsed((s) => s + 1), 1000)
    return () => clearInterval(timer)
  }, [recording])

  useEffect(() => {
    scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: 'smooth' })
  }, [segments.length])

  useEffect(() => () => abort.current?.abort(), [])

  // Rehydrate from the server on mount, so a page reload mid-consultation does not
  // present an empty transcript for a recording that is demonstrably running.
  useEffect(() => {
    api
      .get<{ startedAt: string | null; segments: Segment[] }>(`/v1/encounters/${id}`)
      .then((e) => {
        setStartedAt(e.startedAt)
        if (e.segments.length) setSegments(e.segments)
      })
      .catch(() => {})
  }, [id])

  /**
   * Corrects the doctor/patient attribution — for the lines already on screen and for
   * every line still to come.
   *
   * Persisted, not cosmetic: the note is drafted from these labels, and "the patient
   * reports no chest pain" attributed to the wrong voice is a clinical error, not a
   * display bug.
   */
  async function swapSpeakers() {
    mic.current?.swapSpeakers()

    setSegments((prev) =>
      prev.map((s) => ({
        ...s,
        speaker: s.speaker === DOCTOR ? PATIENT : s.speaker === PATIENT ? DOCTOR : s.speaker,
      })),
    )

    try {
      await api.post(`/v1/encounters/${id}/transcript/swap-speakers`)
    } catch (e) {
      setError(`The correction did not save: ${(e as Error).message}`)
    }
  }

  async function grantConsent() {
    try {
      const c = await api.post<{ granted: boolean; capturedAt: string; retentionStatement: string }>(
        `/v1/encounters/${id}/consent`,
        { granted: true },
      )
      setConsent(c)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  /**
   * Real ambient capture from the microphone.
   *
   * Each final utterance is posted to the same transcript table the scripted
   * consultation writes to, so everything downstream — extraction, the allergy check,
   * the scribe, provenance replay — is identical whichever source produced the words.
   */
  async function startLive() {
    setError(null)
    try {
      const started = await api.post<{ startedAt: string }>(`/v1/encounters/${id}/start`)
      setStartedAt(started.startedAt)
    } catch (e) {
      setError((e as Error).message)
      return
    }

    const session = await startTranscription(
      async (result) => {
        if (!result.isFinal) {
          setInterim(result.text)
          return
        }
        setInterim('')

        try {
          const saved = await api.post<{ id: string; startMs: number; endMs: number }>(
            `/v1/encounters/${id}/transcript`,
            {
              text: result.text,
              offsetMs: result.offsetMs,
              durationMs: result.durationMs,
              confidence: result.confidence,
              speaker: result.speaker,
            },
          )
          setSegments((prev) => [
            ...prev,
            { id: saved.id, speaker: result.speaker ?? '—', text: result.text,
              startMs: saved.startMs, endMs: saved.endMs, confidence: result.confidence },
          ])
        } catch {
          // Losing a sentence silently is the one thing capture must never do.
          setError('A sentence could not be saved. Check the connection before continuing.')
        }
      },
      (message) => {
        setError(message)
        setRecording(false)
        setMode('idle')
      },
    )

    if (!session) return    // not configured; the error already explains why

    mic.current = session
    setMode('live')
    setRecording(true)
  }

  /** The scripted consultation. Demo Mode, and it says so on screen. */
  async function startDemo() {
    setError(null)
    try {
      const started = await api.post<{ startedAt: string }>(`/v1/encounters/${id}/start`)
      setStartedAt(started.startedAt)
      setMode('demo')
      setRecording(true)

      abort.current = new AbortController()
      await api.stream(
        `/v1/encounters/${id}/transcript/stream`,
        (event, data) => {
          if (event === 'segment') setSegments((prev) => [...prev, data as Segment])
          if (event === 'complete') setRecording(false)
        },
        abort.current.signal,
      )
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message)
      setRecording(false)
    }
  }

  async function endAndDraft() {
    await mic.current?.stop()
    mic.current = null
    abort.current?.abort()
    setRecording(false)
    setDrafting(true)
    try {
      await api.post(`/v1/encounters/${id}/end`)
      const { noteId } = await api.post<{ noteId: string }>(`/v1/encounters/${id}/draft`)
      navigate(`/note/${noteId}`)
    } catch (e) {
      setError((e as Error).message)
      setDrafting(false)
    }
  }

  const chips = [
    { title: 'Symptoms', items: extraction?.symptoms ?? [] },
    { title: 'Vitals', items: extraction?.vitals ?? [] },
    { title: 'Medications discussed', items: extraction?.medications ?? [] },
    { title: 'Orders forming', items: extraction?.orders ?? [] },
  ]

  return (
    <div className="h-full flex flex-col">
      {/* Capture header — the audio-health truth-teller. */}
      <div className="px-4 py-2.5 hairline-b flex items-center gap-3 flex-wrap" style={{ background: 'var(--color-surface)' }}>
        {recording ? (
          <span className="flex items-center gap-2 micro" style={{ color: 'var(--color-dangertext)' }}>
            <span className="recording-dot w-2 h-2 rounded-full" style={{ background: 'var(--color-danger)' }} />
            Recording
          </span>
        ) : (
          <span className="micro" style={{ color: 'var(--color-faint)' }}>
            ○ Idle
          </span>
        )}

        <span className="mono">{fmt(elapsed * 1000)}</span>

        {mode !== 'idle' && (
          <span className="micro px-1.5 py-0.5 rounded-[6px]"
                style={mode === 'live'
                  ? { color: 'var(--color-ok)', background: 'color-mix(in srgb, var(--color-ok) 10%, transparent)' }
                  : { color: 'var(--color-reviewtext)', background: 'color-mix(in srgb, var(--color-review) 12%, transparent)' }}>
            {mode === 'live' ? 'microphone · Azure Speech' : 'DEMO — recorded consultation'}
          </span>
        )}

        {recording && (
          <span className="flex items-end gap-[2px] h-4" aria-hidden>
            {[0, 1, 2, 3, 4, 5, 6].map((i) => (
              <span
                key={i}
                className="wave-bar w-[3px] rounded-full"
                style={{ height: '100%', background: 'var(--color-mint)', animationDelay: `${i * 90}ms` }}
              />
            ))}
          </span>
        )}

        <div className="flex-1" />

        {!recording && segments.length === 0 && (
          <>
            <button
              onClick={startLive}
              disabled={!consent?.granted}
              className="px-3 py-1.5 rounded-[8px] text-[13px] text-white disabled:opacity-40"
              style={{ background: 'var(--color-pulse)' }}
              title={consent?.granted ? 'Streams your microphone to Azure AI Speech' : 'Capture is blocked until consent is captured'}
            >
              ◉ Start capture
            </button>
            <button
              onClick={startDemo}
              disabled={!consent?.granted}
              className="px-3 py-1.5 rounded-[8px] text-[13px] hairline disabled:opacity-40"
              title="Play the recorded demonstration consultation instead of using the microphone"
            >
              ▸ Demo consultation
            </button>
          </>
        )}

        {(recording || segments.length > 0) && (
          <button
            onClick={endAndDraft}
            disabled={drafting}
            className="px-3 py-1.5 rounded-[8px] text-[13px] hairline disabled:opacity-50"
          >
            {drafting ? 'Drafting…' : '■ End & draft'}
          </button>
        )}
      </div>

      {/* Consent is a chip, not a modal. It stays visible for the whole recording
          so both parties can see it — ethically and legally load-bearing. */}
      <div className="px-4 py-1.5 hairline-b flex items-center gap-3 flex-wrap" style={{ background: 'var(--color-sunken)' }}>
        {consent?.granted ? (
          <>
            <span className="micro" style={{ color: 'var(--color-ok)' }}>
              ✓ Consent captured {new Date(consent.capturedAt).toLocaleTimeString()}
            </span>
            <span className="micro" style={{ color: 'var(--color-faint)' }}>
              {consent.retentionStatement}
            </span>
          </>
        ) : (
          <>
            <span className="micro" style={{ color: 'var(--color-reviewtext)' }}>
              ▲ Consent pending — capture is blocked
            </span>
            <button onClick={grantConsent} className="micro underline" style={{ color: 'var(--color-pulse)' }}>
              Capture consent
            </button>
            <span className="micro" style={{ color: 'var(--color-faint)' }}>
              Declining is fine — you can still document manually.
            </span>
          </>
        )}
      </div>

      {error && (
        <div className="px-4 py-2 text-[13px]" style={{ background: 'color-mix(in srgb, var(--color-danger) 10%, transparent)', color: 'var(--color-dangertext)' }}>
          {error}
        </div>
      )}

      <div className="flex-1 min-h-0 grid grid-cols-1 md:grid-cols-2">
        {/* LIVE TRANSCRIPT */}
        <section className="min-h-0 flex flex-col" style={{ borderRight: '1px solid var(--color-hairline)' }}>
          <h2 className="micro px-4 py-2 flex items-center gap-2" style={{ color: 'var(--color-faint)' }}>
            <span>Live transcript</span>
            {startedAt && (
              <span>
                · started{' '}
                {new Date(startedAt).toLocaleString(undefined, {
                  day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
                })}
              </span>
            )}

            <span className="flex-1" />

            {/* Diarisation gives anonymous voices; which one is the clinician is our guess.
                A wrong attribution in a clinical record is worse than none at all, so the
                guess is always one tap from being corrected. */}
            {segments.some((s) => s.speaker === DOCTOR || s.speaker === PATIENT) && (
              <button
                onClick={swapSpeakers}
                className="micro px-1.5 py-0.5 rounded-[6px] hairline"
                title="Azure separates the voices; Aria guesses which is the clinician. Swap if it guessed wrong."
              >
                ⇄ Swap Dr./Pt.
              </button>
            )}
          </h2>
          <div ref={scroller} className="flex-1 overflow-auto px-4 pb-4" aria-live="polite">
            {segments.length === 0 && (
              <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
                Capture the consent chip above, then start. The transcript appears here as it is heard.
              </p>
            )}
            {interim && (
              <div className="mb-3 flex gap-3" style={{ opacity: 0.6 }}>
                <span className="micro w-10 shrink-0 pt-0.5" style={{ color: 'var(--color-faint)' }}>…</span>
                <p className="text-[13px] leading-5 flex-1 italic">{interim}</p>
              </div>
            )}
            {segments.map((s) => (
              <div key={s.id} className="mb-3 flex gap-3">
                <span
                  className="micro w-10 shrink-0 pt-0.5"
                  style={{
                    // The doctor's own lines sit back; the patient's are what the note is
                    // actually about, so they carry the ink.
                    color: s.speaker === PATIENT ? 'var(--color-ink)' : 'var(--color-faint)',
                    fontWeight: s.speaker === PATIENT ? 600 : 400,
                  }}
                >
                  {s.speaker}
                </span>
                <p className="text-[13px] leading-5 flex-1">
                  <span className="mono mr-2" style={{ color: 'var(--color-faint)' }}
                        title={clockAt(startedAt, s.startMs, true)}>
                    {clockAt(startedAt, s.startMs)}
                  </span>
                  {s.text}
                  {s.confidence < 0.65 && (
                    <span
                      className="micro ml-2 px-1 rounded"
                      style={{ color: 'var(--color-reviewtext)', background: 'color-mix(in srgb, var(--color-review) 12%, transparent)' }}
                      title="Low ASR confidence — this becomes a flagged passage in the note"
                    >
                      unclear audio
                    </span>
                  )}
                </p>
              </div>
            ))}
          </div>
        </section>

        {/* AS I HEAR IT */}
        <section className="min-h-0 flex flex-col">
          <h2 className="micro px-4 py-2 flex items-center gap-2" style={{ color: 'var(--color-faint)' }}>
            As I hear it <AIChip label="AI" />
          </h2>

          <div className="flex-1 overflow-auto px-4 pb-4">
            {/* The alert that justifies the whole feature. Rose is reserved for this. */}
            {extraction?.conflicts.map((c) => (
              <div
                key={c.drugLabel}
                role="alert"
                className="mb-4 p-3 rounded-[10px]"
                style={{
                  background: 'color-mix(in srgb, var(--color-danger) 8%, transparent)',
                  border: '1px solid var(--color-danger)',
                }}
              >
                <div className="micro mb-1" style={{ color: 'var(--color-dangertext)' }}>
                  ▲ {c.severity} — contraindication
                </div>
                <p className="text-[13px] font-medium">
                  {c.drugLabel} vs recorded {c.allergyLabel}
                </p>
                <p className="text-[12px] mt-0.5" style={{ color: 'var(--color-muted)' }}>
                  {c.explanation}
                </p>
              </div>
            ))}

            {chips.map((group) =>
              group.items.length === 0 ? null : (
                <div key={group.title} className="mb-4">
                  <h3 className="micro mb-1.5" style={{ color: 'var(--color-faint)' }}>
                    {group.title}
                  </h3>
                  <div className="flex flex-wrap gap-1.5">
                    {group.items.map((e) => (
                      <span
                        key={e.label}
                        className="text-[12px] px-2 py-0.5 rounded-full hairline"
                        title={`heard at ${fmt(e.transcriptMs)}`}
                      >
                        {e.label}
                      </span>
                    ))}
                  </div>
                </div>
              ),
            )}

            {!extraction && (
              <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
                Findings appear here as they are mentioned.
              </p>
            )}

            {extraction?.degraded && (
              <p className="text-[13px] mt-3" style={{ color: 'var(--color-reviewtext)' }}>
                Extraction is degraded — the transcript is still being captured in full.
              </p>
            )}
          </div>
        </section>
      </div>
    </div>
  )
}

/**
 * The wall-clock time an utterance was spoken.
 *
 * The transcript carries offsets in milliseconds because that is what provenance replay
 * needs. A clinician reading the record needs "14:32" — the offset is meaningless to
 * anyone reconstructing what happened when, and a medico-legal record is read by people
 * who were not in the room.
 *
 * Falls back to the offset when the start time is not known yet, rather than inventing
 * a time relative to now.
 */
function clockAt(startedAt: string | null, offsetMs: number, full = false): string {
  if (!startedAt) {
    const seconds = Math.round(offsetMs / 1000)
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
  }

  const at = new Date(new Date(startedAt).getTime() + offsetMs)

  return full
    ? at.toLocaleString(undefined, {
        weekday: 'long', day: 'numeric', month: 'long', year: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit',
      })
    : at.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}
