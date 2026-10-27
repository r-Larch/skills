/* @refresh reload */
import { render } from 'solid-js/web'
import './index.css'
import App from './App.tsx'
import Connect from './Connect.tsx'
import { isConnectPath } from './connect.ts'
import { initTheme } from './theme.ts'

initTheme()

// The OAuth pages are not the dashboard: no router, no store, no stream behind them.
render(() => (isConnectPath(location.pathname) ? <Connect /> : <App />), document.getElementById('root')!)
