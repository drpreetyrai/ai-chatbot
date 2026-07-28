import { useEffect, useRef, useState } from 'react'
import { api, type AssistantReply } from '../lib/api'
import { AIChip } from './AIBlock'

type Turn = { role: 'user' | 'assistant'; text: string; at: string; reply?: AssistantReply }

/**
 * The conversational assistant, shared by the clinician and patient shells.
 *
 * The differences that matter are on the server — grounding, tone, and what happens
 * when a message sounds urgent — so this component only changes its copy. A second
 * implementation would be a second place for the safety behaviour to drift.
 */
export function AssistantChat({
  audience,
  patientId,
  suggestions,
  className,
}: {
  audience: 'patient' | 'clinician'
  patientId?: string
  suggestions: string[]
  className?: string
}) {
  const [turns, setTurns] = useState<Turn[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const scroller = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const query = patientId ? `?patientId=${encodeURIComponent(patientId)}` : ''
    api
      .get<Turn[]>(`/v1/assistant/history${query}`)
      .then(setTurns)
      .catch(() => {})
  }, [patientId])

  useEffect(() => {
    scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: 'smooth' })
  }, [turns.length, busy])

  async function send(message: string) {
    if (!message.trim() || busy) return

    setInput('')
    setBusy(true)
    setTurns((t) => [...t, { role: 'user', text: message, at: new Date().toISOString() }])

    try {
      const reply = await api.post<AssistantReply>('/v1/assistant/chat', { message, patientId })
      setTurns((t) => [...t, { role: 'assistant', text: reply.text, at: new Date().toISOString(), reply }])
    } catch (e) {
      setTurns((t) => [
        ...t,
        {
          role: 'assistant',
          at: new Date().toISOString(),
          text:
            audience === 'patient'
              ? "I couldn't answer that just now. Please call the clinic on 080-4000-4400."
              : `The assistant is unavailable (${(e as Error).message}).`,
        },
      ])
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={`flex flex-col min-h-0 ${className ?? ''}`}>
      <div ref={scroller} className="flex-1 overflow-auto p-4" aria-live="polite">
        {turns.length === 0 && (
          <div className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
            <p className="mb-3">
              {audience === 'patient'
                ? 'Ask about your visit, your medicines or your next appointment. I answer only from your own record.'
                : "Ask about this patient's record. Every answer is grounded in what has been signed."}
            </p>
          </div>
        )}

        {turns.map((turn, i) => (
          <div key={i} className={`mb-3 flex ${turn.role === 'user' ? 'justify-end' : ''}`}>
            <div
              className="max-w-[85%] rounded-[12px] px-3 py-2"
              style={{
                background: turn.role === 'user' ? 'var(--color-sunken)' : 'var(--color-surface)',
                border: turn.role === 'user' ? 'none' : '1px solid var(--color-hairline)',
                // An escalated reply is the one message in the thread that must not read
                // like all the others.
                borderColor: turn.reply?.escalated ? 'var(--color-danger)' : undefined,
              }}
            >
              {turn.role === 'assistant' && (
                <div className="mb-1 flex items-center gap-2">
                  {turn.reply?.escalated ? (
                    <span className="micro" style={{ color: 'var(--color-dangertext)' }}>
                      ▲ Escalated to a person
                    </span>
                  ) : (
                    <AIChip label="Aria" />
                  )}
                </div>
              )}

              <p className="text-[13px] leading-5 whitespace-pre-wrap">{turn.text}</p>

              {turn.reply && turn.reply.sources.length > 0 && (
                <div className="mt-2 pt-2 hairline-t">
                  <span className="micro" style={{ color: 'var(--color-faint)' }}>
                    From your record:
                  </span>
                  {turn.reply.sources.map((s) => (
                    <span key={s.id} className="micro block" style={{ color: 'var(--color-pulse)' }}>
                      · {s.citation ?? s.title}
                    </span>
                  ))}
                </div>
              )}

              {turn.reply?.interventions.length ? (
                <p className="micro mt-1" style={{ color: 'var(--color-reviewtext)' }}>
                  Guardrail: {turn.reply.interventions.join('; ')}
                </p>
              ) : null}
            </div>
          </div>
        ))}

        {busy && (
          <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
            Aria is reading the record…
          </p>
        )}
      </div>

      {/* Examples, not an empty box — the same rule as the command palette. */}
      {turns.length === 0 && (
        <div className="px-4 pb-2 flex gap-1.5 flex-wrap">
          {suggestions.map((s) => (
            <button
              key={s}
              onClick={() => send(s)}
              className="text-[12px] px-2 py-1 rounded-full hairline hover:bg-[var(--color-sunken)]"
            >
              {s}
            </button>
          ))}
        </div>
      )}

      <div className="p-3 hairline-t" style={{ background: 'var(--color-surface)' }}>
        <div className="flex gap-2">
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && send(input)}
            placeholder={audience === 'patient' ? 'Ask about your care…' : 'Ask about this patient…'}
            className="flex-1 px-3 py-2 rounded-[8px] hairline bg-transparent text-[13px]"
          />
          <button
            onClick={() => send(input)}
            disabled={busy || !input.trim()}
            className="px-3 py-2 rounded-[8px] text-[13px] text-white disabled:opacity-40"
            style={{ background: 'var(--color-pulse)' }}
          >
            Send
          </button>
        </div>

        <p className="micro mt-2" style={{ color: 'var(--color-faint)' }}>
          {audience === 'patient'
            ? 'Aria never diagnoses and never changes your medicines. For anything urgent, call 080-4000-4400 or 108.'
            : 'Answers come only from this patient’s signed record. Always verify before acting.'}
        </p>
      </div>
    </section>
  )
}
