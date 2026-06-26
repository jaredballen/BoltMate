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

Status: **not started**

- [ ] Azure subscription confirmed, resource group `boltmate-prod` created
- [ ] B2C tenant provisioned, OAuth providers configured (Apple, Google,
      LinkedIn, GitHub, Facebook), user flow for sign-up/sign-in
- [ ] Cosmos DB (Free tier — 1000 RU/s, 25 GB), database `boltmate`,
      containers `Licenses` + `RefreshLog`
- [ ] KeyVault provisioned, secrets seeded:
      `Stripe:SecretKey`, `Stripe:WebhookSecret`, `Resend:ApiKey`,
      `B2C:ClientId`, `JwtSigningKey` (RSA, managed key)
- [ ] Function App (Consumption plan) deployed from `BoltMate.LicenseApi`
      project. Custom domain `api.boltmate.app`.
- [ ] Storage Account for log blob retention, container `support-bundles`,
      lifecycle policy: auto-delete after 30 days
- [ ] Static Web App (Free tier) provisioned, custom domain `boltmate.app`
- [ ] Stripe: Product `boltmate`, Price `$14.99 one-time USD`
- [ ] Stripe webhook endpoint → `https://api.boltmate.app/api/stripe-webhook`,
      events: `checkout.session.completed`, `charge.refunded`, `price.updated`
- [ ] Stripe restricted API keys: site-build (read prices), webhook (full)
- [ ] DNS records via Cloudflare for the four hostnames

### Phase 1 — Backend completion

Status: **not started**

- [ ] Rename `LicenseSkus.Pro = "boltmate-pro"` → `LicenseSkus.Boltmate = "boltmate"`
- [ ] Add `LicenseTier.Trial`, update enum order if needed
- [ ] `EntitlementFunction`: auto-provision 14-day Trial on first hit with no
      license. Bind to verified email from B2C ID token. Hardware-ID hash anti-
      abuse: block re-trial within 12 months.
- [ ] `StripeWebhookFunction`: handle `checkout.session.completed` →
      upgrade Trial → Boltmate, set `ExpiresAt = null`. Handle `charge.refunded`
      → set status `Revoked`. Idempotent on retries (use `event.id` dedup).
- [ ] `SupportFunction`: accept multipart (description, email, optional
      zip). Bearer token optional — if present, validate via `IIdTokenValidator`
      and use token's email; else require submitted email. Write zip to
      Blob Storage. Resend email with download link.
- [ ] Backend tests: trial provisioning, Stripe webhook idempotency,
      support function multipart, refund flow

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
