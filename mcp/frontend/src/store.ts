import { createEffect, createSignal, on, onCleanup, type Accessor } from 'solid-js'
import * as api from './api.ts'
import type { SessionTenant } from './connect.ts'
import { onFrames } from './live.ts'

/**
 * The workspace as the shell reads it: the overview, the inbox, and every run's board (the
 * overview's segmented bars draw one segment per leaf, which only a board carries). `undefined`
 * means not asked yet — never "none" — so no panel can say "nothing" before it knows.
 */
const [overview, setOverview] = createSignal<api.Overview>()
const [inbox, setInbox] = createSignal<api.Inbox>()
const [boards, setBoards] = createSignal<Record<string, api.Board>>({})
const [failure, setFailure] = createSignal<string>()

export { overview, inbox, boards, failure }

/** Bumped once per burst of frames: the page-level reads (an item, the journal) key on it. */
const [version, setVersion] = createSignal(0)

const note = (error: unknown) => {
  if (error instanceof api.Unauthorized) return
  setFailure(error instanceof Error ? error.message : String(error))
}

async function loadBoard(run: string): Promise<void> {
  try {
    const board = await api.board(run)
    setBoards((all) => ({ ...all, [run]: board }))
  } catch (error) {
    if (!(error instanceof api.NotFound)) note(error)
  }
}

async function loadOverview(runs: 'all' | string[]): Promise<void> {
  try {
    const next = await api.overview()
    setOverview(next)
    setFailure(undefined)

    // An archived project's bars are not drawn until it is shown, which falls back to the rollup.
    const ids = next.projects.filter((p) => !p.archivedAt).flatMap((p) => p.runs.map((r) => r.item.id))
    const known = boards()
    const wanted = runs === 'all' ? ids : ids.filter((id) => runs.includes(id) || known[id] === undefined)
    await Promise.all(wanted.map(loadBoard))
  } catch (error) {
    note(error)
  }
}

/**
 * Who is signed in and which workspaces they may enter: the sidebar's Admin link, its tenant switcher and the
 * palette's commands. Not live: the journal's member_added / member_removed are a project's members, which the
 * overview and the Members panel re-read on their frame, and say nothing about a workspace's — so an admin write
 * that may change the caller's own rights asks again with `refreshAccount`.
 */
export type Account = { user: api.SessionUser; tenants: SessionTenant[] }

const [account, setAccount] = createSignal<Account>()
export { account }

export async function refreshAccount(): Promise<void> {
  try {
    const [user, { tenants }] = await Promise.all([api.me(), api.sessionTenants()])
    setAccount({ user, tenants: tenants ?? [] })
  } catch (error) {
    note(error)
  }
}

/** After a write: the journal frame would bring the same re-read, this one does not wait for it. */
export const refreshOverview = (): Promise<void> => loadOverview([])

async function loadInbox(): Promise<void> {
  try {
    setInbox(await api.inbox())
  } catch (error) {
    note(error)
  }
}

/** Opens the store and keeps it current until the owner is disposed. */
export function startStore(): void {
  void loadOverview('all')
  void loadInbox()
  void refreshAccount()

  onCleanup(
    onFrames((frames) => {
      const runs = frames.map((f) => f.runId)
      void loadOverview(runs.includes(null) ? 'all' : [...new Set(runs as string[])])
      void loadInbox()
      setVersion((n) => n + 1)
    }),
  )
}

type Load<T> = { data: Accessor<T | undefined>; missing: Accessor<boolean>; error: Accessor<string | undefined> }

/**
 * One read that follows `key` and, unless `live` is off, every live burst. The previous answer
 * stays on screen while a re-read is in flight; an answer for a key no longer current is dropped.
 */
export function load<T>(
  key: Accessor<string | undefined>,
  fetcher: (key: string) => Promise<T>,
  options: { live: boolean } = { live: true },
): Load<T> {
  const [data, setData] = createSignal<T>()
  const [missing, setMissing] = createSignal(false)
  const [error, setError] = createSignal<string>()
  let held: string | undefined
  let ticket = 0

  const run = (k: string | undefined) => {
    const mine = ++ticket
    if (k !== held) {
      held = k
      setData(undefined)
      setMissing(false)
      setError(undefined)
    }
    if (k === undefined) return

    fetcher(k).then(
      (value) => {
        if (mine !== ticket) return
        setData(() => value)
        setMissing(false)
        setError(undefined)
      },
      (failure: unknown) => {
        if (mine !== ticket || failure instanceof api.Unauthorized) return
        if (failure instanceof api.NotFound) setMissing(true)
        else setError(failure instanceof Error ? failure.message : String(failure))
      },
    )
  }

  createEffect(on(() => [key(), options.live ? version() : 0] as const, ([k]) => run(k)))

  return { data, missing, error }
}
