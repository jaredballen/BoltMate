# Web portal plan

Phased build of the BoltMate marketing site + account portal + supporting
backend completion. Living document — update phase status as work lands.

## Locked decisions

| Decision | Value |
|---|---|
| Frontend stack | Astro on Azure Static Web Apps (Free tier) |
| Pricing | $14.99 one-time, Stripe = source of truth, build-time fetch |
| SKU | `boltmate` (rename `LicenseSkus.Pro` → `LicenseSkus.Boltmate`) |
| Identity | Azure AD B2C, OAuth providers: Apple, Google, LinkedIn, GitHub, Facebook |
| Auth requirement | Required for **all** app use (single-machine use is not a goal) |
| Trust ring | B2C `sub` claim match between peers, auto-respond |
| Support — site | `/support` page with anonymous email field path |
| Support — app | In-app "Send logs" button; bundles local + peer logs |
| Sign-out | Wipes Keychain entry, stops topology, reopens welcome |
| Domain | `boltmate.app` (Cloudflare registrar + DNS) |
| Subdomains | `boltmate.app` (site), `api.boltmate.app` (Functions), `auth.boltmate.app` (B2C) |
| Email sender | `noreply@boltmate.app` via Resend |

## Pending decisions

- **App download hosting**: GitHub Releases (current) vs Azure Blob + CDN. Pick before Phase 3.
- **Privacy policy + ToS**: draft yourself or use Termly/iubenda. B2C onboarding mandatory.
- **Analytics**: skip for v1 or add Plausible ($9/mo)?

## Phases

### Phase 0 — Infrastructure provisioning

Status: **complete** — Apple/LinkedIn/Facebook/GitHub OAuth IdPs deferred to wire when those provider apps are ready.

- [x] Azure subscription confirmed + renamed to `BoltMate`, resource group `boltmate-prod` created (eastus2, tagged)
- [x] Entra External ID tenant `BoltMate` (`boltmateauth.onmicrosoft.com`),
      app registration `BoltMate` (one app, web + mobile/desktop redirect URIs),
      user flow `B2C_1_signup_signin` linked. Google IdP wired + tested end-to-end
      (real Gmail → Google consent → `boltmate.app/auth/callback?code=...`).
      Apple/LinkedIn/Facebook/GitHub deferred.
- [x] Cosmos DB Free Tier (`boltmate-prod-cosmos`), database `boltmate`,
      containers `Licenses` (pk `/email`) + `RefreshLog` (pk `/licenseId`, TTL 30d)
- [x] KeyVault provisioned (`boltmate-prod-kv`, RBAC mode), secrets seeded:
      `Stripe--SecretKey` (test mode), `Stripe--PublishableKey` (test),
      `Stripe--WebhookSecret`, `Resend--ApiKey`, `B2C--ClientId`,
      `B2C--TenantId`, `B2C--Authority`, RSA key `boltmate-jwt-signing`
      (2048, sign/verify only).
- [x] Function App `boltmate-prod-api` (Consumption Linux, dotnet-isolated 10).
      Custom domain `api.boltmate.app` bound (CNAME + asuid TXT validated).
      System-assigned managed identity granted KV Secrets User + Crypto User + Cosmos Data Contributor.
- [x] Storage Account `boltmateprodstorage` (Standard_LRS), container `support-bundles`,
      lifecycle policy: auto-delete after 30 days
- [x] Static Web App `boltmate-prod-web` (Free tier) — apex + www custom domain
      DNS-TXT validated, SSL certs auto-issued by Azure
- [x] Stripe: Product `boltmate` (`prod_UmBxsLh8wgbAFZ`), Price `$14.99 one-time USD`
      (`price_1TmdZyLkIsnQS4tDrUGhDrBg`, lookup key `boltmate_lifetime`)
- [x] Stripe webhook endpoint `we_1TmeI1LkIsnQS4tDzHOPy6mo` → `https://api.boltmate.app/api/stripe-webhook`,
      events: `checkout.session.completed`, `charge.refunded`, `price.updated`. Sandbox mode.
- [ ] Stripe restricted API keys: site-build (read prices), webhook (full) — currently
      using CLI-generated test key (expires 2026-09-24), swap to Restricted Key before going live
- [x] DNS records via Cloudflare: `api.boltmate.app` CNAME (proxy off),
      `asuid.api.boltmate.app` TXT, apex `boltmate.app` CNAME (proxy on, flattened),
      `www.boltmate.app` CNAME (proxy on), `_dnsauth.boltmate.app` + `_dnsauth.www.boltmate.app` TXT validators
- [x] Application Insights `boltmate-prod-insights` for Function telemetry
- [x] Resend signed up + `boltmate.app` domain verified (DKIM + SPF + bounce
      MX on `send.boltmate.app`). API key stashed in KV.
- [x] Cloudflare Email Routing enabled on `boltmate.app`. Rule:
      `support@boltmate.app` → `jaredballen+boltmate@gmail.com`.
      Catch-all: Drop. CF auto-managed MX + DKIM + SPF records on apex.

### Phase 1 — Backend completion

Status: **complete**

- [x] Rename `LicenseSkus.Pro = "boltmate-pro"` → `LicenseSkus.Boltmate = "boltmate"`
- [x] `LicenseTier` enum: dropped `Free`; only `Trial=1` and `Boltmate=2`
      remain. `LicenseStatus.Tier` is nullable for unentitled states
      (NotActivated / Revoked / SignatureInvalid). `JwtVerifier` rejects
      JWTs whose tier claim isn't a known value (was silently defaulting
      to Free, which no longer exists).
- [x] `EntitlementFunction` auto-provisions a 14-day Trial on first hit
      w/ no license. Optional `hardware_id_hash` body field gates the
      12-month re-trial block (configurable via `TrialReuseBlockDays`).
      JWT `ExpiresAt` clamped to the license's own `ExpiresAt` so a
      14-day Trial can't hand out a 30-day refresh token. New
      `trial_reused` error code on `EntitlementErrorCodes`. New
      `EntitlementRequest` wire type with optional `hardware_id_hash`.
- [x] `StripeWebhookHandler` completion:
      - `checkout.session.completed` upgrades existing Trial → Boltmate
        in place. Preserves `HardwareIdHash` + `TrialOriginAt` so the
        12-month block survives any future refund. Fresh purchase (no
        prior Trial) creates a Boltmate record directly. Idempotent via
        tier+SessionId equality check on re-delivery.
      - `charge.refunded` / `charge.dispute.created` reverse-lookup by
        Stripe `PaymentIntentId` → `status="revoked"`, tier → Trial,
        `ExpiresAt` → `RevokedAt`. Idempotent via already-revoked check.
      - Internal `DispatchAsync(Event)` seam exposed via
        `InternalsVisibleTo` so tests can skip signature verification.
- [x] `SupportFunction` rewritten for dual content-type:
      `application/json` (anonymous site form) and `multipart/form-data`
      (in-app "Send logs" with bundle attachment). Optional Bearer ID
      token validated via `IIdTokenValidator` — when present, overrides
      submitted email and tags `source=authenticated`. New
      `ISupportBundleStore` + `BlobSupportBundleStore` upload bundles to
      Azure Storage `support-bundles` container via the Function App's
      managed identity and return a user-delegation SAS URL (30-day
      default, matched by storage lifecycle auto-delete). Hard cap on
      bundle size via `SupportBundleMaxSizeMB` (default 25 MB).
      `SupportTicket` carries `BundleUrl` + `BundleSizeBytes` + `Source`
      through to `ResendSupportTicketSink`.
- [x] Backend tests: 18 new (EntitlementFunctionTests×7,
      StripeWebhookHandlerTests×6, SupportFunctionTests×6) + the
      existing 12 = 31 total in `BoltMate.Licensing.Tests`, all passing.
      `ApiFakes.cs` provides in-memory doubles for the seven service
      interfaces touched in Phase 1.

Tooling changes:
- `Microsoft.Azure.Functions.Worker.*` 2.0.0 → 2.0.7 (.NET 10 compat)
- `Azure.Identity` 1.13.1 → 1.17.0 (transitive minimum)
- New package `Azure.Storage.Blobs` for support bundle uploads
- `BoltMate.LicenseApi` granted the `Storage Blob Data Contributor`
  role on `boltmateprodstorage` so the managed identity can write to
  `support-bundles` and mint user-delegation SAS tokens.

### Phase 2 — Site MVP

Status: **not started**

- [ ] New top-level `web/` dir, Astro project (TypeScript, no UI lib —
      hand-rolled to match the prototype)
- [ ] Pages: `/` (Landing), `/pricing`, `/support`, `/privacy`, `/terms`
- [ ] Brand: green `#99FF55` on `#1C1C1E`, SF Pro Text + SF Mono, 1120 px
      max-width container, ~80 px vertical rhythm. Logo `BoltMate_logo.svg`.
- [ ] Build-time Stripe fetch: `astro.config.mjs` calls Stripe Prices API
      during build, bakes price into HTML
- [ ] `staticwebapp.config.json`: auth routes + B2C OIDC binding
- [ ] `/support` form posts to `/api/support` (no zip from site, no Bearer)
- [ ] GitHub Actions: build + deploy SWA on push to `main`

### Phase 3 — Checkout + Account

Status: **not started**

- [ ] `/checkout` 2-step (Sign-in → Stripe Checkout redirect). On success,
      redirect to `/account`.
- [ ] `/account` page (B2C-gated). Calls `/api/entitlement` with Bearer
      token, renders trial countdown or "licensed for life" badge.
- [ ] Download buttons for `.dmg` / `.msi` (links to GitHub Releases artifacts
      or Azure Blob — TBD)
- [ ] Trial-expired re-entry banner per design

### Phase 4 — App auth integration

Status: **not started**

- [ ] Welcome wizard new page 1: "Sign in to continue" using
      `LoopbackAuthFlow` (already scaffolded). Skips topology + HID prompts
      until signed in.
- [ ] `LicenseGate` (already scaffolded) wired into `App.OnFrameworkInitializationCompleted`
      — block bootstrap until cached entitlement valid OR fresh OAuth completes
- [ ] Sign-out: tray menu item "Sign out". Wipes Keychain entry, stops topology,
      drops mDNS advert, reopens welcome at sign-in step.
- [ ] `AppSettings.json` cleanup:
  - Drop `HostNames`, `Receivers` dict, `ReceiverSettings` class
  - Drop all `Topology.*` constants except `Enabled`. Move port,
    multicast group, intervals, broadcast cadence, mDNS service type
    to `TopologyConstants` static class in Core.
  - Drop `Topology.MachineId`
  - `LastUpdateCheckUtc` moves to separate `state.json`
- [ ] New `IMachineIdProvider`: derive from OS hardware ID.
  - Mac: `ioreg -d2 -c IOPlatformExpertDevice` parsed for IOPlatformUUID
  - Win: `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` read
  - Linux: `/etc/machine-id`
- [ ] Stale CLAUDE.md `CachedHostBindings` line removed

### Phase 5 — Log collection

Status: **not started**

- [ ] `BoltMate.App/Services/Support/LogBundler.cs`: zips
      `~/Library/Logs/BoltMate/boltmate-*.log` (or Win equivalent)
      + scrubbed `settings.json` + version + OS info
- [ ] `MdnsTcpChannel` new message types: `LogBundleRequest { bearer }` /
      `LogBundleResponse { zip bytes }`. Peer verifies token signature
      offline via `JwtVerifier`, compares `sub` claim to cached own `sub`,
      auto-responds.
- [ ] Settings → General → "Send logs" button. Dialog: description textarea
      (always required) + email field (shown only if not logged in).
      Assembles outer bundle (this-host + peer responses, 5s timeout),
      posts multipart to `/api/support`.
- [ ] Site `/support` posts same endpoint without zip

### Phase 6 — Polish + ops

Status: **not started**

- [ ] Stripe webhook on `price.updated` → GitHub `repository_dispatch` →
      SWA rebuild (price changes propagate without manual deploy)
- [ ] Resend email templates: purchase confirmation, trial T-3/T-1/expired,
      support ticket received
- [ ] Account deletion endpoint (`DELETE /api/entitlement`) for GDPR —
      wipes license + refresh log rows
- [ ] CLAUDE.md: add web portal section pointing at `web/` + this doc
- [ ] New `doc/site-architecture.md`: site layout, auth flow, Stripe wiring
- [ ] Update `doc/licensing_architecture.md` to reflect $14.99 + `sub`-based
      trust ring + auto-trial provisioning

## Deferred from Phase 0

- **Apple + Facebook OAuth IdPs**: original design called for 5 providers
  (Apple, Google, LinkedIn, GitHub, Facebook). Phase 0 only wired Google.
  Apple + Facebook step-by-steps in
  `~/.claude/projects/.../memory/project_todo_oauth_providers.md`.
  Wire before Phase 2 site launch — visible on `/checkout` sign-in surface.
- **LinkedIn + GitHub OAuth IdPs**: further deferred. LinkedIn's consumer
  use case is questionable; GitHub requires custom OIDC provider config
  (no built-in IdP in Entra External ID).
- **Stripe live mode**: currently sandbox. Swap to live mode + replace
  CLI-generated test key with a Restricted Key (read prices for site
  build, full for webhook handler) before public launch.

## Open notes / future considerations

- `auth.boltmate.app` Cloudflare CNAME must be **proxy off** (grey cloud) so
  B2C cert validation works. `boltmate.app` and `api.boltmate.app` proxy on
  is fine.
- Cosmos Free tier is one per Azure subscription — confirm BoltMate is the
  one using it.
- B2C 50K MAU free → above that, $0.00325/MAU. Watch the metric.
- Function App Consumption plan: 1M executions/month free.
- Site analytics deliberately deferred. If Plausible added later, place its
  script behind a cookie-consent flow.
