import { Show, createEffect, onCleanup } from 'solid-js'
import { A, Route, Router } from '@solidjs/router'
import { signedIn } from './api.ts'
import Gate from './Gate.tsx'
import Shell from './Shell.tsx'
import { startStream } from './live.ts'
import { startStore } from './store.ts'
import Account from './views/Account.tsx'
import Activity from './views/Activity.tsx'
import Admin from './views/Admin.tsx'
import AdminTenant from './views/AdminTenant.tsx'
import AdminUsers from './views/AdminUsers.tsx'
import Backlog from './views/Backlog.tsx'
import Inbox from './views/Inbox.tsx'
import Item from './views/Item.tsx'
import Overview from './views/Overview.tsx'
import Project from './views/Project.tsx'
import Run from './views/Run.tsx'
import Sessions from './views/Sessions.tsx'

const NotFound = () => (
  <div class="page">
    <div class="panel empty">
      There is no page at this address. <A href="/">Back to the overview</A>.
    </div>
  </div>
)

export default function App() {
  // The stream and the store are torn down when the session lapses and re-established when it
  // returns; a closed EventSource against a 401 would otherwise retry forever behind a sign-in form.
  createEffect(() => {
    if (!signedIn()) return
    onCleanup(startStream())
    startStore()
  })

  return (
    <Show when={signedIn()} fallback={<Gate />}>
      <Router root={Shell}>
        <Route path="/" component={Overview} />
        <Route path="/inbox" component={Inbox} />
        <Route path="/activity" component={Activity} />
        <Route path="/sessions" component={Sessions} />
        <Route path="/runs/:run/:item?" component={Run} />
        <Route path="/items/:item" component={Item} />
        <Route path="/projects/:project" component={Project} />
        <Route path="/projects/:project/backlog" component={Backlog} />
        <Route path="/account" component={Account} />
        <Route path="/admin" component={Admin} />
        <Route path="/admin/users" component={AdminUsers} />
        <Route path="/admin/tenants/:tenant" component={AdminTenant} />
        <Route path="*" component={NotFound} />
      </Router>
    </Show>
  )
}
