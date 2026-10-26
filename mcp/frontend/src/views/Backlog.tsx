import { For, Show } from 'solid-js'
import { A, useParams } from '@solidjs/router'
import * as api from '../api.ts'
import { load, overview } from '../store.ts'
import { itemHref, runHref } from '../nav.ts'
import { refState } from '../state.ts'
import { plural, short } from '../format.ts'
import { CopyRef, Dot, Known, ProjectName } from '../ui.tsx'

export default function Backlog() {
  const params = useParams<{ project: string }>()
  const page = load(() => params.project, api.backlog)
  const project = () => overview()?.projects.find((p) => p.item.id === params.project)?.item

  const count = () => page.data()?.groups.reduce((n, g) => n + g.items.length, 0) ?? 0

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <div class="crumbs">
            <A href="/">Overview</A> /{' '}
            <Show when={project()} fallback="project">
              {(p) => (
                <>
                  <ProjectName project={p()} /> <CopyRef value={p().qualifiedRef} />
                </>
              )}
            </Show>
          </div>
          <h1>Backlog</h1>
          <div class="sub">
            <Show when={page.data()} fallback="Loading…">
              {(b) =>
                b().run
                  ? `${count()} open ${plural(count(), 'item')} deferred out of other runs, grouped by where ${count() === 1 ? 'it' : 'they'} came from.`
                  : 'This project has no backlog run: nothing has been deferred to it.'
              }
            </Show>
          </div>
        </div>
        <Show when={page.data()?.run}>
          {(run) => (
            <A class="stamp" href={runHref(run().id)}>
              Open the backlog run
            </A>
          )}
        </Show>
      </div>

      <Show when={page.missing()}>
        <div class="panel empty">There is no project with this id.</div>
      </Show>
      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Show when={!page.missing()}>
        <Known value={page.data()} class="panel empty">
          {(b) => (
            <div class="panel" style={{ 'padding-bottom': '8px' }}>
              <For each={b().groups} fallback={<div class="empty">The backlog is empty.</div>}>
                {(group) => (
                  <>
                    <Show
                      when={group.origin}
                      fallback={<div class="group-h">Deferred from nowhere recorded</div>}
                    >
                      {(origin) => (
                        <A class="group-h" href={itemHref(origin(), null)}>
                          From {short(origin().title)}
                          <span class="gc">
                            {origin().qualifiedRef} <CopyRef value={origin().qualifiedRef} />
                          </span>
                        </A>
                      )}
                    </Show>
                    <For each={group.items}>
                      {(item) => (
                        <A class="item" href={itemHref(item, b().run)}>
                          <Dot k={refState(item).k} />
                          <div class="item-t">
                            <span class="item-ref">{item.qualifiedRef}</span>
                            <CopyRef value={item.qualifiedRef} />
                            {item.title}
                          </div>
                          <div class="item-side">{item.kind}</div>
                        </A>
                      )}
                    </For>
                  </>
                )}
              </For>
            </div>
          )}
        </Known>
      </Show>
    </div>
  )
}
