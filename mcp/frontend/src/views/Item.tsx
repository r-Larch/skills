import { Show, createEffect } from 'solid-js'
import { useNavigate, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import { load } from '../store.ts'
import { SHOW_CANCELLED, itemHref, runOf } from '../nav.ts'
import { DEFAULT_FILTER, planQuery } from '../plan.ts'
import Detail, { asTab, type Tab } from './Detail.tsx'

/**
 * `/items/{id}`: the address for an item when the linking page does not know its run. Once the
 * item page says which run it sits in, this becomes that run's page with the item selected; only
 * what sits outside every run (a project, project-level work) is shown here on its own.
 */
export default function Item() {
  const params = useParams<{ item: string }>()
  const [search] = useSearchParams<{ tab?: string; cancelled?: string }>()
  const navigate = useNavigate()
  const page = load(() => params.item, api.item)
  const tab = () => asTab(search.tab)
  const cancelled = () => search.cancelled === SHOW_CANCELLED

  const run = () => {
    const p = page.data()
    return p && p.item.id === params.item ? runOf(p.item, p.path) : null
  }

  createEffect(() => {
    const r = run()
    const p = page.data()
    if (r && p) navigate(`${itemHref(p.item, r)}${planQuery(DEFAULT_FILTER, tab(), cancelled())}`, { replace: true })
  })

  const self = (t: Tab, show: boolean) => `/items/${params.item}${planQuery(DEFAULT_FILTER, t, show)}`

  return (
    <div class="page">
      <Show when={page.missing()}>
        <div class="panel empty">There is no item with this id.</div>
      </Show>
      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Show when={!page.missing() && !run()}>
        <Show when={page.data()} fallback={<div class="panel empty">Loading…</div>}>
          {(p) => (
            <div class="panel">
              <Detail
                page={p()}
                run={null}
                rows={new Map()}
                tab={tab()}
                cancelled={cancelled()}
                href={(item) => itemHref(item, null)}
                tabHref={(t) => self(t, cancelled())}
                cancelledHref={(show) => self(tab(), show)}
              />
            </div>
          )}
        </Show>
      </Show>
    </div>
  )
}
