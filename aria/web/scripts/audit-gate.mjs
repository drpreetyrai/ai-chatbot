#!/usr/bin/env node
/**
 * The npm audit gate.
 *
 * Fails on any high or critical advisory that is not explicitly allowed in
 * .audit-allowlist.json, and fails just as hard on an allowlist entry that has
 * expired. The point is that an exception is a dated, justified decision — not a
 * flag someone added once to make the build green.
 */
import { execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'

const BLOCKING = new Set(['high', 'critical'])

const allowlist = JSON.parse(readFileSync(new URL('../.audit-allowlist.json', import.meta.url)))
const today = new Date().toISOString().slice(0, 10)

// npm audit exits non-zero when it finds anything; the JSON is what we want.
let report
try {
  report = JSON.parse(execSync('npm audit --json', { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }))
} catch (error) {
  report = JSON.parse(error.stdout)
}

const expired = allowlist.allow.filter((entry) => entry.expires < today)
if (expired.length > 0) {
  console.error('\n✖ Expired audit exceptions — these must be re-reviewed, not extended by habit:\n')
  for (const entry of expired) {
    console.error(`  ${entry.advisory}  ${entry.package}  expired ${entry.expires}`)
    console.error(`    revisit: ${entry.revisit}`)
  }
  process.exit(1)
}

const allowedIds = new Set(allowlist.allow.map((entry) => entry.advisory))
const blocking = []

for (const [name, vuln] of Object.entries(report.vulnerabilities ?? {})) {
  if (!BLOCKING.has(vuln.severity)) continue

  for (const via of vuln.via ?? []) {
    if (typeof via !== 'object') continue

    const id = (via.url ?? '').split('/').pop()
    if (allowedIds.has(id)) continue

    blocking.push({ name, id, title: via.title, range: via.range, url: via.url })
  }
}

if (blocking.length > 0) {
  console.error('\n✖ Unreviewed high or critical advisories:\n')
  for (const item of blocking) {
    console.error(`  ${item.name}  ${item.id}`)
    console.error(`    ${item.title}`)
    console.error(`    vulnerable: ${item.range}`)
    console.error(`    ${item.url}\n`)
  }
  console.error('Fix the dependency, or add a justified, expiring entry to .audit-allowlist.json.')
  process.exit(1)
}

console.log(`✓ npm audit clean (${allowlist.allow.length} documented exception(s), none expired)`)
