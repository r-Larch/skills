/**
 * The admin pages and the invite page as plain functions, so `verify/state-cases.mjs` can walk them without a
 * browser. The server judges every one of these first (`WorkState.Api/Auth/AccountRules.cs`,
 * `App/AdminEndpoints.cs`); these are the pages' own copy of the rules, for hints before a round trip.
 */

/** `AccountRules.PasswordMinLength`. */
export const PASSWORD_MIN = 12
const PASSWORD_MAX = 1024
const NAME_MAX = 64
const SLUG_MAX = 63

export const passwordProblem = (password: string): string | null =>
  password.length < PASSWORD_MIN
    ? `A password needs at least ${PASSWORD_MIN} characters.`
    : password.length > PASSWORD_MAX
      ? `A password has at most ${PASSWORD_MAX} characters.`
      : null

export function nameProblem(name: string): string | null {
  const trimmed = name.trim()
  if (trimmed === '') return 'A name is required: it is what you sign in with.'
  if (trimmed.length > NAME_MAX) return `A name has at most ${NAME_MAX} characters.`
  return /\p{Cc}/u.test(trimmed) ? 'A name cannot contain control characters.' : null
}

export const slugProblem = (slug: string): string | null =>
  slug.length <= SLUG_MAX && /^[a-z0-9]+(-[a-z0-9]+)*$/.test(slug)
    ? null
    : `A workspace slug is lowercase letters and digits, words joined by single hyphens, at most ${SLUG_MAX} characters — like default or team-two.`

/** A typed slug as the field would like it: lowercase, spaces as hyphens. The server still judges it. */
export const slugFrom = (typed: string): string => typed.trim().toLowerCase().replace(/\s+/g, '-')

export const ROLES = ['member', 'admin'] as const
export type Role = (typeof ROLES)[number]

export const roleLabel = (role: string): string =>
  ({ admin: 'Admin', member: 'Member', 'system-admin': 'System admin' })[role] ?? role

/** The sidebar offers a switch only when there is somewhere else to go. */
export const showSwitcher = (tenants: { id: string }[]): boolean => tenants.length > 1

/**
 * Whether a member's role and removal controls are locked: they are the workspace's only admin, which the
 * server refuses to demote or remove (409). Shown as locked rather than offered and refused.
 */
export const isLastAdmin = (members: { userId: string; role: string }[], userId: string): boolean => {
  const admins = members.filter((m) => m.role === 'admin')
  return admins.length === 1 && admins[0]!.userId === userId
}

/** `in 14 days`, `in 5 hours`, `in 3 minutes`, or `expired`, from the server's `expiresAt`. */
export function expiresIn(expiresAt: string, now: number): string {
  const s = (Date.parse(expiresAt) - now) / 1000
  if (Number.isNaN(s) || s <= 0) return 'expired'
  const unit = (n: number, one: string) => `in ${n} ${n === 1 ? one : `${one}s`}`
  if (s >= 2 * 86400) return unit(Math.floor(s / 86400), 'day')
  if (s >= 2 * 3600) return unit(Math.floor(s / 3600), 'hour')
  return unit(Math.max(1, Math.floor(s / 60)), 'minute')
}

/** The public pages: no router, no store, no stream, and no sign-in gate in front of them. */
export const JOIN = '/join/'

const tokenAfter = (prefix: string, pathname: string): string | null => {
  if (!pathname.startsWith(prefix)) return null
  const token = pathname.slice(prefix.length)
  return token !== '' && !token.includes('/') ? token : null
}

/** The token of an invite page's path, or null when the path is not one. */
export const joinToken = (pathname: string): string | null => tokenAfter(JOIN, pathname)

export const isLinkPath = (pathname: string): boolean => joinToken(pathname) !== null

/** `GET /api/invite/{token}`, as `InviteEndpoints` serialises it. */
export type InviteView = {
  tenantId: string
  tenant: string
  role: string
  expiresAt: string
  /** The cookie's user, who would accept as themselves; null without a session. */
  signedInAs: string | null
  /** Already a member of the workspace; accepting keeps their role, or raises it to the invite's. */
  member: boolean
}

export type JoinState =
  | { k: 'loading' }
  | { k: 'invalid'; message: string }
  | { k: 'shown'; invite: InviteView; mode: 'create' | 'sign-in'; error: string | null }
  | { k: 'accepting'; invite: InviteView; mode: 'create' | 'sign-in' }
  | { k: 'joined'; tenant: string }

export type JoinEvent =
  | { t: 'loaded'; invite: InviteView }
  | { t: 'failed'; status: number; error: string | null }
  | { t: 'mode'; mode: 'create' | 'sign-in' }
  | { t: 'accept' }
  | { t: 'refused'; status: number; error: string | null }
  | { t: 'accepted' }

/** What the page says when it cannot show the invite at all. */
export function joinFailure(status: number, error: string | null): string {
  if (status === 404) return error ?? 'This invite link cannot be used. Ask an admin of the workspace for a new one.'
  if (status === 429) return 'Too many attempts from here. Wait a minute, then reload this page.'
  if (status === 0) return 'WorkState did not answer. Check that it is running, then reload this page.'
  return error ?? `WorkState could not read this invite (${status}). Reload the page to try again.`
}

export function joinStep(state: JoinState, event: JoinEvent): JoinState {
  switch (event.t) {
    case 'loaded':
      return state.k === 'loading' ? { k: 'shown', invite: event.invite, mode: 'create', error: null } : state
    case 'failed':
      return state.k === 'loading' ? { k: 'invalid', message: joinFailure(event.status, event.error) } : state
    case 'mode':
      return state.k === 'shown' ? { ...state, mode: event.mode, error: null } : state
    case 'accept':
      // One accept per click: a second while the first is in flight changes nothing.
      return state.k === 'shown' ? { k: 'accepting', invite: state.invite, mode: state.mode } : state
    case 'refused':
      if (state.k !== 'accepting') return state
      // Used, revoked or expired since the page loaded: there is nothing left to try here.
      if (event.status === 404) return { k: 'invalid', message: joinFailure(404, event.error) }
      return {
        k: 'shown',
        invite: state.invite,
        mode: state.mode,
        error: event.status === 429 ? 'Too many attempts. Wait a minute, then try again.' : (event.error ?? `Refused (${event.status}).`),
      }
    case 'accepted':
      return state.k === 'accepting' ? { k: 'joined', tenant: state.invite.tenant } : state
  }
}
