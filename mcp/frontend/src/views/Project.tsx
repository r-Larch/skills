import { Show, createEffect, createMemo, createSignal, on } from 'solid-js'
import { A, useLocation, useNavigate, useParams } from '@solidjs/router'
import * as api from '../api.ts'
import { account, load, overview } from '../store.ts'
import { clock, day } from '../format.ts'
import { MEMBERS_ANCHOR } from '../sharing.ts'
import { CopyRef, Known, Legend, ProjectName } from '../ui.tsx'
import { ProjectPanel } from './Overview.tsx'
import { MembersPanel } from './Members.tsx'

/**
 * One project: its overview card, archived or not, with the Archive / Unarchive switch, and its Members. A
 * workspace admin who is not a member gets no card — the overview does not list the project for them (doc 10
 * rule 8) — but its Members panel, which they manage without reading any of its work.
 */
export default function Project() {
  const params = useParams()
  const location = useLocation()
  const navigate = useNavigate()
  const project = () => overview()?.projects.find((p) => p.item.id === params.project)

  const members = load(() => params.project, api.projectMembers)
  const [written, setWritten] = createSignal<api.ProjectMembers>()
  createEffect(on(members.data, () => setWritten(undefined), { defer: true }))
  createEffect(on(() => params.project, () => setWritten(undefined), { defer: true }))
  const view = () => written() ?? members.data()

  // On arrival at #members and once the panel is drawn — not again on every live re-read.
  const drawn = createMemo(() => view() !== undefined)
  createEffect(
    on([() => location.hash, drawn], ([hash, ready]) => {
      if (hash !== `#${MEMBERS_ANCHOR}` || !ready) return
      const heading = document.getElementById(MEMBERS_ANCHOR)
      heading?.scrollIntoView({ block: 'start' })
      heading?.focus({ preventScroll: true })
    }),
  )

  const panel = () => (
    <Show
      when={view()}
      fallback={
        <Show when={members.missing() && project()?.sharing.members === 0}>
          <div class="panel empty">
            No one has joined this project yet, so everyone in the workspace can see it. A workspace admin can add its first
            members.
          </div>
        </Show>
      }
    >
      {(v) => <MembersPanel view={v()} me={account()?.user.id} onWritten={setWritten} onLeft={() => navigate('/')} />}
    </Show>
  )

  return (
    <div class="page">
      <Show when={members.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Known value={overview()} class="panel empty">
        {(o) => (
          <Show
            when={project()}
            fallback={
              <Show
                when={view()}
                fallback={
                  <Show when={members.missing()} fallback={<div class="panel empty">Loading…</div>}>
                    <div class="panel empty">
                      There is no project with this id. <A href="/">Back to the overview</A>.
                    </div>
                  </Show>
                }
              >
                {(v) => (
                  <>
                    <div class="page-h">
                      <div>
                        <div class="sub">Project</div>
                        <h1>{v().ref}</h1>
                        <div class="sub">
                          You manage this project's members as a workspace admin. You aren't a member, so its work isn't
                          shown here.
                        </div>
                      </div>
                    </div>
                    <div class="stack">{panel()}</div>
                  </>
                )}
              </Show>
            }
          >
            {(p) => (
              <>
                <div class="page-h">
                  <div>
                    <div class="sub">Project</div>
                    <h1>
                      <ProjectName project={p().item} /> <CopyRef item={p().item} />
                    </h1>
                  </div>
                  <div class="stamp">
                    As of {day(o().asOf)}, {clock(o().asOf)}
                  </div>
                </div>
                <div class="stack">
                  <ProjectPanel project={p()} />
                  {panel()}
                  <Legend />
                </div>
              </>
            )}
          </Show>
        )}
      </Known>
    </div>
  )
}
