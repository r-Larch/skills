import { Show, createEffect, onCleanup } from 'solid-js'
import { Route, Router } from '@solidjs/router'
import { signedIn } from './api'
import Gate from './Gate'
import { startStream } from './live'
import RunView from './views/RunView'
import WatchFloor from './views/WatchFloor'

export default function App() {
  // The stream is torn down when the session lapses and re-established when it returns; a closed
  // EventSource against a 401 would otherwise retry forever behind a sign-in form.
  createEffect(() => {
    if (!signedIn()) return
    onCleanup(startStream())
  })

  return (
    <Show when={signedIn()} fallback={<Gate />}>
      <Router>
        <Route path="/" component={WatchFloor} />
        <Route path="/runs/:run" component={RunView} />
      </Router>
    </Show>
  )
}
