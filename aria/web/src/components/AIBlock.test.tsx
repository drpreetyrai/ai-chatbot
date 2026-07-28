import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AIBlock, AIChip, ConfidenceMeter, ProvenanceLink, bandOf, fmt } from './AIBlock'

/**
 * The trust components carry the product's three non-negotiables into the UI:
 * you can tell at a glance what a machine wrote, how sure it is, and where it
 * came from. These tests assert those properties rather than the markup that
 * happens to express them today.
 */

describe('confidence banding', () => {
  it.each([
    [0.95, 'High'],
    [0.85, 'High'],
    [0.84, 'Medium'],
    [0.65, 'Medium'],
    [0.64, 'Low'],
    [0.0, 'Low'],
  ])('%s maps to %s', (confidence, expected) => {
    expect(bandOf(confidence)).toBe(expected)
  })

  it('shows a band, never a raw percentage', () => {
    // A bare decimal invites false precision — 0.61 vs 0.64 is not a distinction a
    // clinician can act on, but "low, verify this" is.
    render(<ConfidenceMeter confidence={0.61} />)

    expect(screen.getByText('Low')).toBeInTheDocument()
    expect(screen.queryByText(/61/)).not.toBeInTheDocument()
    expect(screen.queryByText(/%/)).not.toBeInTheDocument()
  })
})

describe('AIBlock', () => {
  it('marks a draft as machine-authored for screen readers', () => {
    render(
      <AIBlock state="draft" confidence={0.9}>
        <p>Fever for three days.</p>
      </AIBlock>,
    )

    const block = screen.getByLabelText(/AI draft/i)
    expect(block).toBeInTheDocument()
    expect(block.getAttribute('aria-label')).toMatch(/confidence high/i)
  })

  it('drops the AI marking once signed', () => {
    // Visual permanence mirrors legal permanence: after signature the artefact is
    // neutral and immutable, and must no longer announce itself as a draft.
    render(
      <AIBlock state="signed" confidence={0.9}>
        <p>Fever for three days.</p>
      </AIBlock>,
    )

    expect(screen.queryByLabelText(/AI draft/i)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/signed note/i)).toBeInTheDocument()
  })

  it('forces an explicit decision on a low-confidence claim', () => {
    // Low confidence cannot be passively accepted — the clinician must act.
    render(
      <AIBlock state="draft" confidence={0.61} onAccept={vi.fn()} onRewrite={vi.fn()}>
        <p>Possibly productive cough.</p>
      </AIBlock>,
    )

    expect(screen.getByRole('button', { name: /accept/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /rewrite/i })).toBeInTheDocument()
  })

  it('offers no accept affordance when confidence is high', () => {
    render(
      <AIBlock state="draft" confidence={0.95} onAccept={vi.fn()}>
        <p>Temp 38.4 °C.</p>
      </AIBlock>,
    )

    expect(screen.queryByRole('button', { name: /^accept$/i })).not.toBeInTheDocument()
  })

  it('explains why a claim was flagged, in the clinician’s own terms', () => {
    render(
      <AIBlock
        state="draft"
        confidence={0.61}
        flagReason="Overlapping speech and ambiguous phrasing in the source audio."
        onAccept={vi.fn()}
      >
        <p>Possibly productive cough.</p>
      </AIBlock>,
    )

    expect(screen.getByText(/overlapping speech/i)).toBeInTheDocument()
  })

  it('reports a bad suggestion in one tap', async () => {
    const onReport = vi.fn()
    render(
      <AIBlock state="draft" confidence={0.9} onReport={onReport}>
        <p>Community-acquired pneumonia.</p>
      </AIBlock>,
    )

    await userEvent.click(screen.getByRole('button', { name: /report/i }))
    expect(onReport).toHaveBeenCalledOnce()
  })

  it('offers no report affordance on a signed note', () => {
    // A signed note is the record. Feedback belongs on the draft, before it becomes one.
    render(
      <AIBlock state="signed" confidence={0.9}>
        <p>Signed content.</p>
      </AIBlock>,
    )

    expect(screen.queryByRole('button', { name: /report/i })).not.toBeInTheDocument()
  })
})

describe('ProvenanceLink', () => {
  it('says plainly when a claim has no source', () => {
    // The alternative — a dead link, or nothing at all — is how "show your work"
    // quietly becomes a promise instead of a property.
    render(<ProvenanceLink startMs={null} endMs={null} />)

    expect(screen.getByText(/no source/i)).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('offers playback of the exact window the claim came from', async () => {
    const onOpen = vi.fn()
    render(<ProvenanceLink startMs={25_000} endMs={31_500} onOpen={onOpen} />)

    const link = screen.getByRole('button', { name: /play 7s/i })
    expect(link).toHaveAttribute('title', expect.stringContaining('00:25'))

    await userEvent.click(link)
    expect(onOpen).toHaveBeenCalledOnce()
  })
})

describe('AIChip', () => {
  it('renders the one glyph the eye learns', () => {
    render(<AIChip />)
    expect(screen.getByText(/▮ AI draft/)).toBeInTheDocument()
  })
})

describe('timestamp formatting', () => {
  it.each([
    [0, '00:00'],
    [25_000, '00:25'],
    [95_000, '01:35'],
    [3_600_000, '60:00'],
  ])('%sms → %s', (ms, expected) => {
    expect(fmt(ms)).toBe(expected)
  })
})

describe('a passage a human has already decided about', () => {
  it('stops asking, and says who decided', () => {
    render(
      <AIBlock state="draft" confidence={0.55} accepted onAccept={vi.fn()} onRewrite={vi.fn()}>
        Chest X-ray requested.
      </AIBlock>,
    )

    // The clinician working through several flagged passages must be able to see which
    // ones are done. Leaving the button up made that impossible.
    expect(screen.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument()
    expect(screen.getByText(/you accepted this passage/i)).toBeInTheDocument()
  })

  it('still asks when nobody has decided yet', () => {
    render(
      <AIBlock state="draft" confidence={0.55} onAccept={vi.fn()} onRewrite={vi.fn()}>
        Chest X-ray requested.
      </AIBlock>,
    )

    expect(screen.getByRole('button', { name: 'Accept' })).toBeInTheDocument()
  })
})
