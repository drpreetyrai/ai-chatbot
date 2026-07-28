import { useState } from 'react'
import { api, setToken, type Identity } from '../../lib/api'

type Mode = 'signin' | 'signup'

/**
 * Sign in and register.
 *
 * Registration deliberately does NOT sign you in. It creates a pending account and
 * says so — the whole access model rests on a human approving it first, and a screen
 * that quietly logged you in afterwards would undermine that in the one place users
 * form their mental model of how the system works.
 */
export function AuthScreens({ onSignedIn }: { onSignedIn: (me: Identity) => void }) {
  const [mode, setMode] = useState<Mode>('signin')

  return (
    <div className="min-h-screen grid place-items-center px-4 py-10">
      <div className="w-full max-w-md">
        <header className="text-center mb-7">
          <h1 className="text-[28px] font-semibold tracking-[0.3em] mb-1">ARIA</h1>
          <p style={{ color: 'var(--color-muted)' }}>Ambient assistant for clinical teams</p>
        </header>

        <div className="rounded-[14px] hairline overflow-hidden" style={{ background: 'var(--color-surface)' }}>
          <div className="flex hairline-b">
            {(['signin', 'signup'] as const).map((m) => (
              <button
                key={m}
                onClick={() => setMode(m)}
                className="flex-1 py-2.5 text-[13px]"
                style={{
                  background: mode === m ? 'var(--color-surface)' : 'var(--color-sunken)',
                  fontWeight: mode === m ? 600 : 400,
                  color: mode === m ? 'var(--color-ink)' : 'var(--color-muted)',
                }}
              >
                {m === 'signin' ? 'Sign in' : 'Create an account'}
              </button>
            ))}
          </div>

          {mode === 'signin' ? <SignInForm onSignedIn={onSignedIn} /> : <SignUpForm onDone={() => setMode('signin')} />}
        </div>

        <p className="text-center micro mt-5" style={{ color: 'var(--color-faint)' }}>
          Northbridge Health · centralindia · data stays in region
        </p>
      </div>
    </div>
  )
}

function SignInForm({ onSignedIn }: { onSignedIn: (me: Identity) => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      const result = await api.post<{ token: string }>('/v1/auth/signin', { email, password })
      setToken(result.token)
      onSignedIn(await api.get<Identity>('/v1/auth/me'))
    } catch (err) {
      // The server distinguishes "wrong credentials" from "awaiting approval", and the
      // second is genuinely useful to show — telling someone their password is wrong
      // when it is not just sends them round the reset loop forever.
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} className="p-5">
      <Field label="Email" type="email" value={email} onChange={setEmail} autoFocus required />
      <Field label="Password" type="password" value={password} onChange={setPassword} required />

      {error && (
        <p className="text-[13px] mb-3 p-2 rounded-[8px]"
           style={{ color: 'var(--color-reviewtext)', background: 'color-mix(in srgb, var(--color-review) 10%, transparent)' }}>
          {error}
        </p>
      )}

      <button
        type="submit"
        disabled={busy || !email || !password}
        className="w-full py-2 rounded-[10px] text-[14px] text-white disabled:opacity-40"
        style={{ background: 'var(--color-pulse)' }}
      >
        {busy ? 'Signing in…' : 'Sign in'}
      </button>

      <div className="mt-4 pt-3 hairline-t">
        <p className="micro mb-1" style={{ color: 'var(--color-faint)' }}>
          First run — the one bootstrap account
        </p>
        <p className="text-[12px]" style={{ color: 'var(--color-muted)' }}>
          <code className="mono">admin@northbridge.health</code> · <code className="mono">AriaAdmin!2026</code>
          <br />
          Everyone else registers and waits for the administrator to approve them.
        </p>
      </div>
    </form>
  )
}

function SignUpForm({ onDone }: { onDone: () => void }) {
  const [form, setForm] = useState({
    displayName: '', email: '', password: '', role: 'Patient',
    department: '', phone: '', mrn: '', reason: '',
  })
  const [error, setError] = useState<string | null>(null)
  const [submitted, setSubmitted] = useState(false)
  const [busy, setBusy] = useState(false)

  const set = (key: keyof typeof form) => (value: string) => setForm((f) => ({ ...f, [key]: value }))

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.post('/v1/auth/signup', form)
      setSubmitted(true)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (submitted) {
    return (
      <div className="p-5">
        <div className="p-3 rounded-[10px] mb-4"
             style={{ background: 'color-mix(in srgb, var(--color-ok) 8%, transparent)' }}>
          <p className="text-[14px] font-medium mb-1" style={{ color: 'var(--color-ok)' }}>
            ✓ Registration received
          </p>
          <p className="text-[13px]" style={{ color: 'var(--color-muted)' }}>
            An administrator will review it. You will be able to sign in once your account is
            approved and linked to your record — we do not take your word for who you are.
          </p>
        </div>
        <button onClick={onDone} className="w-full py-2 rounded-[10px] text-[13px] hairline">
          Back to sign in
        </button>
      </div>
    )
  }

  const isPatient = form.role === 'Patient'

  return (
    <form onSubmit={submit} className="p-5">
      <label className="block mb-3">
        <span className="micro block mb-1" style={{ color: 'var(--color-faint)' }}>I am a</span>
        <div className="flex gap-1.5">
          {['Patient', 'Clinician', 'Coordinator'].map((r) => (
            <button
              key={r}
              type="button"
              onClick={() => set('role')(r)}
              className="flex-1 py-1.5 rounded-[8px] text-[13px] hairline"
              style={form.role === r
                ? { background: 'var(--color-pulse)', color: '#fff', borderColor: 'transparent' }
                : undefined}
            >
              {r === 'Clinician' ? 'Doctor' : r}
            </button>
          ))}
        </div>
      </label>

      <Field label="Full name" value={form.displayName} onChange={set('displayName')} required />
      <Field label="Email" type="email" value={form.email} onChange={set('email')} required />
      <Field label="Password" type="password" value={form.password} onChange={set('password')}
             hint="At least 10 characters" required />

      {isPatient ? (
        <>
          <Field label="Phone" value={form.phone} onChange={set('phone')} />
          <Field
            label="Your MRN, if you know it"
            value={form.mrn}
            onChange={set('mrn')}
            hint="The administrator verifies this before linking — it is a claim, not a key."
          />
        </>
      ) : (
        <Field label="Department" value={form.department} onChange={set('department')} />
      )}

      <Field
        label={isPatient ? 'Anything the practice should know' : 'Registration / GMC number and role'}
        value={form.reason}
        onChange={set('reason')}
        hint="This is what the administrator reads when deciding."
      />

      {error && (
        <p className="text-[13px] mb-3" style={{ color: 'var(--color-dangertext)' }}>
          {error}
        </p>
      )}

      <button
        type="submit"
        disabled={busy || !form.email || !form.password || !form.displayName}
        className="w-full py-2 rounded-[10px] text-[14px] text-white disabled:opacity-40"
        style={{ background: 'var(--color-pulse)' }}
      >
        {busy ? 'Submitting…' : 'Request an account'}
      </button>
    </form>
  )
}

function Field({
  label, value, onChange, type = 'text', hint, required, autoFocus,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  type?: string
  hint?: string
  required?: boolean
  autoFocus?: boolean
}) {
  return (
    <label className="block mb-3">
      <span className="micro block mb-1" style={{ color: 'var(--color-faint)' }}>{label}</span>
      <input
        type={type}
        value={value}
        required={required}
        autoFocus={autoFocus}
        onChange={(e) => onChange(e.target.value)}
        className="w-full px-3 py-2 rounded-[8px] hairline bg-transparent text-[14px]"
      />
      {hint && (
        <span className="micro block mt-1" style={{ color: 'var(--color-faint)' }}>{hint}</span>
      )}
    </label>
  )
}
