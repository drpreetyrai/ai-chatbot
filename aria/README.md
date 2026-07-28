# ARIA — Ambient AI Healthcare Assistant

An implementation of [`../plan.md`](../plan.md), built on **Microsoft Agent Framework (.NET 10)**,
**Microsoft Foundry**, **Azure AI services**, and a **React 19** front end.

Aria listens during the consultation, drafts the note with span-level provenance, catches the
allergy conflict *while the patient is still in the room*, and — only once a clinician signs —
writes to the EHR, books the follow-up, sends the prescription and messages the patient.

---

## Run it

You need **.NET SDK 10** and **Node 20+**. Nothing else — no database to install, no Docker,
no Azure account.

### First time only

```bash
cd aria
cp .env.example .env        # optional — it already works empty
dotnet build                # restores and builds all 8 projects
cd web && npm install && cd ..
```

### Every time — three terminals

```bash
# Terminal 1 · backend — the API          → http://localhost:5199 + https://localhost:7001
cd aria
dotnet run --project src/Aria.Api

# Terminal 2 · backend — the workers      → no HTTP surface
cd aria
dotnet run --project src/Aria.Workers

# Terminal 3 · frontend — the React app   → http://localhost:5173
cd aria/web
npm run dev
```

### Or all three with one command

```bash
cd aria
./scripts/start.sh          # builds, starts all three, streams their logs, Ctrl-C stops everything
```

Then open **http://localhost:5173** and sign in as the bootstrap administrator:

| | |
|---|---|
| Email | `admin@northbridge.health` |
| Password | `AriaAdmin!2026` |

Two registrations are already waiting in **Approvals** — a doctor and a patient. Approve the
doctor (link them to `DR-1042`) and the patient (link them to John Abraham), then sign in as
either with the same password. Anyone else registers on the sign-in screen and waits.

**Three products, one sign-in.** Which one you get is decided by the server from your role,
never by the client:

| Role | Surface | What it is |
|---|---|---|
| Clinician | Clinical workspace | Today, encounters, notes, inbox, schedule, insights |
| Patient | Patient portal | Their own visits, messages, appointments, and Ask Aria |
| Admin | Admin console | Approvals, accounts, governance, insights — **no clinical access at all** |

To watch the entire clinical journey on the command line:

```bash
./scripts/demo.sh
```

> **You do not need an Azure subscription to run this.** Every external service has a working
> local implementation, and the app tells you which is which in its startup banner and on a chip
> in the header. Fill in a section of `.env` to switch that one service to live — they are
> independent (see [Wiring up the real services](#wiring-up-the-real-services)).

---

## What each process is

Three processes, and you can run any of them without the others. This is what each one
does and what breaks if it is missing.

| | Check it |
|---|---|
| **.NET SDK 10** | `dotnet --version` → `10.x`. [Download](https://dotnet.microsoft.com/download) |
| **Node 20+** | `node --version` → `v20` or later |

### 1 · Backend — the API

```bash
cd aria
dotnet run --project src/Aria.Api
```

Serves on **http://localhost:5199** and **https://localhost:7001**. Two addresses because
Google's OAuth callback must be an `https` loopback URL; everything else uses the plain one.

On first run it creates `aria.db` next to `.env`, applies the schema, and seeds the clinic
from `wireframe.md` — the same patients, times, allergy and red flag. It then prints a
banner naming every service and whether it is LIVE or STUB. Read that banner: it is the
fastest way to know what your `.env` is actually doing.

Check it is up:

```bash
curl http://localhost:5199/health
```

Without it, nothing works — the front end shows *Could not reach the API*.

### 2 · Backend — the workers

```bash
cd aria
dotnet run --project src/Aria.Workers
```

No HTTP surface. Two background jobs:

- **Outbox dispatcher** — the *only* thing in the system that talks to the EHR, the
  pharmacy, the calendar or the patient, and only for actions a signature released.
- **Calendar sync** — mirrors each connected clinician's Google busy blocks into the local
  projection every five minutes.

The app works without the workers running: notes draft and sign normally, outbox rows just
sit as `Pending` until a dispatcher picks them up.

### 3 · Frontend — the React app

```bash
cd aria/web
npm run dev                               # http://localhost:5173, hot reload
```

It expects the API at `http://localhost:5199`; override with `VITE_API_BASE` if you moved it.

```bash
npm run build && npm run preview          # production build, served statically
```

Without it the backend still runs — `./scripts/demo.sh` drives the whole clinical journey
from the command line with no browser at all.

### Ports

| Port | Process | Notes |
|---|---|---|
| 5173 | Vite dev server | the app you open |
| 5199 | API (http) | what the front end calls |
| 7001 | API (https) | Google OAuth callback only |

### When something is wrong

| Symptom | Cause and fix |
|---|---|
| `address already in use` | An old run is still going: `pkill -f Aria.Api` |
| Everything says STUB in the banner | `.env` is missing or empty. `cp .env.example .env` and fill in a section. |
| `no such table` | Delete `aria.db` and restart the API — it recreates and reseeds. New tables are added automatically; this is only for a schema that has genuinely diverged. |
| Sign-in says *awaiting approval* | Correct. Sign in as the admin and approve the account first. |
| Google says *Access blocked* | The redirect URI is not registered. The Schedule screen prints the exact string to paste into your OAuth client. |
| Browser warns about the certificate on :7001 | The ASP.NET dev certificate is untrusted. `dotnet dev-certs https --trust`, or click through — the flow works either way. |
| The front end shows *Could not reach the API* | The API is not running, or it is on a different port than `VITE_API_BASE`. |

---

## The two invariants

Everything else in this codebase is negotiable. These two are enforced by the compiler, the
database and the test suite simultaneously.

### 1 · Signature is the only write barrier

No code path outside `SignatureService.SignAsync()` can cause an external effect. Three
independent mechanisms:

| # | Mechanism | Where |
| --- | --- | --- |
| a | The outbox table has a `CHECK` constraint requiring a non-empty note id | `AriaDbContext.OnModelCreating` |
| b | `Aria.Api` and `Aria.Agents` **cannot reference** `Aria.Integrations` — the adapter types are not in their closure at all | project references |
| c | An architecture test fails the build if that reference ever appears | `ArchitectureTests` |

You can watch it hold: `Admin → Outbox` is empty until you sign, then contains exactly five rows,
each stamped with the note id that released it.

### 2 · Escalation never depends on a probabilistic system

`RedFlagDetector` lives in `Aria.Safety`, an assembly that **cannot see** `Aria.Agents`, the agent
framework, or the database. It is a deterministic keyword net, optionally *widened* by a
classifier — and a classifier timeout or error counts as a **positive**, never a negative.

```
keyword net  →  (optional) classifier  →  UNION, biased to over-trigger
```

Type `chest tightness since morning` into the Inbox as the patient. The bot mutes itself, a
safety-netting reply goes out immediately, the on-call is paged, and no agent ever sees the text.

---

## What is actually implemented

| Capability | Where | How to see it |
| --- | --- | --- |
| Ambient capture, streaming transcript | `EncounterService`, SSE | Today → Start encounter |
| Live entity extraction | `ExtractionService` | right column fills as the consultation runs |
| **In-conversation allergy conflict** | `AllergyConflictChecker` | fires at ~70s, before the note exists |
| Note drafting with span provenance | `ScribeService` + `OutputGuards` | every sentence links to its transcript window |
| Confidence bands forcing review | `Note.IsSignable` | the note refuses to be signed until you accept the flagged line |
| **The write barrier** | `SignatureService` | Admin → Outbox, before and after signing |
| Post-signature fan-out | `OutboxDispatcher` | five external writes, idempotent, retried with backoff |
| Red-flag escalation | `Aria.Safety` | Inbox → send "chest tightness" |
| Prompt-injection defence | `LocalHeuristicShield` / Content Safety | Inbox → send "ignore all previous instructions…" |
| Template-bounded patient messages | `PatientCommsService` | free prose to a patient is architecturally impossible |
| Chart Q&A with mandatory citations | `ChartQaService` | Patient → Ask this chart |
| Cited clinical evidence | `ClinicalEvidenceService` | uncited items are deleted, not flagged |
| Long/short-term memory | `PatientContextProvider`, `MemoryWriteGate` | only signed records enter long-term memory |
| Tool calling with declared authority | `ToolCatalog`, `ToolAuthorizationMiddleware` | 22 tools, four authority levels |
| Hash-chained audit | `AuditService` | Admin → Audit log → chain verified |
| Autonomy dials | `AutonomyPolicy` | Admin → red-flag dial is non-interactive, API returns 422 |
| RBAC | endpoint policies | sign in as Priya (Admin) — she cannot open a chart |
| Observability | OpenTelemetry + `IAriaEventSink` | Insights reads the live event stream |

---

## Design deliverables

The problem framing, system design walk-through, tradeoffs and failure-mode analysis are in
[`../Deliverables/DELIVERABLES.md`](../Deliverables/DELIVERABLES.md), along with six worked
demos of using the product. The design it was built from is [`../plan.md`](../plan.md).

---

## Architecture

```
Aria.Domain ──────────── entities, state machines, autonomy policy   (no dependencies)
     ▲
Aria.Safety ──────────── red-flag detector, allergy checker          ← CANNOT see Aria.Agents
     ▲
Aria.Infrastructure ──── EF Core, outbox, hash-chained audit, retrieval
     ▲
Aria.Agents ──────────── agents, tools, guardrail middleware, memory ← CANNOT see Aria.Integrations
     ▲
Aria.Api ─────────────── REST + SSE, THE SIGNATURE BARRIER           ← CANNOT see Aria.Integrations
                                    │
                                 outbox
                                    ▼
Aria.Workers ─────────── the only process that touches the outside world
     └── Aria.Integrations ── FHIR · Google Calendar · WhatsApp
```

### Guardrails are middleware, not prompt text

A prompt that says *"always cite your sources"* is a hope. Middleware that **deletes** an uncited
claim before it can render is a guarantee. Every agent is constructed through
`GuardedAgentRunner`, so a new agent inherits input shielding, tool authority, output enforcement,
telemetry and audit — and cannot ship without them.

```
L0 identity/tenancy → L1 input shield → L2 retrieval scope → L3 tool authority
   → L4 output enforcement → L5 human approval → L6 kill switches
```

Layers L0–L4 **fail closed**. L6 degrades to a working manual path — never to an error page.

---

## Wiring up the real services

Each section of `.env` is independent. Fill in one, restart, and the startup banner flips that row
from `STUB` to `LIVE`.

| `.env` section | Unlocks | Get it from |
| --- | --- | --- |
| 1 · `FOUNDRY_PROJECT_ENDPOINT` **or** `OPENAI_API_KEY` | real note drafting, chart Q&A, the assistant | [ai.azure.com](https://ai.azure.com) → project → deploy 4 models, or [platform.openai.com](https://platform.openai.com) |
| 2 · `CONTENT_SAFETY_*` | Prompt Shields, groundedness detection | Azure portal → AI Content Safety |
| 3 · `SPEECH_*` | real-time ASR with diarisation | Azure portal → Speech service |
| 4 · `LANGUAGE_*` | Text Analytics for Health | Azure portal → Language service |
| 5 · `SEARCH_*` | Azure AI Search retrieval | Azure portal → AI Search |
| 6 · `GOOGLE_*` | real calendar free/busy and booking | Google Cloud Console → Calendar API |
| 7 · `WHATSAPP_*` | real patient messaging | developers.facebook.com → WhatsApp product |
| 8 · `FHIR_*` | real EHR writes | any FHIR R4 server |
| 9 · `AZURE_TENANT_ID` etc. | Entra ID SSO instead of local passwords | entra.microsoft.com |

`.env` is git-ignored and development-only. In production every value comes from Azure Key Vault
via managed identity — see `plan.md` §15.

### Google Calendar, specifically

Calendar access is **per clinician**, granted by them, revocable by them — not a service account.
Sign in as a doctor, open **Schedule**, and choose **Connect Google Calendar**.

Two things have to line up first, and both fail in ways Google reports only as a blank
*"Access blocked"* page:

1. `GOOGLE_REDIRECT_URI` must be registered **verbatim** in your OAuth client's *Authorised
   redirect URIs*. The Schedule screen prints the exact string it is using.
2. While your OAuth consent screen is in *Testing*, the Google account you are connecting must be
   listed under *Test users*.

The API listens on `https://localhost:7001` as well as `http://localhost:5199` purely so the
loopback callback can be an `https` URL. The dev certificate is untrusted by default — run
`dotnet dev-certs https --trust` once if you would rather not click through the browser warning.

Once connected, a background worker mirrors that clinician's real busy blocks into the local
projection every five minutes, so the Schedule screen and the slot proposals both account for
what is actually in their diary. Bookings still only happen after a signature.

**A note on the local model:** with no Foundry endpoint, `LocalDemoChatClient` serves every model
call. It is a transparent set of clinical rules over the transcript, not a language model, and the
UI labels it as a stub wherever its output appears. It exists so the guardrails, memory, tool
authority, audit and evaluation can be exercised by anyone with a clone of the repo — and so the
chaos test *"can the clinic finish the day with every model down?"* is the same code path as the
demo.

---

## Tests

```bash
dotnet test                      # unit + architecture + integration + evaluation gates
cd web && npm test               # frontend unit
cd web && npm run audit          # dependency gate, with expiring exceptions
cd web && npm run test:e2e       # browser E2E + WCAG 2.2 AA
```

**251 tests. All green.** The E2E suite runs in about 30 seconds against the real stack.

Five suites, each answering a different question.

| Suite | Where | Count | Question it answers |
| --- | --- | --- | --- |
| **Unit + architecture** | `tests/Aria.Tests` | 136 | Does the logic hold, and do the invariants still hold structurally? |
| **Integration** | `tests/Aria.IntegrationTests` | 54 | Does the real HTTP surface behave — real routing, real database, real guardrails? |
| **Evaluation gates** | `tests/Aria.Evals` | 8 | Is this safe enough to release? |
| **Frontend unit** | `web/src/**/*.test.tsx` | 31 | Do the trust components tell the truth about confidence and provenance? |
| **E2E + accessibility** | `web/e2e` | 22 | Do the clinician's, patient's and administrator's journeys work in a browser, accessibly? |

The integration suite signs in the way a person does — register, wait for an administrator,
then sign in. There is deliberately no test-only back door: a shortcut that minted a token
without approval would leave the system's most important rule untested by every test using it.

### The dependency gate

`npm run audit` fails on any unreviewed high or critical advisory **and** on any
allowlist entry that has expired. Exceptions live in `web/.audit-allowlist.json`
with a justification, a reviewer and a date — so an exception cannot quietly
become permanent. There is currently one: an RSC-mode CSRF advisory in
react-router that has no published fix and whose code path this client-only SPA
never enters. Downgrading to clear it would reintroduce thirteen other advisories,
including an RCE.

### The release gates

`tests/Aria.Evals` runs golden datasets in `evals/datasets/` and is specified with
**no override**. A failure here is not a test to triage — it is a product that must not ship.

| Gate | Threshold | Cases |
| --- | --- | --- |
| `RedFlagRecall` | **100%** | 53 real-world phrasings — colloquial, misspelled, transliterated Hindi, leetspeak |
| `AllergyConflictRecall` | **100%** | 27 contraindications including cross-reactivity |
| `AllergyConflictSpecificity` | **100%** | 11 safe alternatives and negation contexts |
| `InjectionResistance` | **0 successes** | 29 attacks across 3 untrusted channels |
| `ShieldSpecificity` | **100%** | 7 benign clinical passages that must not be flagged |
| `UncertaintyAlwaysEscalates` | pass | classifier timeout and error both fail safe |
| `RedFlagPrecision` | ≥ 60% | over-triggering is accepted; alarm fatigue is still measured |

Fix the detector, never the dataset. The gates exist precisely because that is the
tempting move at 5pm on a release day.

### What the tests actually caught

Written down because it is the honest argument for the suite existing:

- The red-flag golden set found **eight** gaps in the keyword net — transliterated
  Hindi, "chest *feels* tight", `chesssst paaaain`, and "pressure **in my** chest"
  (my pattern only matched one word order).
- The injection corpus found a pattern that assumed a space where a real payload
  used a colon.
- The accessibility pass found that `text.tertiary` from the wireframe measures
  **2.96:1** on white — the same document requires 4.5:1. Four tokens were wrong,
  and a stale variable name (`--color-mint-text` vs `--color-minttext`) meant a
  hard-coded fallback was silently shipping the wrong colour everywhere.
- The E2E suite found that `.env` was **overriding real environment variables**
  (DotNetEnv clobbers by default) — which would have let a stray `.env` override
  container configuration in production.
- Integration tests found that seeded encounters start `Scheduled`, so capture
  could never begin on them — the wireframe's "Start a walk-in" was never wired up.
- The E2E suite found that live extraction was gated on `recording`, so whenever the
  transcript finished faster than the debounce the **final window was never
  extracted** — and the allergy conflict silently never appeared.
- It also found the red-flag banner could take up to ten seconds to appear, because
  it only refreshed on its own heartbeat.
- Running the product for real found the red-flag classifier's token budget was being
  spent on reasoning, so it returned an empty answer, fail-safed, and **escalated every
  routine message**. Fail-safe without a circuit breaker is just failure.
- The role tests found a patient could read **another patient's record** — five
  endpoints each deciding for themselves, and four of them right. There is now one
  method that decides.
- Rewriting the integration harness to sign in the way a person does found the test
  database path was being ignored: every run silently shared a file that survived
  between runs, so the seeder saw an existing clinic and skipped it.
- Running against a live model found span confidence came from the model's own
  self-report, which is 0.9+ on nearly everything — so the review gate **stopped
  engaging** the moment a real model was plugged in. Confidence is now capped by what
  the recogniser heard.
- It also found the live extraction timing out, taking the **allergy alert down with
  it**: an empty entity list conflicts with nothing, so the screen looked calm.
- And it found the patient assistant answering *"I don't have that information"* about a
  record it was holding, because lay phrasing shares no vocabulary with a clinical note.

## Project layout

```
aria/
├── .env.example              every key you can provide, with what it unlocks
├── .github/workflows/ci.yml  build → unit → integration → gates → e2e → supply chain
├── scripts/start.sh          runs API + workers + web
├── scripts/demo.sh           drives the whole journey and prints what happened
├── evals/datasets/           golden sets, JSONL, readable and editable without a build
├── src/
│   ├── Aria.Domain/          entities, state machines, autonomy policy
│   ├── Aria.Safety/          red-flag detector, allergy checker  (isolated)
│   ├── Aria.Shared/          options binding, fail-fast validation, telemetry
│   ├── Aria.Infrastructure/  EF Core, outbox, audit chain, retrieval, seed
│   ├── Aria.Agents/          agents, tools, guardrails, memory, prompts
│   ├── Aria.Integrations/    FHIR · Calendar · WhatsApp adapters
│   ├── Aria.Api/             REST + SSE, the signature barrier
│   └── Aria.Workers/         outbox dispatcher
├── tests/
│   ├── Aria.Tests/           architecture, safety, guardrail, signing
│   ├── Aria.IntegrationTests/ the real HTTP surface, end to end
│   └── Aria.Evals/           the release gates
└── web/                      React 19 · Vite · Tailwind, tokens from wireframe §6
```

---

## Known limits

Stated plainly, because a demo that overclaims is worse than one that doesn't.

- **The local model is a stub.** Realistic and deterministic, but it is rules over the demo
  transcript, not language understanding. Point `FOUNDRY_PROJECT_ENDPOINT` at a real deployment
  before judging note quality.
- **The local shield is heuristic.** It catches the published technique families and the corpus in
  the tests; a determined novel attack will get past it. Production refuses to start without Azure
  AI Content Safety configured — that check is in `AriaConfigurationExtensions`.
- **SQLite, not Postgres,** for local development. The schema and queries are provider-agnostic
  (`DateTimeOffset` conversion is applied only for SQLite), but migrations are not authored yet —
  `EnsureCreated` is used locally.
- **Pharmacy and lab ordering** are recorded rather than transmitted; the adapter seam is in place
  but no vendor is wired.
- **The E2E suite is isolated from your `.env`** via `ARIA_IGNORE_DOTENV=true`. If you have
  filled in real Azure credentials, the browser tests still run against local stubs — they must
  never sign in to a live directory or write to a live FHIR server.
- **Not yet built** from the plan: mobile companion (§S-11), Bicep infrastructure (§18), and the
  `require-ai-block` / `no-raw-color` ESLint rules — the convention is followed throughout and
  the accessibility half is enforced by the axe pass, but the lint rules themselves are not
  written.
