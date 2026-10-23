import { Show, createEffect } from 'solid-js'
import { useNavigate, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import { load } from '../store.ts'
import { itemHref, runOf } from '../nav.ts'
import Detail, { asTab } from './Detail.tsx'

/**
 * `/items/{id}`: the address for an item when the linking page does not know its run. Once the
 * item page says which run it sits in, this becomes that run's page with the item selected; only
 * what sits outside every run (a project, project-level work) is shown here on its own.
 */
export default function Item() {
  const params = useParams<{ item: string }>()
  const [search] = useSearchParams<{ tab?: string }>()
  const navigate = useNavigate()
  const page = load(() => params.item, api.item)

  const run = () => {
    const p = page.data()
    return p && p.item.id === params.item ? runOf(p.item, p.path) : null
  }

  createEffect(() => {
    const r = run()
    const p = page.data()
    if (r && p) navigate(`${itemHref(p.item, r)}${search.tab ? `?tab=${search.tab}` : ''}`, { replace: true })
  })

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
                tab={asTab(search.tab)}
                href={(item) => itemHref(item, null)}
                tabHref={(t) => `/items/${p().item.id}${t === 'overview' ? '' : `?tab=${t}`}`}
              />
            </div>
          )}
        </Show>
      </Show>
    </div>
  )
}
