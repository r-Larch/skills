import { Show } from 'solid-js'
import { A, useParams } from '@solidjs/router'
import { overview } from '../store.ts'
import { clock, day } from '../format.ts'
import { CopyRef, Known, Legend, ProjectName } from '../ui.tsx'
import { ProjectPanel } from './Overview.tsx'

/** One project: its overview card, archived or not, with the Archive / Unarchive switch. */
export default function Project() {
  const params = useParams()
  const project = () => overview()?.projects.find((p) => p.item.id === params.project)

  return (
    <div class="page">
      <Known value={overview()} class="panel empty">
        {(o) => (
          <Show
            when={project()}
            fallback={
              <div class="panel empty">
                There is no project with this id. <A href="/">Back to the overview</A>.
              </div>
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
