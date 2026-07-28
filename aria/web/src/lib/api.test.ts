import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api, getToken, setToken } from './api'

/**
 * The API client, and in particular the SSE reader.
 *
 * Stream parsing is where a bug is silent: a frame boundary landing mid-chunk
 * drops a transcript segment, and nobody notices until a clinician says the note
 * is missing something the patient definitely said.
 */

function streamOf(chunks: string[]): Response {
  const encoder = new TextEncoder()
  let index = 0

  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    body: {
      getReader: () => ({
        read: async () =>
          index < chunks.length
            ? { done: false, value: encoder.encode(chunks[index++]) }
            : { done: true, value: undefined },
      }),
    },
  } as unknown as Response
}

describe('token handling', () => {
  beforeEach(() => setToken(null))
  afterEach(() => setToken(null))

  it('persists across a reload and clears on sign-out', () => {
    setToken('abc.123')
    expect(getToken()).toBe('abc.123')
    expect(localStorage.getItem('aria.token')).toBe('abc.123')

    setToken(null)
    expect(getToken()).toBeNull()
    expect(localStorage.getItem('aria.token')).toBeNull()
  })

  it('attaches the bearer token to every request', async () => {
    setToken('tok')
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await api.get('/v1/auth/me')

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit | undefined
    const headers = init?.headers as Record<string, string> | undefined
    expect(headers?.Authorization).toBe('Bearer tok')

    vi.unstubAllGlobals()
  })
})

describe('error handling', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('surfaces the server’s explanation rather than a bare status', async () => {
    // A 403 that reads "Forbidden" teaches nobody anything. "Role 'Admin' is not
    // permitted to view patient data" is the message the UI should show.
    vi.stubGlobal('fetch', async () =>
      new Response(JSON.stringify({ error: "Role 'Admin' is not permitted to view patient data." }), {
        status: 403,
      }),
    )

    const failure = await api.get('/v1/patients').catch((e) => e as ApiError)

    expect(failure).toBeInstanceOf(ApiError)
    expect((failure as ApiError).status).toBe(403)
    expect((failure as ApiError).message).toContain('not permitted')
  })

  it('still reports an error when the body is not JSON', async () => {
    vi.stubGlobal('fetch', async () => new Response('<html>gateway timeout</html>', { status: 504 }))

    const failure = await api.get('/v1/insights').catch((e) => e as ApiError)
    expect((failure as ApiError).status).toBe(504)
  })
})

describe('server-sent events', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('parses well-formed frames', async () => {
    vi.stubGlobal('fetch', async () =>
      streamOf([
        'event: segment\ndata: {"id":"s1","text":"Fever"}\n\n',
        'event: segment\ndata: {"id":"s2","text":"Cough"}\n\n',
        'event: complete\ndata: {}\n\n',
      ]),
    )

    const seen: [string, unknown][] = []
    await api.stream('/v1/encounters/e/transcript/stream', (event, data) => seen.push([event, data]))

    expect(seen).toHaveLength(3)
    expect(seen[0][0]).toBe('segment')
    expect((seen[0][1] as { id: string }).id).toBe('s1')
    expect(seen[2][0]).toBe('complete')
  })

  it('reassembles a frame split across chunk boundaries', async () => {
    // The case that breaks naive parsers: the network does not respect message
    // boundaries, so a frame can arrive in three pieces.
    vi.stubGlobal('fetch', async () =>
      streamOf(['event: segment\nda', 'ta: {"id":"s1","text":"Fev', 'er"}\n\nevent: complete\ndata: {}\n\n']),
    )

    const seen: [string, unknown][] = []
    await api.stream('/x', (event, data) => seen.push([event, data]))

    expect(seen).toHaveLength(2)
    expect((seen[0][1] as { text: string }).text).toBe('Fever')
  })

  it('skips a malformed frame without killing the stream', async () => {
    // One bad frame must not cost the clinician the rest of the consultation.
    vi.stubGlobal('fetch', async () =>
      streamOf([
        'event: segment\ndata: {"id":"s1"}\n\n',
        'event: segment\ndata: {not json}\n\n',
        'event: segment\ndata: {"id":"s3"}\n\n',
      ]),
    )

    const seen: unknown[] = []
    await api.stream('/x', (_e, data) => seen.push(data))

    expect(seen).toHaveLength(2)
    expect((seen[1] as { id: string }).id).toBe('s3')
  })

  it('reports a failed stream instead of silently returning nothing', async () => {
    // A capture screen that shows an empty transcript because the stream 500'd is
    // a screen that lies about whether recording is happening.
    vi.stubGlobal('fetch', async () => new Response(null, { status: 500, statusText: 'Server Error' }))

    await expect(api.stream('/x', () => {})).rejects.toBeInstanceOf(ApiError)
  })
})
