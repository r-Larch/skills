/* @refresh reload */
import { render } from 'solid-js/web'
import './index.css'
import App from './App.tsx'
import Connect from './Connect.tsx'
import { isConnectPath } from './connect.ts'
import Join from './Join.tsx'
import { isLinkPath } from './admin.ts'
import { initTheme } from './theme.ts'

initTheme()

// The OAuth pages and the invite page are not the dashboard: no router, no store, no stream behind them.
const page = () => (isConnectPath(location.pathname) ? <Connect /> : isLinkPath(location.pathname) ? <Join /> : <App />)

render(page, document.getElementById('root')!)
