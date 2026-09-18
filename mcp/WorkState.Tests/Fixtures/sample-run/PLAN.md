# Token security — Phases 1–5

## Goal

Every credential this product issues carries an explicit, verifiable purpose, and every authorization
policy accepts only credentials minted for it. When this is done: a JWT issued to Gestac cannot call the
product API or `/mcp`; a web-session JWT cannot call `/api/gestac/*`; the MCP integration key can no
longer select which user it acts as, and cannot act as a disabled one; the tenant service account is
resolved by role rather than by an e-mail prefix; the chat token handed to OpenAI expires in hours
rather than a month and dies when the user's security stamp changes; and an MCP caller's tool scope
comes from signed claims with real ACLs behind them rather than from request headers. A human confirms
this by running `dotnet test Nomos.Tests/Nomos.Tests.csproj` — specifically the credential×policy matrix
added in Phase 1, which asserts one allowed cell per credential — and by reading `AnonymousSurfaceTests`
and `ConnectorFlowTests` still green, which prove the public surface and the Claude/ChatGPT connector
handshake did not change.

Audit codes closed: `K0`, `K1`, `K2` (reach only — see constraint 3), `K3`, `K4`, `K6`, `K8`, `A9`,
`Z6` (reachability via token).

## Binding constraints from the owner

1. **`X-DIKAIA-USER-ID` goes.** Identity comes from the JWT. The header has exactly one reader in the
   repo (`McpUserService.cs`) and no other caller anywhere.
2. **The MCP integration key stays**, as a platform-only credential. `Nomos.Tests/Mcp/McpTests.cs` is its
   only holder in this repo. Do not delete the `McpToken` scheme.
3. **Gestac keeps the `TenantAdmin` service-account token** in the `/tokens` response. Do **not** add
   `WhereNotTenantAdmin` to `GestacUserService.GetUserTokens`. Phase 1 confines that token to
   `/api/gestac/*`; its 8-hour non-revocability is accepted, not fixed.
4. **Gestac integration keys may not be altered** — re-issuing requires a support call. No MAC, no
   expiry enforcement on `GestacKey`. Out of scope.
5. **No prompt-cache regressions.** Do not restructure chat history. `consumption_tokens` shows 65.5%
   cached input on `gpt-5.2` over 30 days; that is what a careless change would put at risk.

## Phases

### Phase 1 — Audience separation *(the foundation; everything else is smaller after it)*

Today every JWT — web login, Gestac user tokens, the OpenAI chat token — comes out of
`UserService.CreateUserToken` with issuer `nomos` and audience `nomos-client`, signed by one key and
validated by one `Bearer` scheme. They are indistinguishable, so every policy naming `Bearer` accepts
all of them. That single fact is `K2`'s reach, `K3`'s reach, `A9`, and `Z6`'s reachability.

| Task | Deliverable | Verify |
|---|---|---|
| **T1.1** | `NomosAudiences` constants (`dikaia:app` / `dikaia:gestac` / `dikaia:mcp`). Minting takes an explicit audience; every call site passes one. Register `GestacJwt` + `McpJwt` schemes. **All schemes temporarily accept all three audiences plus the legacy `nomos-client`** so the tree stays green and no behaviour changes yet. | `dotnet build Nomos.slnx` clean **and** `dotnet test --filter "FullyQualifiedName~Infrastructure\|FullyQualifiedName~Mcp"` green — a no-regression task, so green is the criterion |
| **T1.2** | **Test first, then the change.** Write `TokenAudienceTests`: every credential × every policy, one allowed cell per credential row. **Red-check it against the un-repointed code and report the failure count** — it must fail, because today every policy accepts every JWT. Then repoint policies (default → Cookie + `Bearer`; `gestac` → `GestacToken` + `GestacJwt`; `mcp` → `McpToken` + `McpJwt` + `McpOAuth`) and narrow each scheme to its own audience, `GestacJwt` also accepting legacy `nomos-client` for one release. | `dotnet test --filter "FullyQualifiedName~TokenAudienceTests"` green, having first been red |

### Phase 2 — The MCP integration key (`K0`, `K1`, `K8`)

`K0` is the live admin-tenant key committed in `McpTests.cs`. Revoking the DB row is the owner's task;
getting it out of tracked source is ours.

| Task | Deliverable | Verify |
|---|---|---|
| **T2.1** | The test credential moves out of tracked source (configuration / `.env`, skipped when absent, never silently passing). | `dotnet test --filter "FullyQualifiedName~McpTests"` |
| **T2.2** | Delete the `X-DIKAIA-USER-ID` branch in `McpUserService.Authorize`. Add `TenantUserService.IsActive` before `Login`, as the Gestac path already does. | `dotnet test --filter "FullyQualifiedName~Mcp"` |

### Phase 3 — The service account (`K6`)

`UserService.GetTenantAdmin` finds it with `Email.StartsWith("admin@")` — no ordering, no role check — so
a firm that invites a real `admin@studiolegale.it` non-deterministically shadows it, and background jobs
run as that human. `WhereNotTenantAdmin` / `WhereTenantAdmin` already resolve by role and are the model.

| Task | Deliverable | Verify |
|---|---|---|
| **T3.1** | `GetTenantAdmin` resolves on the `TenantAdmin` role, deterministically. | `dotnet test --filter "FullyQualifiedName~Tenancy"` |

### Phase 4 — The chat token (`K3`)

`AiToolProvider.cs` mints a 30-day token carrying the user's whole Identity principal. Phase 1 already
confines it to `/mcp`. Two things remain, neither of which touches history bytes.

| Task | Deliverable | Verify |
|---|---|---|
| **T4.1** | Lifetime 30 days → 48 hours. The existing daily strip bounds replay to under 24h, so 48h is safe under either reading of OpenAI's undocumented replay behaviour. | `dotnet test --filter "FullyQualifiedName~Chats"` |
| **T4.2** | Carry the Identity security stamp in the token and validate it per request, so a password change / disable / sign-out-everywhere kills outstanding chat tokens. | `dotnet test --filter "FullyQualifiedName~Mcp"` |

### Phase 5 — MCP tool scoping (`K4`)

Four compounding defects, ordered by what makes the others exploitable.

| Task | Deliverable | Verify |
|---|---|---|
| **T5.1** | Gate `tools/call`, not only `tools/list`. Today `McpFeatureAttribute` is consulted only by `AddListToolsFilter`, so a client that hard-codes a name invokes tools filtered out of its listing. | `dotnet test --filter "FullyQualifiedName~Mcp"` |
| **T5.2** | `McpHelper.GetValue` reads claims only — no header fallback. `McpFeatureAttribute.IsValid` defaults to **deny**. | `dotnet test --filter "FullyQualifiedName~Mcp"` |
| **T5.3** | Real ACLs in `ChatFileSearchHelper` (user↔chat ownership) and `LibraryDocumentSearchHelper` (`LibraryUser` membership). Both enforce tenancy only today, despite a doc-comment claiming otherwise. | `dotnet test --filter "FullyQualifiedName~Mcp|FullyQualifiedName~Chats"` |

### Phase 6 — The connector can refresh silently (owner-requested)

Today `OpenIddictInstaller` allows `AuthorizationCodeFlow` only, and dynamic client registration grants
`GrantTypes.AuthorizationCode` with no `offline_access`. Claude therefore receives a 1-hour access token and
nothing to renew it with: at the hour mark the connector needs a front-channel trip through
`/connect/authorize`, which skips the consent screen only while the user's `.dikaia.ai` cookie is alive.

This is a feature, but it *tightens* security rather than loosening it. OpenIddict re-checks the
authorization entry on every refresh redemption, so the revocation added in `042423be7` starts biting at the
next refresh instead of coasting to the hour — right now that revocation is blunted by there being no
refresh to intercept.

| Task | Deliverable | Verify |
|---|---|---|
| **T6.1** | `AllowRefreshTokenFlow()`, `offline_access` registered and advertised, `GrantTypes.RefreshToken` + the offline scope on dynamically registered clients, and `/connect/token` handling `grant_type=refresh_token`. **The refresh branch re-validates the user** — a disabled account must not renew. | `dotnet test --filter "FullyQualifiedName~ConnectorFlow\|FullyQualifiedName~OAuth\|FullyQualifiedName~Mcp"` |

## Sequencing note

T5.2 removes the header fallback that `McpTests` currently drives (`X-DIKAIA-TOOLS`, `-CASEID`,
`-CHATID`, `-LIBRARYID`). Those tests must move to minted claims — coordinate with T2.1, which is
already rewriting how those tests authenticate.
