import { ago, plural } from './format.ts'

/** `GET /api/session/apps` (doc 13): one OAuth approval of the signed-in user in this workspace. */
export type ConnectedApp = {
  id: string
  clientId: string
  name: string
  scopes: string[]
  createdAt: string | null
  lastUsedAt: string | null
  sessions: number
}

export type ConnectedApps = { asOf: string; apps: ConnectedApp[] }

export type RevokedApp = { tokensRevoked: number; sessionsRevoked: number }

export const sessionsLabel = (n: number) => `${n} agent ${plural(n, 'session')}`

/** `mcp offline_access · connected 3d ago · last used 5m ago · 1 agent session`, relative to the server's `asOf`. */
export function appLine(app: ConnectedApp, asOf: string): string {
  const parts = [app.scopes.join(' ')]
  if (app.createdAt) parts.push(`connected ${ago(app.createdAt, asOf)}`)
  parts.push(app.lastUsedAt ? `last used ${ago(app.lastUsedAt, asOf)}` : 'not used yet')
  parts.push(sessionsLabel(app.sessions))
  return parts.filter(Boolean).join(' · ')
}

/** What Revoke ends, in the confirm's words. */
export const revokeWarning = (app: ConnectedApp) =>
  app.sessions === 0
    ? `${app.name} will need to sign in again.`
    : app.sessions === 1
      ? `${app.name} will need to sign in again, and its agent session stops.`
      : `${app.name} will need to sign in again, and its ${sessionsLabel(app.sessions)} stop.`
