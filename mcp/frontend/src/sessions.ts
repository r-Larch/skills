import type { TokenState } from './api.ts'

/** What a released row's name carries after its old name (SessionStore.TombstoneMarker). */
export const RELEASED_MARKER = '~released~'

export const isReleased = (name: string) => name.includes(RELEASED_MARKER)

/** The name a released row went by before it let go of it. */
export const formerName = (name: string) => name.split(RELEASED_MARKER)[0]!

export const tokenLabel = (token: TokenState): string =>
  ({ none: 'named by header, no token', live: 'token live', revoked: 'token revoked', expired: 'token expired' })[token]

/** A released row has nothing left to free; every other row offers Release and the server judges it. */
export const releasable = (row: { session: { name: string } }) => !isReleased(row.session.name)
