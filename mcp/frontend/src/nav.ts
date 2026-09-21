/**
 * Where a qualified ref points.
 *
 * The item travels in the query string rather than the path, and that is forced, not a preference:
 * `MapFallback` registers `{*path:nonfile}`, so any SPA path whose last segment contains a dot is a
 * 404 on a hard reload — and item refs are exactly the dotted ones (`3.1`, `4.6b`, `9.2a`).
 * `/runs/entity-store/3.1` would work until the operator pressed F5. Reported to the controller.
 */
export function refHref(qualifiedRef: string): string {
  const cut = qualifiedRef.indexOf('/')

  return cut < 0
    ? `/runs/${encodeURIComponent(qualifiedRef)}`
    : `/runs/${encodeURIComponent(qualifiedRef.slice(0, cut))}?item=${encodeURIComponent(qualifiedRef.slice(cut + 1))}`
}

export const itemHref = (run: string, item: string): string =>
  `/runs/${encodeURIComponent(run)}?item=${encodeURIComponent(item)}`
