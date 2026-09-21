import type { JSX } from 'solid-js'
import { link } from './live'
import { cycleTheme, theme } from './theme'

/** The link light is the one place the app talks about itself; grey when live, amber when not. */
function LinkLight() {
  const label = () =>
    link() === 'live' ? 'live' : link() === 'connecting' ? 'connecting' : 'stream down'

  return (
    <span
      class="count"
      style={link() === 'live' ? undefined : { color: 'var(--color-attention)' }}
      title="GET /api/stream — the board changes because the database did"
    >
      {label()}
    </span>
  )
}

export default function TopBar(props: {
  subtitle: string
  crumb?: JSX.Element
  nav?: JSX.Element
}) {
  return (
    <header class="top">
      <div class="top-in">
        <div class="brand">
          workstate <span>/ {props.subtitle}</span>
        </div>
        {props.crumb}
        <div class="right">
          {props.nav}
          <LinkLight />
          <button class="themebtn" onClick={cycleTheme} title="dark · light · system">
            {theme()}
          </button>
        </div>
      </div>
    </header>
  )
}
