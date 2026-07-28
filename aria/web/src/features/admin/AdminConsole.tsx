import { useEffect, useState } from 'react'
import { api, setToken, type Account, type Identity, type Linkable } from '../../lib/api'
import { Admin as GovernanceTabs, InsightsScreen } from '../Admin'

type Tab = 'approvals' | 'accounts' | 'governance' | 'insights'

/**
 * The administrator's surface.
 *
 * Their first job is the approval queue, so it is the landing tab and it carries a
 * count — an unreviewed registration is somebody unable to work, and it should nag.
 *
 * The admin has every configuration and audit privilege and no clinical access at all:
 * the API returns 403 on patient data for this role, which is the RBAC matrix from the
 * plan rather than an oversight.
 */
export function AdminConsole({ me }: { me: Identity }) {
  const [tab, setTab] = useState<Tab>('approvals')
  const [pendingCount, setPendingCount] = useState(0)

  const refreshCount = () =>
    api.get<Account[]>('/v1/admin/accounts/pending')
      .then((rows) => setPendingCount(rows.length))
      .catch(() => {})

  useEffect(() => {
    refreshCount()
    const timer = setInterval(refreshCount, 20_000)
    return () => clearInterval(timer)
  }, [])

  const tabs: [Tab, string, number?][] = [
    ['approvals', 'Approvals', pendingCount],
    ['accounts', 'Accounts'],
    ['governance', 'Governance'],
    ['insights', 'Insights'],
  ]

  return (
    <div className="min-h-screen flex flex-col">
      <header className="hairline-b px-4 h-14 flex items-center gap-3 shrink-0"
              style={{ background: 'var(--color-surface)' }}>
        <span className="font-semibold tracking-[0.18em] text-[13px]">ARIA</span>
        <span className="micro px-2 py-0.5 rounded-[6px]"
              style={{ background: 'var(--color-sunken)', color: 'var(--color-muted)' }}>
          Administrator
        </span>
        <div className="flex-1" />
        <span className="micro" style={{ color: 'var(--color-faint)' }}>
          full configuration and audit · no clinical access
        </span>
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

      <nav className="hairline-b flex px-2 shrink-0" style={{ background: 'var(--color-surface)' }}>
        {tabs.map(([key, label, count]) => (
          <button
            key={key}
            onClick={() => setTab(key)}
            className="px-3 py-2.5 text-[13px] flex items-center gap-1.5"
            style={{
              fontWeight: tab === key ? 600 : 400,
              color: tab === key ? 'var(--color-ink)' : 'var(--color-muted)',
              borderBottom: tab === key ? '2px solid var(--color-pulse)' : '2px solid transparent',
            }}
          >
            {label}
            {count ? (
              /* --color-review is a RULE colour: white on it measures 3.64:1, which fails
                 AA for text this small. --color-reviewtext is the same amber darkened to
                 5.2:1. The badge only renders when something is pending, which is why this
                 survived until an accessibility pass ran against a non-empty queue. */
              <span className="micro px-1.5 rounded-full text-white"
                    style={{ background: 'var(--color-reviewtext)' }}>
                {count}
              </span>
            ) : null}
          </button>
        ))}
      </nav>

      <main className="flex-1 min-h-0 overflow-auto">
        {tab === 'approvals' && <Approvals onChanged={refreshCount} />}
        {tab === 'accounts' && <Accounts />}
        {tab === 'governance' && <GovernanceTabs />}
        {tab === 'insights' && <InsightsScreen />}
      </main>
    </div>
  )
}

function Approvals({ onChanged }: { onChanged: () => void }) {
  const [pending, setPending] = useState<Account[]>([])
  const [linkable, setLinkable] = useState<Linkable | null>(null)
  const [active, setActive] = useState<Account | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = () => {
    api.get<Account[]>('/v1/admin/accounts/pending').then(setPending).catch(() => {})
    api.get<Linkable>('/v1/admin/accounts/linkable').then(setLinkable).catch(() => {})
  }

  useEffect(load, [])

  async function decide(account: Account, link: { doctorId?: string; patientId?: string }, note: string) {
    setNotice(null)
    try {
      await api.post(`/v1/admin/accounts/${account.id}/approve`, {
        linkedDoctorId: link.doctorId ?? null,
        linkedPatientId: link.patientId ?? null,
        note,
      })
      setNotice(`${account.displayName} approved and linked.`)
      setActive(null)
      load()
      onChanged()
    } catch (e) {
      setNotice((e as Error).message)
    }
  }

  async function reject(account: Account, note: string) {
    await api.post(`/v1/admin/accounts/${account.id}/reject`, { note })
    setNotice(`${account.displayName} rejected.`)
    setActive(null)
    load()
    onChanged()
  }

  return (
    <div className="p-5 max-w-4xl">
      <h1 className="text-[20px] font-semibold mb-1">Approvals</h1>
      <p className="text-[13px] mb-4" style={{ color: 'var(--color-muted)' }}>
        Nobody signs in until you approve them. Approving is also where you <b>link</b> the
        account to a real record — what somebody typed about themselves at registration is a
        claim to be checked, never a key.
      </p>

      {notice && (
        <p className="text-[13px] mb-3 p-2 rounded-[8px]"
           style={{ background: 'var(--color-sunken)', color: 'var(--color-muted)' }}>
          {notice}
        </p>
      )}

      {pending.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--color-faint)' }}>
          Nothing waiting. New registrations appear here.
        </p>
      ) : (
        pending.map((account) => (
          <article key={account.id} className="rounded-[14px] hairline p-4 mb-3"
                   style={{ background: 'var(--color-surface)' }}>
            <div className="flex items-start justify-between gap-4 flex-wrap">
              <div className="min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-[15px] font-medium">{account.displayName}</span>
                  <span className="micro px-1.5 py-0.5 rounded-[6px] hairline">
                    {account.role === 'Clinician' ? 'Doctor' : account.role}
                  </span>
                </div>
                <p className="text-[13px] mt-0.5" style={{ color: 'var(--color-muted)' }}>
                  {account.email}
                  {account.phone && ` · ${account.phone}`}
                  {account.department && ` · ${account.department}`}
                </p>
                {account.requestedReason && (
                  <p className="text-[13px] mt-1.5 p-2 rounded-[8px]" style={{ background: 'var(--color-sunken)' }}>
                    {account.requestedReason}
                  </p>
                )}
                <p className="micro mt-1" style={{ color: 'var(--color-faint)' }}>
                  Requested {new Date(account.createdAt).toLocaleString()}
                </p>
              </div>

              <button
                onClick={() => setActive(active?.id === account.id ? null : account)}
                className="px-3 py-1.5 rounded-[8px] text-[13px] text-white shrink-0"
                style={{ background: 'var(--color-pulse)' }}
              >
                Review
              </button>
            </div>

            {active?.id === account.id && linkable && (
              <ReviewPanel
                account={account}
                linkable={linkable}
                onApprove={(link, note) => decide(account, link, note)}
                onReject={(note) => reject(account, note)}
              />
            )}
          </article>
        ))
      )}
    </div>
  )
}

function ReviewPanel({
  account, linkable, onApprove, onReject,
}: {
  account: Account
  linkable: Linkable
  onApprove: (link: { doctorId?: string; patientId?: string }, note: string) => void
  onReject: (note: string) => void
}) {
  const [selected, setSelected] = useState('')
  const [note, setNote] = useState('')

  const isClinician = account.role === 'Clinician' || account.role === 'Coordinator'
  const options = isClinician ? linkable.clinicians : linkable.patients

  return (
    <div className="mt-4 pt-4 hairline-t">
      <h3 className="micro mb-2" style={{ color: 'var(--color-faint)' }}>
        {isClinician ? 'Link to a clinician record' : 'Link to a patient record'}
      </h3>

      <div className="max-h-52 overflow-auto rounded-[10px] hairline mb-3">
        {options.map((option) => {
          const id = 'doctorId' in option ? option.doctorId : option.id
          const label = 'doctorId' in option
            ? `${option.name} · ${option.department} · ${option.doctorId}`
            : `${option.name} · MRN ${option.mrn} · born ${new Date(option.dateOfBirth).toLocaleDateString()}`

          return (
            <label
              key={id}
              className="flex items-center gap-2 px-3 py-2 hairline-b last:border-b-0 text-[13px] cursor-pointer"
              style={{ background: selected === id ? 'var(--color-sunken)' : undefined }}
            >
              <input type="radio" name={`link-${account.id}`} checked={selected === id}
                     onChange={() => setSelected(id)} />
              <span className="flex-1">{label}</span>
              {option.alreadyLinked && (
                // Shown rather than hidden: two people claiming one identity is exactly
                // what an approver needs to notice.
                <span className="micro" style={{ color: 'var(--color-reviewtext)' }}>
                  already linked to another account
                </span>
              )}
            </label>
          )
        })}
      </div>

      <label className="block mb-3">
        <span className="micro block mb-1" style={{ color: 'var(--color-faint)' }}>
          How did you verify this? (recorded in the audit log)
        </span>
        <input
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder={isClinician ? 'GMC number checked against the register' : 'DOB and MRN confirmed at reception'}
          className="w-full px-3 py-2 rounded-[8px] hairline bg-transparent text-[13px]"
        />
      </label>

      <div className="flex gap-2 flex-wrap">
        <button
          onClick={() => onApprove(isClinician ? { doctorId: selected } : { patientId: selected }, note)}
          disabled={!selected}
          className="px-3 py-1.5 rounded-[8px] text-[13px] text-white disabled:opacity-40"
          style={{ background: 'var(--color-ok)' }}
        >
          Approve &amp; link
        </button>
        <button
          onClick={() => onReject(note)}
          className="px-3 py-1.5 rounded-[8px] text-[13px] hairline"
          style={{ color: 'var(--color-dangertext)' }}
        >
          Reject
        </button>
        {!selected && (
          <span className="micro self-center" style={{ color: 'var(--color-faint)' }}>
            Choose a record — an account cannot be approved without one.
          </span>
        )}
      </div>
    </div>
  )
}

function Accounts() {
  const [rows, setRows] = useState<Account[]>([])
  const load = () => { api.get<Account[]>('/v1/admin/accounts').then(setRows).catch(() => {}) }
  useEffect(load, [])

  async function setStatus(id: string, status: string) {
    await api.post(`/v1/admin/accounts/${id}/status`, { status })
    load()
  }

  const colour = (status: string) =>
    status === 'Approved' ? 'var(--color-ok)'
      : status === 'Pending' ? 'var(--color-reviewtext)'
      : 'var(--color-dangertext)'

  return (
    <div className="p-5 max-w-5xl">
      <h1 className="text-[20px] font-semibold mb-1">Accounts</h1>
      <p className="text-[13px] mb-4" style={{ color: 'var(--color-muted)' }}>
        Suspending an account revokes its live sessions immediately — it does not wait for the
        current token to expire.
      </p>

      <div className="rounded-[14px] hairline overflow-x-auto" style={{ background: 'var(--color-surface)' }}>
        <table className="w-full text-left text-[13px]">
          <thead>
            <tr className="hairline-b">
              {['name', 'email', 'role', 'status', 'linked to', 'last sign-in', ''].map((h) => (
                <th key={h} className="micro px-3 py-2 font-medium" style={{ color: 'var(--color-faint)' }}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((a) => (
              <tr key={a.id} className="hairline-b last:border-b-0">
                <td className="px-3 py-2">{a.displayName}</td>
                <td className="px-3 py-2" style={{ color: 'var(--color-muted)' }}>{a.email}</td>
                <td className="px-3 py-2">{a.role === 'Clinician' ? 'Doctor' : a.role}</td>
                <td className="px-3 py-2" style={{ color: colour(a.status) }}>{a.status}</td>
                <td className="px-3 py-2 mono" style={{ color: 'var(--color-faint)' }}>
                  {a.linkedDoctorId ?? a.linkedPatientId ?? '—'}
                </td>
                <td className="px-3 py-2" style={{ color: 'var(--color-faint)' }}>
                  {a.lastSignInAt ? new Date(a.lastSignInAt).toLocaleString() : 'never'}
                </td>
                <td className="px-3 py-2">
                  {a.status === 'Approved' ? (
                    <button onClick={() => setStatus(a.id, 'Suspended')} className="micro underline"
                            style={{ color: 'var(--color-dangertext)' }}>
                      suspend
                    </button>
                  ) : a.status === 'Suspended' ? (
                    <button onClick={() => setStatus(a.id, 'Approved')} className="micro underline"
                            style={{ color: 'var(--color-ok)' }}>
                      restore
                    </button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
