# BoltMate site architecture

Companion to [`doc/web-portal-plan.md`](web-portal-plan.md) (phase plan)
and [`doc/licensing_architecture.md`](licensing_architecture.md)
(licensing stack). This document describes the production shape of the
marketing + account site as of Phase 6.

## Layout

```
web/
├── astro.config.mjs            # output:'static', no SSR
├── package.json                # @astrojs/check, Stripe SDK for build-time price fetch
├── public/                     # favicon, og-image, robots.txt
├── staticwebapp.config.json    # SWA Easy Auth + route rules
├── src/
│   ├── layouts/Base.astro      # nav + footer + global CSS variables
│   ├── lib/
│   │   ├── pricing.ts          # Stripe Price fetch at build time, hardcoded fallback
│   │   └── auth.ts             # /.auth/me + id_token extraction
│   ├── pages/
│   │   ├── index.astro         # marketing landing
│   │   ├── pricing.astro       # build-time price + FAQ
│   │   ├── checkout.astro      # launches Stripe Checkout via /api/checkout-session
│   │   ├── account.astro       # post-login: license info + downloads + GDPR delete
│   │   ├── support.astro       # anonymous JSON form → /api/support
│   │   ├── privacy.astro
│   │   ├── terms.astro
│   │   └── 404.astro
│   └── styles/                 # CSS variables for dark theme + brand
└── dist/                       # build output (gitignored)
```

## Hosting + DNS

| Origin | Host | Notes |
| :--- | :--- | :--- |
| `boltmate.app` | Azure Static Web Apps Free | Cloudflare DNS, proxy ON. |
| `api.boltmate.app` | Azure Functions Consumption | Cloudflare DNS, proxy ON. The SWA `staticwebapp.config.json` rewrites `/api/*` to this origin. |
| `auth.boltmate.app` | Entra External ID custom domain | Cloudflare DNS, proxy OFF — B2C cert validation requires direct CNAME. |

## Auth flow (site)

1. User clicks a Sign-in link → SWA Easy Auth redirects to
   `auth.boltmate.app/{tenant}/oauth2/v2.0/authorize?...&post_login_redirect_uri=/account`.
2. Entra renders the configured user flow (`B2C_1_signup_signin`) — Google + Apple options.
3. Successful login redirects back through SWA, which writes the
   id_token into the principal accessible at `/.auth/me`.
4. The page-level `<script>` blocks fetch `/.auth/me`, pull the
   `accessToken` (id_token), and Bearer-post it to `/api/entitlement`.
   The same token authenticates GDPR `DELETE /api/entitlement`.

## Stripe wiring

- One Price object in the live Stripe account, lookup key
  `boltmate_lifetime`. `loadPricing()` queries by lookup key at build
  time so the published page shows the current amount without code
  edits.
- `/checkout` posts to `/api/checkout-session` (`CheckoutSessionFunction`
  in LicenseApi) which creates a Stripe Checkout Session and returns its
  URL; the client redirects there.
- Webhook `/api/stripe-webhook` lives on the LicenseApi and handles:
  - `checkout.session.completed` → upsert license at Boltmate tier +
    send purchase-confirmation email
  - `charge.refunded` / `charge.dispute.created` → revoke
  - `price.updated` / `price.created` (canonical lookup key only) →
    `repository_dispatch` to GitHub → re-runs the SWA build with the
    fresh price

## Build + deploy

`/.github/workflows/swa-deploy.yml` runs on:

- push to `main` touching `web/**` (production)
- PR vs `main` touching `web/**` (preview environment per PR)
- `repository_dispatch` event type `stripe-price-updated` (price refresh)

The Azure SWA action calls `npm run build` with `STRIPE_SECRET_KEY` in
the env so `loadPricing()` can pull live data. Build output is `dist/`.

## Site → LicenseApi contract

| Site call | LicenseApi function | Auth |
| :--- | :--- | :--- |
| `POST /api/entitlement` | `EntitlementFunction.Run` | Bearer id_token |
| `DELETE /api/entitlement` | `EntitlementFunction.Delete` | Bearer id_token |
| `POST /api/checkout-session` | `CheckoutSessionFunction` | Bearer id_token (email picked from token claim) |
| `POST /api/support` (JSON) | `SupportFunction.Run` | Optional Bearer; anonymous OK |
| `POST /api/stripe-webhook` | `StripeWebhookFunction` | Stripe signature (`Stripe-Signature` header) |

## Non-goals

- No SSR runtime on the site. Astro `output: 'static'`; anything dynamic
  is fetched client-side after auth lands.
- No client-side framework (React / Vue / Svelte). Plain `<script>` per
  page. Two small islands at most if a future feature needs reactivity.
- No CMS. Copy lives in `.astro` files alongside the layout — fast
  enough at this surface area, no CMS hosting cost.
