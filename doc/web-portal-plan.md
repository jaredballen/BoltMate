# Web portal plan

Phased build of the BoltMate marketing site + account portal + supporting
backend completion. Living document — update phase status as work lands.

## Locked decisions

| Decision | Value |
|---|---|
| Frontend stack | Astro on Azure Static Web Apps (Free tier) |
| Pricing | $14.99 one-time, Stripe = source of truth, build-time fetch |
| SKU | `boltmate` (rename `LicenseSkus.Pro` → `LicenseSkus.Boltmate`) |
| Identity | Azure AD B2C, OAuth providers: Apple, Google (LinkedIn + GitHub deferred; Facebook cut — Meta Business Verification not worth it for a solo project) |
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

Status: **complete** — LinkedIn/GitHub OAuth IdPs deferred; Facebook cut.

- [x] Azure subscription confirmed + renamed to `BoltMate`, resource group `boltmate-prod` created (eastus2, tagged)
- [x] Entra External ID tenant `BoltMate` (`boltmateauth.onmicrosoft.com`),
      app registration `BoltMate` (one app, web + mobile/desktop redirect URIs),
      user flow `B2C_1_signup_signin` linked. Google + Apple IdPs wired + tested end-to-end
      (real provider → consent → `boltmate.app/auth/callback?code=...`).
      LinkedIn/GitHub deferred; Facebook cut.
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

Status: **complete**

- [x] Top-level `web/` Astro project (TypeScript, no UI lib —
      hand-rolled). `astro build` produces a static `dist/` directory
      served by SWA Free. `Base.astro` layout shared across all pages
      (sticky nav, Logitech-trademark footer disclaimer, brand mark
      lockup inline as SVG).
- [x] Pages: `/` (Landing), `/pricing`, `/support`, `/privacy`,
      `/terms`, branded `/404`.
- [x] Brand tokens in `src/styles/tokens.css` mirror the desktop
      `Theme/DesignTokens.axaml` (green `#99FF55` on `#1C1C1E`,
      SF Pro Text + SF Mono, 1120 px max-width, ~80 px vertical
      rhythm). Favicon is a squircle-framed bolt mark.
- [x] Build-time Stripe fetch: `src/lib/pricing.ts` calls
      `stripe.prices.list({ lookup_keys: ["boltmate_lifetime"] })` at
      build time using `STRIPE_SECRET_KEY` from env. Falls back to a
      hard-coded `$14.99` / current Price ID when no key is present
      so local dev builds still render.
- [x] `web/staticwebapp.config.json`: B2C OIDC provider `aad` wired
      to `boltmateauth.ciamlogin.com`, `/api/*` rewrites to
      `https://api.boltmate.app/api/*`, `/account/*` + `/checkout/*`
      gated to the `authenticated` role, 401s redirect to
      `/.auth/login/aad`. Default security headers
      (HSTS, nosniff, strict referrer) applied globally.
- [x] `/support` form posts JSON to `/api/support` (no zip from site,
      no Bearer) — Phase 1 SupportFunction accepts this content type
      already.
- [x] GitHub Actions `.github/workflows/swa-deploy.yml`:
      pushes to `main` that touch `web/` deploy to the Static Web
      App; pull requests get a per-PR staging environment from the
      SWA action. `AZURE_STATIC_WEB_APPS_API_TOKEN` and
      `STRIPE_SECRET_KEY` seeded as GH repo secrets.

### Phase 3 — Checkout + Account

Status: **complete**

- [x] `/checkout` page acts as a hand-off shim — Easy Auth's
      `allowedRoles: ["authenticated"]` rule sends the user through
      sign-in if needed, then a small inline script POSTs the SWA-
      injected ID token to `/api/checkout`, receives the Stripe
      Checkout Session URL, and `window.location`s there. Cancel /
      retry / error states all surface on the page.
- [x] New `CheckoutFunction` (`POST /api/checkout`). Validates the
      Bearer ID token via the existing `IIdTokenValidator`, resolves
      the active Stripe Price by lookup key `boltmate_lifetime` (same
      key the site uses at build time so a live-mode swap doesn't break
      either side), creates a Checkout Session with `mode=payment`,
      `customer_email=<token email>`, `metadata={sku: boltmate,
      oauth_sub: <subject>}`, success URL
      `/account?checkout=success&session_id={CHECKOUT_SESSION_ID}` and
      cancel URL `/pricing?checkout=cancelled`. Returns
      `{ url, sessionId }`. New `LicenseApiOptions` keys
      `StripePriceLookupKey` + `SiteOrigin`.
- [x] `/account` page (B2C-gated). After Easy Auth lands, fetches
      `/.auth/me` to read the principal + id token, POSTs an empty body
      to `/api/entitlement` (so the EntitlementFunction auto-provisions
      a Trial if no license exists yet), and decodes the returned JWT
      to render: tier + issued + expires/status + email, download
      buttons, sign-out row, and a trial-expired banner when the JWT
      exp claim is in the past. Loading / Unauth / Error states all
      handled.
- [x] Download buttons for `.dmg` / `.exe` link to the latest GitHub
      Release. Azure Blob + CDN hosting deferred — the GH Releases
      surface is good enough for v1 and is the same shelf the
      desktop-side `UpdateService` will check against.
- [x] Trial-expired re-entry banner per the design handoff — dark
      near-black bar with "Buy lifetime — $14.99" CTA, auto-displayed
      when `exp <= now` on the decoded entitlement JWT.

### Phase 4 — App auth integration

Status: **complete**

- [x] `BoltMate.App` now references `BoltMate.Licensing` +
      `BoltMate.Licensing.Contracts`. `ServiceRegistration` calls
      `AddBoltMateLicensing(...)` with production wiring: `Issuer` +
      `EntitlementEndpoint` = `https://api.boltmate.app`, OAuth
      authorize/token endpoints on the BoltMate Entra External ID
      tenant (`boltmateauth.ciamlogin.com`/`<tenant-guid>`),
      `OAuthClientId` = the BoltMate app registration's GUID, scopes
      `openid email profile offline_access`. `PublicKeyPem` left empty
      for now — `LicenseGate.EvaluateStored` returns `SignatureInvalid`
      until Phase 6 ships a stable verify key, which is fine because
      the wizard's sign-in step forces a fresh OAuth round-trip.
- [x] `App.OnFrameworkInitializationCompletedCore` resolves
      `ILicenseGate` from DI, seeds `_licenseStatus` from `.Current`,
      subscribes to `.StatusChanges` for the App layer to observe, and
      fires `LoadAsync` fire-and-forget.
- [x] `WelcomeViewModel` + `WelcomeWindow.axaml` gain a new
      `PageSignIn` (default first page). Skipped automatically when
      `LicenseGate.Current.IsEntitled` already holds. New
      `SignInCommand` awaits `LicenseGate.ActivateAsync()` →
      `LoopbackAuthFlow` opens the system browser, swaps the loopback
      auth code for tokens, hits `/api/entitlement`. On
      `Valid`/`GracePeriod` we advance to `PageWelcome`. `SignInStatus`
      + `SignInBusy` drive the UI feedback.
- [x] Tray menu gains a `Sign out` item. App-side
      `SignOutAndReopenSignInAsync` awaits `LicenseGate.SignOutAsync()`
      (wipes Keychain / DPAPI entry), stops the UDP + mDNS+TCP
      topology stack so the machine drops off the LAN trust ring,
      flips `HasShownWelcome=false` + saves, and reopens the welcome
      wizard at `PageSignIn`.
- [x] `AppSettings.json` slimmed down:
      - `HostNames`, `Receivers` dict, `ReceiverSettings` class — gone
        (no production callers; only Logi+ -overlapping renaming UX
        that was already out of scope per CLAUDE.md).
      - `Topology.MachineId` — gone. UDP topology now consumes
        `IMachineIdProvider.GetMachineId()` instead of reading from
        settings.
      - Topology constants (`Port`, `MulticastGroup`, intervals,
        cadence, mDNS service type) **deferred**: the
        `TopologyConstants` extraction changes the ctor signatures on
        `UdpTopologyService` + `MdnsTcpChannel` and isn't strictly
        needed for the Phase 4 auth integration. Tracked as
        follow-up.
      - `LastUpdateCheckUtc` → separate `state.json` **deferred**:
        same rationale. Field still lives on `AppSettings` for now.
- [x] New `IMachineIdProvider` in `BoltMate.Core.Topology` +
      `HardwareMachineIdProvider`: probes
      `ioreg -d2 -c IOPlatformExpertDevice` → IOPlatformUUID (macOS),
      `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` (Windows),
      `/etc/machine-id` (Linux). Result is SHA-256 of the salted raw
      value so the topology MachineId isn't correlatable with anything
      else reading the same OS identifier. `Lazy<string>` caches the
      computation per process.
- [x] Stale `CLAUDE.md` `CachedHostBindings` line removed +
      replaced with the actual in-memory `PairedDevice.HostBindings`
      truth.

### Phase 5 — Log collection

Status: **complete** (excluding site `/support` form — covered by Phase 6)

- [x] `BoltMate.App/Services/Support/LogBundler.cs`: zips
      `~/Library/Logs/BoltMate/boltmate-*.log` (or Win equivalent)
      + scrubbed `settings.json` + version + OS info
- [x] `MdnsTcpChannel` new message types: `LogBundleRequest` / `LogBundleResponse`
      ride a discriminated `TcpFrame` envelope. Trust ring is **cryptographic**
      via the per-account AES-GCM SyncKey shipped with the entitlement
      (#101–#104) — a peer can only request or honour log bundles if it
      decrypts our envelope. Cleaner than the original JWT-signature plan
      and forward-compatible with the eventual ephemeral DH upgrade.
- [x] Settings → General → "Send logs" section. Description textarea
      (required) + email field (shown only if signed out). Assembles outer
      bundle (this-host + peer responses, 5 s timeout) and posts multipart
      to `/api/support`.
- [x] Site `/support` posts same endpoint without zip (already shipped
      ahead of Phase 6 — `SupportFunction.cs` handles both JSON + multipart)

### Phase 6 — Polish + ops

Status: **complete** (sans launch-day Stripe swap, runbook ready)

- [x] Stripe webhook on `price.updated` → GitHub `repository_dispatch` →
      SWA rebuild. `IGitHubDispatcher` + filter on `boltmate_lifetime`
      lookup key so dashboard noise doesn't bounce the build.
- [x] Resend email templates: purchase confirmation, trial T-3 / T-1 /
      expired, support ticket received. Templates are inline HTML in
      `ResendEmailNotifier`. Trial reminders driven by daily
      `TrialReminderFunction` TimerTrigger with per-stage dedup flags.
- [x] Account deletion endpoint (`DELETE /api/entitlement`) for GDPR —
      wipes license + refresh log rows. Idempotent (204 either way) so
      the endpoint never reveals whether an email existed. Site
      `/account` gains a danger-zone delete button.
- [x] CLAUDE.md gains a "Web portal" section pointing at `web/` + the
      plan + architecture docs.
- [x] `doc/site-architecture.md` covers layout, hosting + DNS, auth
      flow, Stripe wiring, build/deploy triggers, and the
      site→LicenseApi contract.
- [x] `doc/licensing_architecture.md` rewritten — sections 1–4 reflect
      the shipped stack (Boltmate not Pro, SyncKey peer crypto, daily
      reminder timer, GDPR delete); the original Phase-0 proposal is
      preserved as section 5 for history.
- [ ] **Stripe live-mode swap** — runbook at
      `doc/stripe-live-mode-runbook.md` captures the dashboard +
      Azure App Configuration steps. Executed on launch day, not now.

### Phase 7 — Local Astro dev env

Status: **not started**

- [ ] `web/README.md` walks a fresh contributor from clone to running
      site in under 5 minutes (`npm ci`, `npm run dev`, hot reload notes).
- [ ] `.env.example` listing every var the build expects. At minimum:
      `STRIPE_SECRET_KEY` (sandbox / test-mode key, `sk_test_…`).
- [ ] Confirm `loadPricing()` falls back cleanly when the env var is
      absent or test-mode hits the wrong account (default-price banner).
- [ ] Note in the README which dashboard creates the sandbox Price /
      lookup key + how to make sure `boltmate_lifetime` exists in test mode.

### Phase 8 — Rebuild full site from design handoff

Status: **not started**

- [ ] Visual system: brand green `#99ff55` + near-black `#1c1c1e`, SF
      Pro Text body + SF Mono eyebrows, light surfaces, 1120 px content
      width, 28 px gutters. Swap CSS variables in `Base.astro`.
- [ ] **Landing** — sticky blurred nav, centered dark radial hero
      ("Brandful" direction), 92 px glowing app tile, 60 px headline
      "Let the keyboard lead. The whole desk follows.", animated
      two-tile device diagram (`@keyframes bm-travel`), 3 how-it-works
      cards, dark privacy band (3 rows), pricing teaser, footer.
- [ ] **Pricing** — two price cards (Free trial 14d + Lifetime $14.99)
      + 4-item FAQ.
- [ ] **Checkout** — 2-step indicator (Account → Payment) + order
      summary rail. OAuth providers: Apple + Google only (Facebook cut,
      LinkedIn/GitHub deferred — show only the wired ones). Real Stripe
      Checkout redirect; no mock card form.
- [ ] **Account / post-purchase** — trial + owned modes, license card
      (provider + email, no key), `.dmg` + `.msi` downloads, version
      line, activation note, trial-expired entry banner toggle. Keep the
      Phase 6 GDPR delete button.
- [ ] Assets: ship `boltmate-mark.svg` to `web/public/`. Match radii
      (hero panels 20–26, cards 14–20, buttons 9–12, pills 20).
- [ ] Strip the handoff's $29 placeholder; `loadPricing()` already
      ships the live $14.99.

## Deferred from Phase 0

- **Apple OAuth IdP**: wired 2026-06-28 (Services ID `app.boltmate.web`,
  .p8 Sign-In-with-Apple key uploaded to Entra Apple IdP blade, added to
  `signup_signin` user flow, end-to-end tested through `jwt.ms` round-trip).
- **Facebook OAuth IdP**: **cut**. Meta now requires Business Verification
  (incorporation docs, utility bills) for Live-mode apps requesting even
  basic `email` scope. Not viable for a sole-proprietor project with no
  LLC; marginal conversion lift doesn't justify the paperwork. Revisit
  only if BoltMate incorporates or user feedback explicitly demands it.
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
