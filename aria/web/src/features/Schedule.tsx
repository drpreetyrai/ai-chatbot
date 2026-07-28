import { useEffect, useState } from 'react'
import { api } from '../lib/api'

type Block = {
  startAt: string
  endAt: string
  title: string
  isExternal: boolean
  isBuffer: boolean
  patientId: string | null
}

type Proposal = { startAt: string; durationMinutes: number; reason: string }

type CalendarStatus = {
  configured: boolean
  connected: boolean
  calendarId: string | null
  connectedAt: string | null
  redirectUri: string
  reason: string | null
}

/**
 * Schedule (wireframe S-06).
 *
 * Google Calendar is the source of truth, not a mirror. External events render
 * as read-only blocks and cannot be edited here — no dual-write, no drift, no
 * "which calendar is right?".
 *
 * Requests are PROPOSALS WITH A REASON, never silent bookings, and there are
 * never more than three: more options is decision fatigue, not helpfulness.
 */
/**
 * Google Calendar consent, on the screen where its absence is felt.
 *
 * Three states, and the copy is different in each because the required action is
 * different: the operator has to configure credentials, the clinician has to grant
 * consent, or nothing needs doing. A single "not connected" message would leave the
 * clinician clicking a button that cannot work.
 */
function CalendarConnection() {
  const [status, setStatus] = useState<CalendarStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = () =>
    api.get<CalendarStatus>('/v1/integrations/google/status').then(setStatus).catch(() => {})

  useEffect(() => {
    void refresh()
  }, [])

  async function connect() {
    setBusy(true)
    setError(null)
    try {
      const { url } = await api.get<{ url: string }>('/v1/integrations/google/connect')
      const popup = window.open(url, 'aria-google', 'width=520,height=680')

      // Google redirects to our callback, which renders its own confirmation page — so
      // the only way back into this tab is to watch for the connection appearing. Polling
      // beats postMessage here: the callback is a plain page on a different origin.
      const started = Date.now()
      const timer = window.setInterval(async () => {
        const next = await api
          .get<CalendarStatus>('/v1/integrations/google/status')
          .catch(() => null)

        if (next?.connected || Date.now() - started > 180_000 || popup?.closed) {
          window.clearInterval(timer)
          setBusy(false)
          if (next) setStatus(next)
        }
      }, 2000)
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  async function disconnect() {
    await api.post('/v1/integrations/google/disconnect').catch(() => {})
    void refresh()
  }

  if (!status) return null

  return (
    <section
      className="rounded-[14px] hairline p-4 mb-6 flex items-start gap-3 flex-wrap"
      style={{ background: 'var(--color-surface)' }}
    >
      <div className="flex-1 min-w-[16rem]">
        <h2 className="micro mb-1" style={{ color: 'var(--color-faint)' }}>
          Google Calendar
        </h2>

        {!status.configured ? (
          <p className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
            {status.reason} Until then, holds and bookings are recorded locally and never
            reach a real calendar.
          </p>
        ) : status.connected ? (
          <p className="text-[13px]">
            <span style={{ color: 'var(--color-minttext)' }}>✓ Connected</span>{' '}
            <span className="mono" style={{ color: 'var(--color-muted)' }}>
              {status.calendarId}
            </span>
          </p>
        ) : (
          <>
            <p className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
              Not connected. Aria writes to <em>your</em> calendar, under your own Google
              identity — which is also why you can revoke it yourself at any time.
            </p>
            {/* Google reports an unregistered callback as a blank "Access blocked" page
                that names nothing. Showing the exact string it must match turns a
                twenty-minute hunt into a copy and paste. */}
            <p className="micro mt-1" style={{ color: 'var(--color-faint)' }}>
              If Google says <em>Access blocked</em>, add this to your OAuth client's
              authorised redirect URIs:{' '}
              <span className="mono" style={{ color: 'var(--color-muted)' }}>
                {status.redirectUri}
              </span>
            </p>
          </>
        )}

        {error && (
          <p className="micro mt-1" style={{ color: 'var(--color-dangertext)' }}>
            {error}
          </p>
        )}
      </div>

      {status.configured &&
        (status.connected ? (
          <button onClick={disconnect} className="micro px-2 py-1 rounded-[6px] hairline">
            Disconnect
          </button>
        ) : (
          <button
            onClick={connect}
            disabled={busy}
            className="px-3 py-1.5 rounded-[8px] text-[13px] text-white disabled:opacity-50"
            style={{ background: 'var(--color-pulse)' }}
          >
            {busy ? 'Waiting for Google…' : 'Connect Google Calendar'}
          </button>
        ))}
    </section>
  )
}

export function Schedule() {
  const [blocks, setBlocks] = useState<Block[]>([])
  const [proposals, setProposals] = useState<Proposal[]>([])
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    api.get<{ blocks: Block[] }>('/v1/schedule/day').then((d) => setBlocks(d.blocks)).catch(() => {})
  }, [])

  async function propose() {
    setBusy(true)
    setNotice(null)
    try {
      const result = await api.post<{ proposals: Proposal[]; note: string }>('/v1/schedule/proposals', {
        patientId: 'pt-john',
        withinDays: 7,
        durationMinutes: 20,
      })
      setProposals(result.proposals)
      setNotice(result.note)
    } finally {
      setBusy(false)
    }
  }

  async function hold(p: Proposal) {
    try {
      await api.post('/v1/schedule/holds', {
        patientId: 'pt-john',
        startAt: p.startAt,
        durationMinutes: p.durationMinutes,
      })
      setNotice('Slot held for 15 minutes. A hold changes nothing the patient can see — booking still needs a signature or an autonomy dial.')
      const day = await api.get<{ blocks: Block[] }>('/v1/schedule/day')
      setBlocks(day.blocks)
    } catch (e) {
      setNotice((e as Error).message)
    }
  }

  return (
    <div className="p-6 max-w-4xl">
      <h1 className="text-[20px] font-semibold mb-1">Schedule</h1>
      <p className="micro mb-5" style={{ color: 'var(--color-faint)' }}>
        Aria only writes into slots it holds. External calendar entries are read-only by contract.
      </p>

      <CalendarConnection />

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <section>
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Today
          </h2>
          <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
            {blocks.length === 0 ? (
              <p className="p-4 text-[13px]" style={{ color: 'var(--color-faint)' }}>
                No appointments — set your availability →
              </p>
            ) : (
              blocks.map((b, i) => (
                <div key={i} className="px-4 py-2.5 hairline-b last:border-b-0 flex items-center gap-3">
                  <span className="mono w-24 shrink-0" style={{ color: 'var(--color-faint)' }}>
                    {new Date(b.startAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                  </span>
                  <span className="text-[13px] flex-1">{b.title}</span>
                  {b.isExternal && (
                    <span
                      className="micro px-1.5 py-0.5 rounded-[6px]"
                      style={{ background: 'var(--color-sunken)', color: 'var(--color-faint)' }}
                      title="External — owned by Google Calendar and read-only here"
                    >
                      ░ external
                    </span>
                  )}
                  {b.title === 'held' && (
                    <span className="micro" style={{ color: 'var(--color-minttext)' }}>
                      ▓ Aria-held
                    </span>
                  )}
                </div>
              ))
            )}
          </div>
        </section>

        <section>
          <h2 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
            Booking proposals
          </h2>
          <div className="rounded-[14px] hairline p-4" style={{ background: 'var(--color-surface)' }}>
            <button
              onClick={propose}
              disabled={busy}
              className="text-[13px] underline disabled:opacity-50"
              style={{ color: 'var(--color-pulse)' }}
            >
              {busy ? 'Finding slots…' : 'Propose a follow-up for John Abraham →'}
            </button>

            {proposals.map((p) => (
              <div key={p.startAt} className="mt-3 pt-3 hairline-t">
                <div className="text-[13px] font-medium">
                  {new Date(p.startAt).toLocaleString([], {
                    weekday: 'short',
                    day: 'numeric',
                    month: 'short',
                    hour: '2-digit',
                    minute: '2-digit',
                  })}
                </div>
                {/* A reason a patient would understand. Never a silent booking. */}
                <p className="text-[12px] mb-2" style={{ color: 'var(--color-muted)' }}>
                  {p.reason}
                </p>
                <button onClick={() => hold(p)} className="micro px-2 py-1 rounded-[6px] hairline">
                  Hold this slot
                </button>
              </div>
            ))}

            {notice && (
              <p className="micro mt-3 pt-3 hairline-t" style={{ color: 'var(--color-faint)' }}>
                {notice}
              </p>
            )}
          </div>
        </section>
      </div>
    </div>
  )
}
