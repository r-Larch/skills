/**
 * The Workman marks, copied byte for byte from the brand kit into `public/brand/` (doc 13); never redrawn
 * here. `ground` is what the block sits on: the sidebar is dark in both themes, a gate page follows the theme.
 */
export const BrandIcon = (props: { alt?: string }) => <img src="/brand/workman.svg" alt={props.alt ?? ''} />

export function Brand(props: { ground: 'dark' | 'theme' }) {
  return (
    <>
      <span class="brand-mark">
        <BrandIcon />
      </span>
      {props.ground === 'dark' ? (
        <img class="brand-name" src="/brand/workman-wordmark-on-dark.svg" alt="Workman" />
      ) : (
        <>
          <img class="brand-name for-light" src="/brand/workman-wordmark-on-light.svg" alt="Workman" />
          <img class="brand-name for-dark" src="/brand/workman-wordmark-on-dark.svg" alt="Workman" />
        </>
      )}
    </>
  )
}
