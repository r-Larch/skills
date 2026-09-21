/* @refresh reload */
import { render } from 'solid-js/web'
import './index.css'
import App from './App'
import { initTheme } from './theme'

initTheme()

render(() => <App />, document.getElementById('root')!)
