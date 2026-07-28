/**
 * Typed client for the Aria API.
 *
 * Every call carries the bearer token resolved at sign-in. There is no anonymous
 * path and no "current user" in the request body — the server derives identity,
 * tenancy and role from the token, which is what makes multi-tenancy enforceable
 * rather than aspirational.
 */

const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

let token: string | null = localStorage.getItem('aria.token')

export function setToken(next: string | null) {
  token = next
  if (next) localStorage.setItem('aria.token', next)
  else localStorage.removeItem('aria.token')
}

export function getToken() {
  return token
}

export class ApiError extends Error {
  // Written out rather than using constructor parameter properties: the project
  // builds with erasableSyntaxOnly, so type-directed emit is not available.
  readonly status: number
  readonly body?: unknown

  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  })

  if (!response.ok) {
    let body: unknown
    let message = response.statusText
    try {
      body = await response.json()
      const asRecord = body as Record<string, unknown>
      if (typeof asRecord?.error === 'string') message = asRecord.error
    } catch {
      /* a non-JSON error body is still an error; keep the status text */
    }
    throw new ApiError(response.status, message, body)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PATCH', body: JSON.stringify(body) }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  del: <T>(path: string) => request<T>(path, { method: 'DELETE' }),

  /**
   * Server-sent events with the bearer token attached.
   *
   * EventSource cannot send an Authorization header, so this reads the stream
   * directly. It also means a dropped connection surfaces to the caller rather
   * than being silently retried — the encounter banner must never lie about
   * whether capture is actually running.
   */
  stream: async (
    path: string,
    onEvent: (event: string, data: unknown) => void,
    signal?: AbortSignal,
  ) => {
    const response = await fetch(`${BASE}${path}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
      signal,
    })

    if (!response.ok || !response.body) {
      throw new ApiError(response.status, `Stream failed: ${response.statusText}`)
    }

    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''

    for (;;) {
      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })
      const frames = buffer.split('\n\n')
      buffer = frames.pop() ?? ''

      for (const frame of frames) {
        let eventName = 'message'
        let payload = ''
        for (const line of frame.split('\n')) {
          if (line.startsWith('event: ')) eventName = line.slice(7).trim()
          else if (line.startsWith('data: ')) payload += line.slice(6)
        }
        if (!payload) continue
        try {
          onEvent(eventName, JSON.parse(payload))
        } catch {
          /* a malformed frame is skipped, not fatal to the stream */
        }
      }
    }
  },
}

// ── Types mirroring the API contracts ──────────────────────────────────────

export type Identity = {
  doctorId: string
  name: string
  email: string
  department: string
  role: string
  tenantId: string
  patientId: string | null
  accountId: string | null
  /** Which of the three shells to render. Decided by the server so they cannot disagree. */
  surface: 'clinical' | 'patient' | 'admin'
  permissions: {
    canSign: boolean
    mayViewPhi: boolean
    isPatient: boolean
    isClinician: boolean
    canConfigure: boolean
    canApproveAccounts: boolean
  }
}

export type Account = {
  id: string
  email: string
  displayName: string
  role: string
  status: string
  department: string | null
  phone: string | null
  requestedReason: string | null
  linkedDoctorId: string | null
  linkedPatientId: string | null
  createdAt: string
  reviewedBy: string | null
  reviewedAt: string | null
  reviewNote: string | null
  lastSignInAt: string | null
}

export type Linkable = {
  clinicians: { doctorId: string; name: string; department: string; role: string; alreadyLinked: boolean }[]
  patients: { id: string; name: string; mrn: string; dateOfBirth: string; alreadyLinked: boolean }[]
}

export type AssistantSource = { id: string; title: string; citation: string | null }

export type AssistantReply = {
  text: string
  escalated: boolean
  degraded: boolean
  interventions: string[]
  sources: AssistantSource[]
}

export type PortalPatient = {
  id: string
  name: string
  mrn: string
  sex: string
  age: number
  phone: string
  preferredLanguage: string
  allergies: { label: string; severity: string }[]
  conditions: string[]
}

export type PortalAppointment = {
  id: string
  startAt: string
  durationMinutes: number
  reason: string | null
  status: string
  doctor: string
  isPast: boolean
}

export type PortalVisit = {
  id: string
  signedAt: string
  clinician: string
  summary: string | null
  plan: string | null
}

export type PortalMessage = {
  id: string
  direction: string
  body: string
  createdAt: string
  fromClinic: boolean
}

export type SpeechToken =
  | { configured: true; token: string; region: string; phrases: string[] }
  | { configured: false; reason: string }

export type PatientFlag = { label: string; kind: string; severity: string }

export type QueueEntry = {
  id: string
  state: string
  room?: string
  chiefComplaint?: string
  patient: {
    id: string
    name: string
    mrn: string
    sex: string
    age: number
    phone: string
    flags: PatientFlag[]
  }
}

export type Segment = {
  id: string
  speaker: string
  text: string
  startMs: number
  endMs: number
  confidence: number
}

export type Entity = { label: string; code: string; transcriptMs: number; confidence: number }

export type Conflict = {
  drugLabel: string
  allergyLabel: string
  severity: string
  explanation: string
}

export type Extraction = {
  symptoms: Entity[]
  vitals: Entity[]
  medications: Entity[]
  orders: Entity[]
  conflicts: Conflict[]
  degraded: boolean
}

export type Span = {
  id: string
  text: string
  confidence: number
  band: 'Low' | 'Medium' | 'High'
  transcriptStartMs: number | null
  transcriptEndMs: number | null
  acceptedByHuman: boolean
  editedByHuman: boolean
  flagReason: string | null
  hasProvenance: boolean
}

export type Note = {
  id: string
  encounterId: string
  status: string
  templateId: string
  modelVersion: string | null
  promptVersion: string | null
  editDistance: number
  draftUnavailable: boolean
  draftCreatedAt: string
  signedAt: string | null
  signedBy: string | null
  lowConfidenceSpanCount: number
  signable: boolean
  blocker: string | null
  patient: { id: string; name: string; mrn: string; sex: string; age: number; flags: PatientFlag[] }
  sections: { id: string; kind: string; spans: Span[] }[]
  attachedActions: {
    id: string
    kind: string
    description: string
    enabled: boolean
    blockedReason: string | null
  }[]
  codes: { code: string; system: string; display: string; confidence: number }[]
  addenda: { id: string; body: string; createdAt: string; authorId: string }[]
}

export type Thread = {
  id: string
  status: string
  botMuted: boolean
  patient: { id: string; name: string; phone: string }
  lastMessage: string | null
  lastAt: string | null
  windowRemainingMinutes: number | null
  requiresTemplate: boolean
  pendingApproval: boolean
}

export type Message = {
  id: string
  direction: string
  body: string
  templateId: string | null
  status: string
  confidence: number | null
  basis: string | null
  createdAt: string
  sentAt: string | null
  approvedBy: string | null
  canUndo: boolean
  undoSecondsRemaining: number | null
}

export type Escalation = {
  id: string
  threadId: string
  trigger: string
  severity: string
  raisedAt: string
  detectorVersion: string
  patientName: string
  waitingSeconds: number
  slaBreached: boolean
}

export type ChartAnswer = {
  insufficientEvidence: boolean
  scopeStatement: string
  claims: { text: string; sources: { id: string; title: string; citation: string | null }[] }[]
  interventions: string[]
}

export type Evidence = {
  findings: string[]
  considerations: {
    title: string
    strength: number
    suggested: string
    citationId: string
    citation: string
    url: string | null
  }[]
  safetyChecks: string[]
  disclaimer: string
  nothingCited: boolean
  interventions: string[]
  emptyMessage: string | null
}

export type Integration = { name: string; live: boolean; detail: string }

export type AuditRow = {
  id: string
  timestamp: string
  actorId: string
  actorKind: string
  action: string
  targetKind: string | null
  targetId: string | null
  patientId: string | null
  modelVersion: string | null
  promptVersion: string | null
  humanEdits: number | null
  outcome: string
  detailJson: string
  rowHash: string
}

export type OutboxRow = {
  id: string
  noteId: string
  actionType: string
  status: string
  attempts: number
  lastError: string | null
  externalRef: string | null
  createdAt: string
  idempotencyKey: string
}

export type Insights = {
  adoption: Record<string, number>
  quality: Record<string, number>
  trust: {
    acceptanceRate: number | null
    acceptedCount: number
    rejectedCount: number
    provenanceOpened: number
    badSuggestionReports: number
    overTrustAlarm: boolean
    underTrustAlarm: boolean
    healthyBand: { low: number; high: number }
  }
  safety: {
    escalationsRaised: number
    escalationsAcknowledged: number
    escalationsOutstanding: number
    slaBreaches: number
    medianAckSeconds: number | null
    guardrailInterventions: Record<string, number>
    uncitedClaimsRendered: number
  }
}

export type AutonomyRow = {
  id: string
  scopeKind: string
  scopeId: string
  intent: string
  mode: string
  approvedBy: string | null
  expiresAt: string | null
  immutable: boolean
}
