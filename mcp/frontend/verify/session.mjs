import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Where the verify scripts get a credential, and the one place that decides it. Never a literal in
 * a script: a script carrying its own copy is a second credential to rotate.
 *
 * In order: `WORKSTATE_USER` / `WORKSTATE_PASSWORD`; then `mcp/.env`, which is what `compose.yml`
 * hands the container (`WORKSTATE_DASHBOARD_USERNAME` / `_PASSWORD`); then the API's own
 * `appsettings.Development.json`, for `dotnet run`.
 */
const ENV = fileURLToPath(new URL('../../.env', import.meta.url))
const SETTINGS = fileURLToPath(new URL('../../WorkState.Api/appsettings.Development.json', import.meta.url))

function fromEnvFile() {
  const values = {}
  for (const line of readFileSync(ENV, 'utf8').split(/\r?\n/)) {
    const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line)
    if (match) values[match[1]] = match[2].replace(/^(['"])(.*)\1$/, '$2')
  }
  return { username: values.WORKSTATE_DASHBOARD_USERNAME, password: values.WORKSTATE_DASHBOARD_PASSWORD }
}

function fromSettings() {
  const dashboard = JSON.parse(readFileSync(SETTINGS, 'utf8'))?.WorkState?.Dashboard ?? {}
  return { username: dashboard.Username, password: dashboard.Password }
}

const attempt = (read) => {
  try {
    return read()
  } catch {
    return {}
  }
}

export function credential() {
  const file = attempt(fromEnvFile)
  const settings = attempt(fromSettings)

  const username = process.env.WORKSTATE_USER ?? file.username ?? settings.username
  const password = process.env.WORKSTATE_PASSWORD ?? file.password ?? settings.password

  if (typeof username !== 'string' || typeof password !== 'string') {
    throw new Error(
      `no dashboard credential: set WORKSTATE_USER and WORKSTATE_PASSWORD, or WORKSTATE_DASHBOARD_USERNAME/` +
        `_PASSWORD in ${ENV}, or WorkState:Dashboard in ${SETTINGS}`,
    )
  }

  return { username, password }
}

/** The server binds IPv4 loopback only; `localhost` may resolve to ::1 and miss it. */
export const DEFAULT_BASE = 'http://127.0.0.1:5290'

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

/** The status alone, for link checks: a 404 is an answer, not an exception. */
export async function status(base, cookie, path) {
  const response = await fetch(`${base}${path}`, { headers: { cookie } })
  await response.arrayBuffer()
  return response.status
}
