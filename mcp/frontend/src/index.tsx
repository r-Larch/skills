/* @refresh reload */
import { render } from 'solid-js/web'
import './index.css'
import App from './App.tsx'
import { initTheme } from './theme.ts'

initTheme()

render(() => <App />, document.getElementById('root')!)
