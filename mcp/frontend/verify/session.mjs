import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Where the verify scripts get a credential, and the one place that decides it.
 *
 * Not literals in a script: `appsettings.Development.json` is where the API's own dashboard
 * credential is configured, so reading it is both correct and self-updating — a script carrying its
 * own copy is a second credential to rotate and a secret checked into a second file.
 * `WORKSTATE_USER` / `WORKSTATE_PASSWORD` override it, for a host configured some other way.
 */
const SETTINGS = fileURLToPath(
  new URL('../../WorkState.Api/appsettings.Development.json', import.meta.url),
)

function configured() {
  const dashboard = JSON.parse(readFileSync(SETTINGS, 'utf8'))?.WorkState?.Dashboard ?? {}
  return { username: dashboard.Username, password: dashboard.Password }
}

export function credential() {
  const fromFile = (() => {
    try {
      return configured()
    } catch {
      return {}
    }
  })()

  const username = process.env.WORKSTATE_USER ?? fromFile.username
  const password = process.env.WORKSTATE_PASSWORD ?? fromFile.password

  if (typeof username !== 'string' || typeof password !== 'string') {
    throw new Error(
      `no dashboard credential: set WORKSTATE_USER and WORKSTATE_PASSWORD, or configure ` +
        `WorkState:Dashboard in ${SETTINGS}`,
    )
  }

  return { username, password }
}

/** Signs in and returns the Cookie header every later request needs. */
export async function signIn(base) {
  const response = await fetch(`${base}/api/session`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(credential()),
  })

  if (response.status !== 204) {
    throw new Error(`POST ${base}/api/session → ${response.status} (expected 204)`)
  }

  const cookie = response.headers
    .getSetCookie()
    .map((c) => c.split(';')[0])
    .join('; ')

  if (cookie === '') throw new Error('POST /api/session answered 204 but set no cookie')
  return cookie
}

export async function read(base, cookie, path) {
  const response = await fetch(`${base}${path}`, { headers: { cookie } })
  if (!response.ok) throw new Error(`GET ${path} → ${response.status}`)
  return response.json()
}
