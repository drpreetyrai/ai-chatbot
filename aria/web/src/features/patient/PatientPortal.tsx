import { useEffect, useState } from 'react'
import {
  api, setToken,
  type Identity, type PortalAppointment, type PortalMessage, type PortalPatient, type PortalVisit,
} from '../../lib/api'
import { AssistantChat } from '../../components/AssistantChat'

type Tab = 'home' | 'chat' | 'visits' | 'messages'

/**
 * The patient's own surface.
 *
 * Deliberately not a cut-down clinician view. A patient needs four things — what is
 * coming up, what happened last time, what the clinic has said, and somewhere to ask
 * a question — so those are the four tabs and nothing else is here.
 *
 * Every request is scoped server-side to their linked record; there is no patient id
 * anywhere in this file, because there is nowhere for one to go wrong.
 */
export function PatientPortal({ me }: { me: Identity }) {
  const [tab, setTab] = useState<Tab>('home')
  const [patient, setPatient] = useState<PortalPatient | null>(null)
  const [appointments, setAppointments] = useState<PortalAppointment[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<PortalPatient>('/v1/portal/me').then(setPatient).catch((e) => setError((e as Error).message))
    api.get<PortalAppointment[]>('/v1/portal/appointments').then(setAppointments).catch(() => {})
  }, [])

  const next = appointments.find((a) => !a.isPast)

  const tabs: [Tab, string][] = [
    ['home', 'Home'],
    ['chat', 'Ask Aria'],
    ['visits', 'My visits'],
    ['messages', 'Messages'],
  ]

  return (
    <div className="min-h-screen flex flex-col">
      <header className="hairline-b px-4 h-14 flex items-center gap-3 shrink-0"
              style={{ background: 'var(--color-surface)' }}>
        <span className="font-semibold tracking-[0.18em] text-[13px]">ARIA</span>
        <span className="micro px-2 py-0.5 rounded-[6px]"
              style={{ background: 'var(--color-sunken)', color: 'var(--color-muted)' }}>
          Patient
        </span>
        <div className="flex-1" />
        <span className="text-[13px]">{me.name}</span>
        <button
          onClick={async () => {
            await api.post('/v1/auth/signout').catch(() => {})
            setToken(null)
            location.reload()
          }}
          className="micro"
          style={{ color: 'var(--color-faint)' }}
        >
          Sign out
        </button>
      </header>

      {error && (
        <p className="px-4 py-3 text-[13px]" style={{ color: 'var(--color-dangertext)' }}>
          {error}
        </p>
      )}

      <nav className="hairline-b flex px-2 shrink-0" style={{ background: 'var(--color-surface)' }}>
        {tabs.map(([key, label]) => (
          <button
            key={key}
            onClick={() => setTab(key)}
            className="px-3 py-2.5 text-[13px]"
            style={{
              fontWeight: tab === key ? 600 : 400,
              color: tab === key ? 'var(--color-ink)' : 'var(--color-muted)',
              borderBottom: tab === key ? '2px solid var(--color-pulse)' : '2px solid transparent',
            }}
          >
            {label}
          </button>
        ))}
      </nav>

      <main className="flex-1 min-h-0 overflow-auto">
        {tab === 'home' && <Home patient={patient} next={next} appointments={appointments} onAsk={() => setTab('chat')} />}

        {tab === 'chat' && (
          <AssistantChat
            audience="patient"
            className="h-full"
            suggestions={[
              'What did the doctor say was wrong with me?',
              'How do I take my medicine?',
              'When is my next appointment?',
              'Do I need to fast before my blood test?',
            ]}
          />
        )}

        {tab === 'visits' && <Visits />}
        {tab === 'messages' && <Messages />}
      </main>
    </div>
  )
}

function Home({
  patient, next, appointments, onAsk,
}: {
  patient: PortalPatient | null
  next?: PortalAppointment
  appointments: PortalAppointment[]
  onAsk: () => void
}) {
  if (!patient) return <p className="p-6 text-[13px]" style={{ color: 'var(--color-faint)' }}>Loading…</p>

  return (
    <div className="p-5 max-w-2xl">
      <h1 className="text-[22px] font-semibold mb-1">Hello, {patient.name.split(' ')[0]}</h1>
      <p className="text-[13px] mb-5" style={{ color: 'var(--color-muted)' }}>
        Everything here is your own record. Nobody else's.
      </p>

      {next ? (
        <section className="rounded-[14px] hairline p-4 mb-4" style={{ background: 'var(--color-surface)' }}>
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>Your next appointment</h2>
          <p className="text-[16px] font-medium">
            {new Date(next.startAt).toLocaleString(undefined, {
              weekday: 'long', day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit',
            })}
          </p>
          <p className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
            {next.reason ?? 'Appointment'} · with {next.doctor}
          </p>
        </section>
      ) : (
        <section className="rounded-[14px] hairline p-4 mb-4" style={{ background: 'var(--color-surface)' }}>
          <h2 className="micro mb-1" style={{ color: 'var(--color-faint)' }}>Appointments</h2>
          <p className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
            Nothing booked. Call 080-4000-4400 to arrange a visit.
          </p>
        </section>
      )}

      {/* Allergies are the one clinical fact a patient genuinely needs at hand —
          it is what they will be asked in any other clinic or pharmacy. */}
      {patient.allergies.length > 0 && (
        <section className="rounded-[14px] p-4 mb-4"
                 style={{ border: '1px solid var(--color-danger)',
                          background: 'color-mix(in srgb, var(--color-danger) 6%, transparent)' }}>
          <h2 className="micro mb-1" style={{ color: 'var(--color-dangertext)' }}>▲ Your allergies</h2>
          {patient.allergies.map((a) => (
            <p key={a.label} className="text-[14px] font-medium">{a.label}</p>
          ))}
          <p className="micro mt-1" style={{ color: 'var(--color-muted)' }}>
            Tell any clinician or pharmacist about this before taking a new medicine.
          </p>
        </section>
      )}

      <section className="rounded-[14px] hairline p-4 mb-4" style={{ background: 'var(--color-surface)' }}>
        <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>Have a question?</h2>
        <p className="text-[13px] mb-3" style={{ color: 'var(--color-muted)' }}>
          Aria can explain your visit, your medicines and how to prepare for a test — using
          your own records. It never diagnoses, and anything urgent goes straight to a person.
        </p>
        <button
          onClick={onAsk}
          className="px-3 py-1.5 rounded-[8px] text-[13px] text-white"
          style={{ background: 'var(--color-pulse)' }}
        >
          Ask Aria
        </button>
      </section>

      {appointments.filter((a) => a.isPast).length > 0 && (
        <section>
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>Past appointments</h2>
          <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
            {appointments.filter((a) => a.isPast).map((a) => (
              <div key={a.id} className="px-4 py-2.5 hairline-b last:border-b-0 text-[13px]">
                <span style={{ color: 'var(--color-faint)' }}>
                  {new Date(a.startAt).toLocaleDateString()} ·{' '}
                </span>
                {a.reason} · {a.doctor}
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}

function Visits() {
  const [visits, setVisits] = useState<PortalVisit[]>([])

  useEffect(() => {
    api.get<PortalVisit[]>('/v1/portal/visits').then(setVisits).catch(() => {})
  }, [])

  return (
    <div className="p-5 max-w-2xl">
      <h1 className="text-[20px] font-semibold mb-1">My visits</h1>
      <p className="text-[13px] mb-4" style={{ color: 'var(--color-muted)' }}>
        Summaries of visits your clinician has signed off. A note only appears here once it
        has been reviewed and signed.
      </p>

      {visits.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
          No signed visit summaries yet.
        </p>
      ) : (
        visits.map((v) => (
          <article key={v.id} className="rounded-[14px] hairline p-4 mb-3" style={{ background: 'var(--color-surface)' }}>
            <div className="flex items-baseline justify-between mb-2 flex-wrap gap-1">
              <span className="text-[14px] font-medium">
                {new Date(v.signedAt).toLocaleDateString(undefined, {
                  weekday: 'long', day: 'numeric', month: 'long', year: 'numeric',
                })}
              </span>
              <span className="micro" style={{ color: 'var(--color-faint)' }}>
                Reviewed by {v.clinician}
              </span>
            </div>

            {v.summary && (
              <>
                <h3 className="micro mb-1" style={{ color: 'var(--color-faint)' }}>What we found</h3>
                <p className="note-body mb-3">{v.summary}</p>
              </>
            )}

            {v.plan && (
              <>
                <h3 className="micro mb-1" style={{ color: 'var(--color-faint)' }}>What happens next</h3>
                <p className="note-body">{v.plan}</p>
              </>
            )}
          </article>
        ))
      )}
    </div>
  )
}

function Messages() {
  const [messages, setMessages] = useState<PortalMessage[]>([])

  useEffect(() => {
    api.get<PortalMessage[]>('/v1/portal/messages').then(setMessages).catch(() => {})
  }, [])

  return (
    <div className="p-5 max-w-2xl">
      <h1 className="text-[20px] font-semibold mb-1">Messages</h1>
      <p className="text-[13px] mb-4" style={{ color: 'var(--color-muted)' }}>
        Messages between you and the clinic. Everything here was reviewed by a person before
        it was sent.
      </p>

      {messages.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>No messages yet.</p>
      ) : (
        messages.map((m) => (
          <div key={m.id} className={`mb-3 flex ${m.fromClinic ? '' : 'justify-end'}`}>
            <div
              className="max-w-[80%] rounded-[12px] px-3 py-2"
              style={{
                background: m.fromClinic ? 'var(--color-surface)' : 'var(--color-sunken)',
                border: m.fromClinic ? '1px solid var(--color-hairline)' : 'none',
              }}
            >
              <p className="text-[13px] whitespace-pre-wrap">{m.body}</p>
              <p className="micro mt-1" style={{ color: 'var(--color-faint)' }}>
                {m.fromClinic ? 'Northbridge Health' : 'You'} ·{' '}
                {new Date(m.createdAt).toLocaleString()}
              </p>
            </div>
          </div>
        ))
      )}
    </div>
  )
}
