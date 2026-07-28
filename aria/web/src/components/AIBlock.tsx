import type { ReactNode } from 'react'

/**
 * ═══════════════════════════════════════════════════════════════════════════
 *  The signature component of the product (wireframe §8).
 *
 *  Any surface showing machine output MUST compose AIBlock + ConfidenceMeter +
 *  ProvenanceLink. That is not a convention, it is the rule that lets a new AI
 *  feature inherit the trust UI for free — and stops one shipping without it.
 *
 *  Two visual states carry real meaning:
 *    • draft  — mint left rule + "AI draft" chip. You can tell at a glance what
 *               a machine wrote and what a person owns.
 *    • signed — the mint disappears entirely. The artefact becomes neutral and
 *               permanent, because visual permanence mirrors legal permanence.
 * ═══════════════════════════════════════════════════════════════════════════
 */

export type AIState = 'draft' | 'accepted' | 'rejected' | 'signed'

export function AIBlock({
  state = 'draft',
  confidence,
  flagReason,
  provenance,
  accepted = false,
  onAccept,
  onRewrite,
  onReport,
  children,
}: {
  state?: AIState
  confidence?: number
  flagReason?: string | null
  provenance?: ReactNode
  /** A human has already made the call on this passage. */
  accepted?: boolean
  onAccept?: () => void
  onRewrite?: () => void
  onReport?: () => void
  children: ReactNode
}) {
  const signed = state === 'signed'
  const band = confidence === undefined ? undefined : bandOf(confidence)
  // An accepted passage is no longer a passage needing review — the amber highlight and
  // the Accept button both go. Leaving them up meant a clinician working through several
  // flagged spans had no way to see which ones they had already decided about, which is
  // the entire job the review gate exists to help them do.
  const needsReview = band === 'Low' && state === 'draft' && !accepted

  return (
    <div
      className="relative pl-4 py-1"
      // Screen readers get the same three facts the eye does.
      aria-label={
        signed
          ? 'Signed note content'
          : `AI draft${band ? `, confidence ${band.toLowerCase()}` : ''}${
              provenance ? ', provenance available' : ''
            }`
      }
    >
      {/* The mint left rule. Absent once signed — that absence is the signal. */}
      {!signed && (
        <span
          aria-hidden
          className="absolute left-0 top-0 bottom-0 w-[3px] rounded-full"
          style={{ background: needsReview ? 'var(--color-review)' : 'var(--color-mint)' }}
        />
      )}

      <div className={needsReview ? 'bg-[color-mix(in_srgb,var(--color-review)_6%,transparent)] -mx-1 px-1 rounded' : ''}>
        {children}
      </div>

      {(band || provenance || onAccept) && !signed && (
        <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
          {band && <ConfidenceMeter confidence={confidence!} />}
          {provenance}

          {band === 'Low' && accepted && !signed && (
            <span className="micro" style={{ color: 'var(--color-ok)' }}>
              ✓ You accepted this passage
            </span>
          )}

          {needsReview && (
            <>
              {/* Low confidence forces an explicit decision. It cannot be bulk-accepted. */}
              <button
                onClick={onAccept}
                className="micro px-2 py-0.5 rounded-[6px] hairline hover:bg-[var(--color-sunken)]"
                style={{ color: 'var(--color-ok)' }}
              >
                Accept
              </button>
              <button
                onClick={onRewrite}
                className="micro px-2 py-0.5 rounded-[6px] hairline hover:bg-[var(--color-sunken)]"
              >
                Rewrite
              </button>
            </>
          )}

          {onReport && (
            <button
              onClick={onReport}
              className="micro ml-auto opacity-50 hover:opacity-100"
              title="Reports this to the clinical review queue and the evaluation set"
            >
              Report
            </button>
          )}
        </div>
      )}

      {needsReview && flagReason && (
        <p className="mt-1 text-[12px] leading-4" style={{ color: 'var(--color-reviewtext)' }}>
          Why flagged: {flagReason}
        </p>
      )}
    </div>
  )
}

export function bandOf(confidence: number): 'Low' | 'Medium' | 'High' {
  return confidence >= 0.85 ? 'High' : confidence >= 0.65 ? 'Medium' : 'Low'
}

/**
 * Three bands, never a bare percentage (wireframe §8).
 *
 * A raw decimal invites false precision — 0.61 versus 0.64 is not a distinction
 * a clinician can act on, but "low, verify this" is.
 */
export function ConfidenceMeter({ confidence }: { confidence: number }) {
  const band = bandOf(confidence)
  const filled = band === 'High' ? 4 : band === 'Medium' ? 3 : 2
  const colour =
    band === 'Low' ? 'var(--color-review)' : band === 'Medium' ? 'var(--color-muted)' : 'var(--color-minttext)'

  return (
    <span className="inline-flex items-center gap-1.5" title={`Model confidence: ${band.toLowerCase()}`}>
      <span className="flex gap-[2px]" aria-hidden>
        {[0, 1, 2, 3].map((i) => (
          <span
            key={i}
            className="w-[5px] h-[5px] rounded-full"
            style={{ background: i < filled ? colour : 'var(--color-hairline)' }}
          />
        ))}
      </span>
      <span className="micro" style={{ color: colour }}>
        {band}
      </span>
    </span>
  )
}

/**
 * Makes "show your work" structural rather than a promise.
 *
 * Clicking replays the exact transcript window the claim came from, so a
 * clinician verifies in seconds instead of re-reading everything. The click is
 * instrumented: if nobody ever opens provenance, the pattern is not working and
 * we should know that rather than assume it.
 */
export function ProvenanceLink({
  startMs,
  endMs,
  onOpen,
}: {
  startMs: number | null
  endMs: number | null
  onOpen?: () => void
}) {
  if (startMs === null || endMs === null) {
    // No source → we say so rather than rendering a dead link.
    return (
      <span className="micro" style={{ color: 'var(--color-dangertext)' }}>
        No source
      </span>
    )
  }

  const seconds = Math.max(1, Math.round((endMs - startMs) / 1000))

  return (
    <button
      onClick={onOpen}
      className="micro underline decoration-dotted underline-offset-2 hover:no-underline"
      style={{ color: 'var(--color-pulse)' }}
      title={`Transcript ${fmt(startMs)} → ${fmt(endMs)}`}
    >
      ▸ play {seconds}s
    </button>
  )
}

export function fmt(ms: number) {
  const total = Math.floor(ms / 1000)
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`
}

/** The `▮ AI draft` chip. One glyph the eye learns: "this needs my judgement." */
export function AIChip({ label = 'AI draft' }: { label?: string }) {
  return (
    <span
      className="micro inline-flex items-center gap-1 px-1.5 py-0.5 rounded-[6px]"
      style={{
        color: 'var(--color-minttext)',
        background: 'color-mix(in srgb, var(--color-mint) 10%, transparent)',
      }}
    >
      ▮ {label}
    </span>
  )
}
