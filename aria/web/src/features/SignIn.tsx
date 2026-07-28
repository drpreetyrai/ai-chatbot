import { useEffect, useState } from 'react'
import { api, setToken, type Identity } from '../lib/api'

type TeamMember = {
  doctorId: string
  name: string
  email: string
  department: string
  role: string
  calendarConnected: boolean
}

/**
 * Sign-in (wireframe S-01).
 *
 * Identity is the tenancy boundary: it resolves doctor_id · name · email ·
 * department and, with them, the right calendar and messaging sender. One
 * identity, three integrations, zero manual configuration.
 *
 * With Entra ID configured this is the SSO button. Without it, the seeded team
 * is offered so you can experience the product as each role and watch the
 * permissions genuinely differ — an admin cannot open a chart, a coordinator
 * cannot sign.
 */
export function SignIn({ onSignedIn }: { onSignedIn: (me: Identity) => void }) {
  const [team, setTeam] = useState<TeamMember[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .get<TeamMember[]>('/v1/auth/team')
      .then(setTeam)
      .catch((e) => setError(e.message))
  }, [])

  async function signIn(doctorId: string) {
    setBusy(doctorId)
    setError(null)
    try {
      const result = await api.post<{ token: string; identity: Identity }>('/v1/auth/dev-signin', { doctorId })
      setToken(result.token)
      const me = await api.get<Identity>('/v1/auth/me')
      onSignedIn(me)
    } catch (e) {
      setError((e as Error).message)
      setBusy(null)
    }
  }

  return (
    <div className="min-h-screen grid place-items-center px-4">
      <div className="w-full max-w-md">
        <div className="text-center mb-8">
          <h1 className="text-[28px] font-semibold tracking-[0.3em] mb-1">ARIA</h1>
          <p style={{ color: 'var(--color-muted)' }}>Ambient assistant for clinical teams</p>
        </div>

        <div className="rounded-[14px] hairline p-5" style={{ background: 'var(--color-surface)' }}>
          <div className="micro mb-3" style={{ color: 'var(--color-faint)' }}>
            Development sign-in · choose a role to experience
          </div>

          {team.map((m) => (
            <button
              key={m.doctorId}
              onClick={() => signIn(m.doctorId)}
              disabled={busy !== null}
              className="w-full text-left px-3 py-2.5 rounded-[10px] hairline mb-2 hover:bg-[var(--color-sunken)] disabled:opacity-50 flex items-center gap-3"
            >
              <span
                className="w-8 h-8 rounded-full grid place-items-center micro shrink-0"
                style={{ background: 'var(--color-sunken)' }}
              >
                {m.name.split(' ').map((p) => p[0]).slice(-2).join('')}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block text-[14px]">{m.name}</span>
                <span className="block micro" style={{ color: 'var(--color-faint)' }}>
                  {m.doctorId} · {m.department} · {m.role}
                </span>
              </span>
              {busy === m.doctorId && <span className="micro">…</span>}
            </button>
          ))}

          {error && (
            <p className="mt-3 text-[13px]" style={{ color: 'var(--color-dangertext)' }}>
              {error}
            </p>
          )}

          <p className="micro mt-4 pt-3 hairline-t" style={{ color: 'var(--color-faint)' }}>
            Second factor required for PHI access · set AZURE_TENANT_ID in .env to switch to SSO
          </p>
        </div>

        <p className="text-center micro mt-5" style={{ color: 'var(--color-faint)' }}>
          Northbridge Health · centralindia · data stays in region
        </p>
      </div>
    </div>
  )
}
