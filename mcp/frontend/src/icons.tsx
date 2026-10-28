const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  'stroke-width': '1.7',
  'stroke-linecap': 'round',
  'stroke-linejoin': 'round',
} as const

export const OverviewIcon = () => (
  <svg viewBox="0 0 24 24" {...stroke} aria-hidden="true">
    <rect x="3" y="3" width="7" height="9" rx="1.5" />
    <rect x="14" y="3" width="7" height="5" rx="1.5" />
    <rect x="14" y="12" width="7" height="9" rx="1.5" />
    <rect x="3" y="16" width="7" height="5" rx="1.5" />
  </svg>
)

export const InboxIcon = () => (
  <svg viewBox="0 0 24 24" {...stroke} aria-hidden="true">
    <path d="M3 13h5l1.5 3h5L16 13h5" />
    <path d="M5.5 5h13L21 13v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-6z" />
  </svg>
)

export const ActivityIcon = () => (
  <svg viewBox="0 0 24 24" {...stroke} aria-hidden="true">
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7v5l3 2" />
  </svg>
)

export const SessionsIcon = () => (
  <svg viewBox="0 0 24 24" {...stroke} aria-hidden="true">
    <circle cx="9" cy="8" r="3.5" />
    <path d="M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6" />
    <path d="M16 4.5a3.5 3.5 0 0 1 0 7" />
    <path d="M18 14.5c1.8.8 3 2.9 3 5.5" />
  </svg>
)

export const CopyIcon = () => (
  <svg viewBox="0 0 24 24" {...stroke} aria-hidden="true">
    <rect x="8" y="8" width="12" height="12" rx="2" />
    <path d="M16 8V5.5A1.5 1.5 0 0 0 14.5 4h-9A1.5 1.5 0 0 0 4 5.5v9A1.5 1.5 0 0 0 5.5 16H8" />
  </svg>
)

export const LinkIcon = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" {...stroke} aria-hidden="true">
    <path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1" />
    <path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1" />
  </svg>
)

export const CheckIcon =(props: { size?: number; weight?: number }) => (
  <svg
    viewBox="0 0 24 24"
    width={props.size ?? 12}
    height={props.size ?? 12}
    fill="none"
    stroke="currentColor"
    stroke-width={props.weight ?? 3.2}
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path d={props.size && props.size > 12 ? 'M4 12.5l5 5L20 6.5' : 'M5 12.5l4.5 4.5L19 7.5'} />
  </svg>
)
