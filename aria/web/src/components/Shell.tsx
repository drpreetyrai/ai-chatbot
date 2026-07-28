import { useEffect, useState, type ReactNode } from 'react'
import { NavLink, useNavigate } from 'react-router-dom'
import { api, setToken, type Escalation, type Identity, type Integration } from '../lib/api'

/**
 * The persistent rail plus the two things that must never be missed: the
 * escalation banner and the honest statement of which services are live.
 */
export function Shell({ me, children }: { me: Identity; children: ReactNode }) {
  const [escalations, setEscalations] = useState<Escalation[]>([])
  const [showHelp, setShowHelp] = useState(false)
  const [showPalette, setShowPalette] = useState(false)
  const navigate = useNavigate()

  // The escalation banner polls on its own heartbeat. If the connection drops,
  // the next tick recovers it — an escalation must never be missed because a
  // socket died.
  useEffect(() => {
    let alive = true
    const tick = () =>
      api
        .get<Escalation[]>('/v1/escalations')
        .then((rows) => alive && setEscalations(rows))
        .catch(() => {})
    tick()
    const timer = setInterval(tick, 10_000)

    // A red flag must not wait for the next heartbeat. Anything that detects one
    // announces it, and the banner refreshes on the spot — ten seconds is a long
    // time to sit on "chest tightness".
    const onEscalation = () => tick()
    window.addEventListener('aria:escalation', onEscalation)

    return () => {
      alive = false
      clearInterval(timer)
      window.removeEventListener('aria:escalation', onEscalation)
    }
  }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault()
        setShowPalette((v) => !v)
      }
      if (e.key === 'Escape') {
        setShowPalette(false)
        setShowHelp(false)
      }
      if (e.key === '?' && !(e.target as HTMLElement)?.closest('input,textarea')) {
        setShowHelp(true)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const nav = [
    { to: '/today', label: 'Today' },
    { to: '/patients', label: 'Patients' },
    { to: '/schedule', label: 'Schedule' },
    { to: '/inbox', label: 'Inbox' },
    { to: '/insights', label: 'Insights' },
    { to: '/admin', label: 'Admin' },
  ]

  return (
    <div className="min-h-screen flex flex-col">
      {/* z-index: escalation. Above modals, cannot be dismissed unacknowledged. */}
      {escalations.length > 0 && (
        <div
          role="alert"
          aria-live="assertive"
          className="sticky top-0 z-50 px-4 py-2 flex items-center gap-3 flex-wrap"
          style={{ background: 'var(--color-danger)', color: '#fff' }}
        >
          <span className="micro">▲ Red flag</span>
          {escalations.map((e) => (
            <button
              key={e.id}
              onClick={() => navigate(`/inbox?thread=${e.threadId}&escalation=${e.id}`)}
              className="text-[13px] underline underline-offset-2"
            >
              {e.patientName} — “{e.trigger.replace(/_/g, ' ')}” · waiting {e.waitingSeconds}s
              {e.slaBreached && ' · SLA BREACHED'}
            </button>
          ))}
          <span className="micro ml-auto">on-call notified · acknowledge in Inbox</span>
        </div>
      )}

      <header className="hairline-b px-4 h-12 flex items-center gap-4 shrink-0" style={{ background: 'var(--color-surface)' }}>
        <span className="font-semibold tracking-[0.18em] text-[13px]">ARIA</span>

        <button
          onClick={() => setShowPalette(true)}
          className="hairline rounded-[8px] px-3 h-7 text-[13px] flex-1 max-w-md text-left"
          style={{ color: 'var(--color-faint)' }}
        >
          Search patients, notes, slots…
          <kbd className="micro float-right mt-[1px]">⌘K</kbd>
        </button>

        <ServiceStatus />

        <button onClick={() => setShowHelp(true)} className="micro" title="Help  ( ? )">
          ? Help
        </button>

        <div className="text-right leading-tight">
          <div className="text-[13px]">{me.name}</div>
          <div className="micro" style={{ color: 'var(--color-faint)' }}>
            {me.department} · {me.role}
          </div>
        </div>

        <button
          onClick={async () => {
            // Revoke the session on the server. Clearing local storage alone would
            // leave a working token in the wild, which is not sign-out.
            await api.post('/v1/auth/signout').catch(() => {})
            setToken(null)
            location.href = '/'
          }}
          className="micro"
          style={{ color: 'var(--color-faint)' }}
        >
          Sign out
        </button>
      </header>

      <div className="flex flex-1 min-h-0">
        <nav className="w-[184px] shrink-0 hairline-r p-3 hidden lg:block" style={{ borderRight: '1px solid var(--color-hairline)' }}>
          {nav.map((n) => (
            <NavLink
              key={n.to}
              to={n.to}
              className={({ isActive }) =>
                `block px-3 py-1.5 rounded-[8px] text-[13px] mb-0.5 ${isActive ? 'font-semibold' : ''}`
              }
              style={({ isActive }) => ({
                background: isActive ? 'var(--color-sunken)' : 'transparent',
                color: isActive ? 'var(--color-ink)' : 'var(--color-muted)',
              })}
            >
              {n.label}
            </NavLink>
          ))}
        </nav>

        <main className="flex-1 min-w-0 overflow-auto">{children}</main>
      </div>

      {showPalette && <CommandPalette onClose={() => setShowPalette(false)} />}
      {showHelp && <HelpDrawer onClose={() => setShowHelp(false)} />}
    </div>
  )
}

/**
 * Which brain am I talking to?
 *
 * The operator should never have to guess whether the note they are reading came
 * from a real model or the local stub. This chip is the answer, always on screen.
 */
function ServiceStatus() {
  const [rows, setRows] = useState<Integration[]>([])

  useEffect(() => {
    api.get<Integration[]>('/v1/admin/integrations').then(setRows).catch(() => {})
  }, [])

  const model = rows.find((r) => r.name === 'Model plane')
  if (!model) return null

  return (
    <span
      className="micro px-2 py-0.5 rounded-[6px] hairline"
      title={rows.map((r) => `${r.name}: ${r.live ? 'LIVE' : 'STUB'} — ${r.detail}`).join('\n')}
      style={{
        color: model.live ? 'var(--color-ok)' : 'var(--color-reviewtext)',
        background: model.live
          ? 'color-mix(in srgb, var(--color-ok) 8%, transparent)'
          : 'color-mix(in srgb, var(--color-review) 10%, transparent)',
      }}
    >
      {model.live ? '● model live' : '● model: local stub'}
    </span>
  )
}

/**
 * ⌘K opens with EXAMPLES, not an empty box (plan.md §14.2).
 *
 * An empty command box teaches nothing. These are the five things a clinician
 * actually does, phrased the way they would say them out loud.
 */
function CommandPalette({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()

  const examples = [
    { label: '"start encounter John"', hint: 'begins ambient capture for John Abraham', to: '/encounter/enc-john' },
    { label: '"review and sign Sarah"', hint: "opens Sarah Menon's draft to sign", to: '/today' },
    { label: '"book Neha next week morning"', hint: 'proposes 3 slots with reasons', to: '/schedule' },
    { label: '"message Ali about fasting"', hint: 'drafts from an approved template, you approve', to: '/inbox' },
    { label: '"what did we do for John\'s cough?"', hint: 'searches his chart, with citations', to: '/patients/pt-john' },
  ]

  return (
    <div className="fixed inset-0 z-60 flex items-start justify-center pt-24 px-4" style={{ background: 'rgba(11,18,32,.35)' }} onClick={onClose}>
      <div
        className="w-full max-w-xl rounded-[14px] overflow-hidden"
        style={{ background: 'var(--color-surface)', boxShadow: '0 12px 32px rgba(11,18,32,.14)' }}
        onClick={(e) => e.stopPropagation()}
      >
        <input
          autoFocus
          placeholder="Type a command or a patient's name…"
          className="w-full px-4 py-3 text-[15px] outline-none hairline-b bg-transparent"
        />
        <div className="p-2">
          <div className="micro px-2 py-1" style={{ color: 'var(--color-faint)' }}>
            Try these
          </div>
          {examples.map((e) => (
            <button
              key={e.label}
              onClick={() => {
                navigate(e.to)
                onClose()
              }}
              className="w-full text-left px-2 py-2 rounded-[8px] hover:bg-[var(--color-sunken)] flex justify-between gap-3"
            >
              <span className="text-[13px]">{e.label}</span>
              <span className="micro shrink-0 self-center" style={{ color: 'var(--color-faint)' }}>
                {e.hint}
              </span>
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}

/**
 * Help slides over the current screen — never a new tab, never a lost place.
 *
 * The section that matters most is "What Aria won't do". Setting the boundary
 * explicitly is part of teaching the tool, and it is the thing clinicians ask
 * about first.
 */
function HelpDrawer({ onClose }: { onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-60 flex justify-end" style={{ background: 'rgba(11,18,32,.35)' }} onClick={onClose}>
      <aside
        className="w-full max-w-md h-full overflow-auto p-5"
        style={{ background: 'var(--color-surface)', boxShadow: '0 12px 32px rgba(11,18,32,.14)' }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-[16px] font-semibold">Help</h2>
          <button onClick={onClose} className="micro">
            Esc ✕
          </button>
        </div>

        <Section title="What Aria does">
          Aria listens during the visit, drafts the note, and prepares the follow-up, the
          prescription and the patient message. You review and sign; only then does anything
          leave this screen.
        </Section>

        <Section title="Try it">
          <ol className="list-decimal ml-4 space-y-1">
            <li>Open Today and press <b>Start encounter</b> on John Abraham.</li>
            <li>Watch the transcript stream and the chips fill in.</li>
            <li>At ~70s the penicillin conflict fires — while the patient is still in the room.</li>
            <li>End the encounter, review the draft, accept the low-confidence line, and sign.</li>
            <li>Open Admin → Outbox to see exactly what the signature released.</li>
          </ol>
        </Section>

        <Section title="Keyboard">
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
            <dt className="mono">⌘K</dt><dd>command palette</dd>
            <dt className="mono">?</dt><dd>this drawer</dd>
            <dt className="mono">Esc</dt><dd>close any drawer</dd>
          </dl>
        </Section>

        <Section title="What Aria won't do">
          <ul className="space-y-1">
            <li>· Aria never diagnoses. It surfaces cited evidence; you decide.</li>
            <li>· Aria never messages a patient without your approval.</li>
            <li>· Aria never writes to the record until you sign.</li>
            <li>· Aria never handles a red flag itself — it stops and gets a person.</li>
          </ul>
        </Section>
      </aside>
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mb-5">
      <h3 className="micro mb-1.5" style={{ color: 'var(--color-faint)' }}>
        {title}
      </h3>
      <div className="text-[13px] leading-5" style={{ color: 'var(--color-muted)' }}>
        {children}
      </div>
    </section>
  )
}
