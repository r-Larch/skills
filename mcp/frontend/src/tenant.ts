import { createSignal } from 'solid-js'
import { switchTenant } from './api.ts'

const [switchFailure, setSwitchFailure] = createSignal<string>()
export { switchFailure }

/**
 * Moves the session to another workspace, then reloads at the overview: every read, the stream and every
 * board belong to one tenant, and a page of the old one would 404 in the new. A refusal stays in the sidebar.
 */
export async function switchWorkspace(id: string): Promise<void> {
  setSwitchFailure(undefined)
  const refused = await switchTenant(id)
  if (refused === null) location.assign('/')
  else setSwitchFailure(refused)
}
