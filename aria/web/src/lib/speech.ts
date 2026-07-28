import * as sdk from 'microsoft-cognitiveservices-speech-sdk'
import { api, type SpeechToken } from './api'

export type Recognised = {
  text: string
  isFinal: boolean
  offsetMs: number
  durationMs: number
  /** Mean word-level confidence, or 1 when the service did not report per-word scores. */
  confidence: number
  /**
   * Who said it — "Dr." or "Pt.", resolved from Azure's anonymous speaker ids.
   * Null when diarisation produced nothing for this utterance.
   */
  speaker: string | null
}

/** The two labels a consultation transcript uses. Correctable in the UI. */
export const DOCTOR = 'Dr.'
export const PATIENT = 'Pt.'

/**
 * Real ambient capture: the browser streams microphone audio straight to Azure AI
 * Speech using a short-lived token minted by our API.
 *
 * Audio never touches our servers. That is not only faster — it means PHI-bearing
 * audio has one fewer place to be retained by accident, which is exactly the promise
 * on the consent chip.
 *
 * Returns null when Speech is not configured, so the caller can fall back to the
 * scripted consultation and SAY it is doing so rather than silently pretending.
 */
export async function startTranscription(
  onRecognised: (result: Recognised) => void,
  onError: (message: string) => void,
): Promise<{ stop: () => Promise<void>; swapSpeakers: () => void } | null> {
  let issued: SpeechToken
  try {
    issued = await api.get<SpeechToken>('/v1/speech/token')
  } catch (e) {
    onError(`Could not reach the speech service: ${(e as Error).message}`)
    return null
  }

  if (!issued.configured) {
    onError(issued.reason)
    return null
  }

  const config = sdk.SpeechConfig.fromAuthorizationToken(issued.token, issued.region)
  config.speechRecognitionLanguage = 'en-IN'

  // Word-level confidence is what drives the low-confidence spans in the note. Without
  // it every passage looks equally certain, and the review affordance never appears
  // where it is actually needed.
  config.outputFormat = sdk.OutputFormat.Detailed
  config.requestWordLevelTimestamps()

  const audio = sdk.AudioConfig.fromDefaultMicrophoneInput()

  // ConversationTranscriber rather than SpeechRecognizer: it separates speakers.
  //
  // A consultation transcript without "who said it" is close to useless for a clinical
  // note — "no chest pain" means opposite things depending on whether the doctor asked it
  // or the patient answered it. Azure returns anonymous ids (Guest-1, Guest-2); mapping
  // them to Dr./Pt. is ours to do, below.
  const transcriber = new sdk.ConversationTranscriber(config, audio)

  // Drug names are the words that matter and the ones a general recogniser mangles.
  // "Azithromycin" misheard is a safety problem, not a typo.
  const phrases = sdk.PhraseListGrammar.fromRecognizer(transcriber)
  for (const phrase of issued.phrases) phrases.addPhrase(phrase)

  const toMs = (ticks: number) => Math.round(ticks / 10_000)

  // The first voice heard is taken to be the clinician: they start the recording and open
  // the consultation. It is a good default and it is wrong often enough that `swapSpeakers`
  // exists — a guess presented as fact in a medico-legal record is not acceptable, so the
  // UI shows the labels as correctable.
  const roles = new Map<string, string>()
  let swapped = false

  function label(speakerId: string | undefined): string | null {
    if (!speakerId || speakerId === 'Unknown') return null

    if (!roles.has(speakerId)) {
      roles.set(speakerId, roles.size === 0 ? DOCTOR : PATIENT)
    }

    const assigned = roles.get(speakerId)!
    if (!swapped) return assigned
    return assigned === DOCTOR ? PATIENT : DOCTOR
  }

  transcriber.transcribing = (_s, e) => {
    if (!e.result.text) return
    onRecognised({
      text: e.result.text,
      isFinal: false,
      offsetMs: toMs(e.result.offset),
      durationMs: toMs(e.result.duration),
      confidence: 1,
      speaker: label(e.result.speakerId),
    })
  }

  transcriber.transcribed = (_s, e) => {
    if (e.result.reason !== sdk.ResultReason.RecognizedSpeech || !e.result.text) return

    onRecognised({
      text: e.result.text,
      isFinal: true,
      offsetMs: toMs(e.result.offset),
      durationMs: toMs(e.result.duration),
      confidence: meanConfidence(e.result),
      speaker: label(e.result.speakerId),
    })
  }

  transcriber.canceled = (_s, e) => {
    // The mic-health signal. Capture failing silently is the worst outcome here — the
    // clinician carries on talking while nothing is recorded.
    if (e.reason === sdk.CancellationReason.Error) {
      onError(`Capture stopped: ${e.errorDetails || 'the speech service disconnected'}`)
    }
  }

  await new Promise<void>((resolve, reject) =>
    transcriber.startTranscribingAsync(() => resolve(), (err) => reject(new Error(err))),
  )

  return {
    stop: () =>
      new Promise<void>((resolve) =>
        transcriber.stopTranscribingAsync(
          () => { transcriber.close(); resolve() },
          () => { transcriber.close(); resolve() },
        ),
      ),

    /** Flips Dr./Pt. for every future utterance. The UI relabels what is already on screen. */
    swapSpeakers: () => { swapped = !swapped },
  }
}

/**
 * Averages the per-word confidences Azure reports in its detailed result.
 *
 * Falls back to the utterance-level score, and finally to 1 — never to 0, because a
 * missing score is "we do not know", and marking every span low-confidence would make
 * the review gate meaningless.
 */
function meanConfidence(result: sdk.SpeechRecognitionResult | sdk.ConversationTranscriptionResult): number {
  try {
    const detail = JSON.parse(result.json)
    const best = detail?.NBest?.[0]
    if (!best) return 1

    const words = best.Words as { Confidence?: number }[] | undefined
    if (words?.length) {
      const scores = words.map((w) => w.Confidence).filter((c): c is number => typeof c === 'number')
      if (scores.length) return scores.reduce((a, b) => a + b, 0) / scores.length
    }

    return typeof best.Confidence === 'number' ? best.Confidence : 1
  } catch {
    return 1
  }
}
