# Ledger — Brief 04 "Convention pricing actually reaching self-checkout"

> Append-only. Written for a fresh orchestrator with zero context.

## Status
- **CODE COMPLETE, remaining work is Odoo configuration.** 6 commits `eb537e478..` (config doc
  last). Deliverable for the owner: `odoo/CONVENTION_PRICING_CONFIG.md`.
- Final validation, run by the orchestrator: `dotnet build` **0 errors** ·
  `CheckoutMatchingTests` **243/243** · `SubscriptionTests` **85/85** · `pnpm ts --force` clean ·
  all **41** Odoo probe tests complete in **77 ms** without the env var (gates hold).
- Working tree holds only the **user's own** unrelated edits under
  `Nomos.Web/Features/Files/Services/Owners/**` — deliberately never staged.

## 🚨 The brief's premise was void
Brief 04 was written to stop a convention promo double-applying a 10% loyalty reward. **There is
no reward line anywhere in the database**, no applied coupon, and program 2's trigger is
`with_code`, so nothing fires on `ActionConfirm`. Nothing to suppress; **no suppression code was
written**, deliberately. The door is marked: switching a program to `trigger = auto` would create
the double discount for real, and nothing guards it.

## What was actually wrong
Convention pricelists are maintained as **percentages off `Default`**; `CheckoutService.cs:235`
filtered items to `compute_price = "fixed"`; so the checkout rendered **zero products** for every
convention pricelist. **All 26 self-checkout orders sit on `SaaS Checkout` at public prices,
campaign `organic` — no convention customer has ever been quoted a convention price.**
Product owner's ruling: *"Add general support for percentages — the CRM team loves to have the
freedom of using percentages and fixed as needed."*

## Task log (newest first)
- **3.1 config doc** — ACCEPTED. `odoo/CONVENTION_PRICING_CONFIG.md`, 8 sections. Blocking gaps:
  pricelist 2 sells nothing (its rows are `3_global`, invisible to a per-product query); only
  `OA-Bolzano` → program 2 → pricelist 6 exists, so pricelists 2/3/5/7 are unreachable by any
  customer; pricelist 3 has no quantity-1 tier.
- **1.3 validity windows** (`4a96f3165`) — Odoo pricelist rows **cannot be archived**, so the date
  window is the only way to retire one, and the checkout read neither date. Now honoured, on base
  pricelists too. **Start inclusive, end exclusive** — matches how Odoo compares a midnight
  datetime and errs toward ending a discount a day early rather than a day late. Consequence to
  tell the CRM team: author `date_end` as the day *after* the intended last day.
- **2.1 verification probe** (`f8f0f8952`) — read-only, against production. **11 of 11 checkable
  real order lines agree to the cent**, across four pricelists and six pricelist rows, including
  a tier expansion onto `Default`'s volume tiers. Four lines disagree, **none refuting the code**:
  two match our figure exactly but on a different pricelist or quantity than the order records,
  two are internally inconsistent and reproducible at no pricelist and no quantity.
  **Public prices provably cannot move** — `SaaS Checkout` is 100% fixed rows so `Default` is
  never loaded on the public path, and recomputing the whole grid both ways moves **0 of 20**
  price points. `Default`'s 8 percentage rows are all on legacy template 9 and all
  `base='list_price'`, which the code refuses outright. **Named gap: 15 of 21 convention rows have
  no production figure to check against.**
- **1.2 promo failures** (`0034f3d73`) — `ResolveCode` matched with `=` (**case-sensitive**), so a
  link written `OA-BOLZANO` silently resolved to null and the customer paid public prices. Now
  `=ilike`, per the measured operator behaviour. A code that resolves but prices nothing now shows
  an Italian notice and logs an **Error** naming code/convention/pricelist. Deviation, accepted:
  it still falls back to public prices rather than a dead catalogue — but loudly on both channels.
  Probe later confirmed `=ilike` matches all four casings and that the wildcard escaping is
  load-bearing (`%` raw → 1 hit, `\%` → 0), though no stored code contains `%`, `_` or `\`.
- **1.1 percentage pricing** (`8cd8a5c07`) — percentage rows resolve against `base_pricelist_id`
  for the same product/plan/tier, rounded 2dp half-away-from-zero **at each step** (Odoo's
  `float_round`, not .NET's banker's default). Anything unresolvable — `formula`, percentage off
  `list_price`, missing base row, cycle, depth > 4, negative result — is **dropped with a warning**
  rather than guessed. Fixed rows pass through untouched. Deviation, good: the base's **quantity
  tiers are carried across**, because pricelist 6's single row is min-qty 0 and a tier-for-tier
  reading would price a 10-seat firm at the 1-seat price.
- **Spike** (`eb537e478`) — the finding above.

## Hard constraints
1. No EF migration. 2. Never `dotnet ef database update`. 3. Odoo is production — probes read-only
and gated on `ODOO_LIVE_PROBE=1`. 4. `Libraries/Odoo/{Entities,Models}` are generated — never
hand-edit, never run the generator. 5. Never run `Odoo.Tests` or `Nomos.Tests` unfiltered.

## Deferred follow-ups
- [ ] **The order-line shape.** We write `price_unit` = effective price, `discount` = 0. Odoo's own
      manual orders use base price + `discount` %, which makes the convention saving **visible on
      the invoice**. A product decision, not built.
- [ ] `loyalty.rule` / `loyalty.reward` / `loyalty.card` have **no generated models**, and
      `SaleOrder`/`SaleOrderLine` carry zero loyalty fields — the spike read everything raw.
      Same for `account.tax`.
- [ ] `pricelist_ids` on a loyalty program is a many2many **with no sequence**, so `ResolveCode`'s
      `pricelist_ids[0]` is arbitrary the moment a program carries two. Only one today.
- [ ] `sale.order.line.is_reward_line` is **not stored** on Odoo 19 — searching it raises; reward
      lines must be found via `reward_id`.
