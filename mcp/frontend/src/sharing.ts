/** The overview's `sharing` (doc 10): `members` 0 is a project no one has joined, open to the whole workspace. */
export type Sharing = { members: number; you: boolean; names: string[] }

export type ShareMarker = {
  k: 'private' | 'shared' | 'open'
  label: string
  /** What hover or focus shows: who the members are. */
  detail: string
}

const list = (names: string[]): string =>
  names.length <= 1 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`

/**
 * The project card's one marker. "Shared with N" counts the people besides you when you are a member; a caller
 * who is not (a static token names no user) sees the whole count.
 */
export function shareMarker(sharing: Sharing, me?: string): ShareMarker {
  if (sharing.members === 0) {
    return { k: 'open', label: 'Open to the workspace', detail: 'No one has joined yet, so everyone in the workspace can see it.' }
  }

  if (sharing.you) {
    const n = sharing.members - 1
    // Without the caller's name the list cannot leave them out, so it names everyone.
    return n === 0
      ? { k: 'private', label: 'Private', detail: 'Only you.' }
      : {
          k: 'shared',
          label: `Shared with ${n}`,
          detail: `${list(me === undefined ? sharing.names : ['You', ...sharing.names.filter((name) => name !== me)])}.`,
        }
  }

  return sharing.members === 1
    ? { k: 'private', label: `Private to ${sharing.names[0]}`, detail: `Only ${sharing.names[0]}.` }
    : { k: 'shared', label: `Shared with ${sharing.members}`, detail: `${list(sharing.names)}.` }
}

/** A member's line in the Members panel: who added them and when, and whether their account is disabled. */
export function memberLine(member: { addedBy: string | null; disabledAt: string | null }, added: string): string {
  return `${member.addedBy ? `Added by ${member.addedBy}` : 'Added'} ${added}${member.disabledAt ? ' · disabled' : ''}`
}

/** The Members panel's anchor on a project page; the palette's "Manage members" opens it. */
export const MEMBERS_ANCHOR = 'members'

export const membersHref = (project: string) => `/projects/${project}#${MEMBERS_ANCHOR}`

/** The project a path is the page of, or null: `/projects/{id}` only, not its backlog. */
export const projectOnPath = (path: string): string | null => /^\/projects\/([^/?#]+)\/?$/.exec(path)?.[1] ?? null
