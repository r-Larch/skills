/* @refresh reload */
import { render } from 'solid-js/web'
import './index.css'
import App from './App.tsx'
import Connect from './Connect.tsx'
import { isConnectPath } from './connect.ts'
import Join from './Join.tsx'
import Reset from './Reset.tsx'
import { joinToken, resetToken } from './admin.ts'
import { initTheme } from './theme.ts'

initTheme()

// The OAuth pages and the link pages are not the dashboard: no router, no store, no stream behind them.
const page = () => {
  const path = location.pathname
  if (isConnectPath(path)) return <Connect />
  if (joinToken(path) !== null) return <Join />
  if (resetToken(path) !== null) return <Reset />
  return <App />
}

render(page, document.getElementById('root')!)
