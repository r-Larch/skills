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

export const CheckIcon = (props: { size?: number; weight?: number }) => (
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
