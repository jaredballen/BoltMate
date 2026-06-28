# BoltMate site — `web/`

The marketing + account site at [`boltmate.app`](https://boltmate.app).
Static Astro build, deployed to Azure Static Web Apps Free. Live
architecture in [`doc/site-architecture.md`](../doc/site-architecture.md).

## Run it locally

Prerequisites: **Node 22 LTS**. nvm works:

```sh
nvm install 22 && nvm use 22
```

Then from inside `web/`:

```sh
cp .env.example .env       # paste a Stripe test key in here
npm ci
npm run dev                # http://localhost:4321 with hot reload
```

That's it — every page renders against the dev server with hot reload
on `.astro` / `.ts` / `.css` saves. No backend dependency for the static
pages; the `/account` page assumes you're signed in via SWA Easy Auth
and stays in its "Signed out" state when running locally.

## Environment variables

| Key | Required? | Notes |
| :--- | :--- | :--- |
| `STRIPE_SECRET_KEY` | Optional for dev, required for prod | `sk_test_…` in sandbox, `rk_live_…` in production (see [Stripe live-mode runbook](../doc/stripe-live-mode-runbook.md)). Absent → `loadPricing()` falls back to a hardcoded $14.99 default and logs a warning. |

The build never reaches Stripe at runtime — `loadPricing()` is called
once at build time, results bake into the static HTML.

## Sandbox Stripe setup

The dev env should point at **test mode** so accidental Checkout opens
don't hit a real card.

1. Stripe Dashboard → make sure the **Test mode** toggle in the top-right is on.
2. Products → New product → "BoltMate Lifetime (Test)".
3. Add a one-time price of `$14.99 USD`.
4. Open the price's advanced settings → set **lookup key** to
   `boltmate_lifetime`. **Critical** — `loadPricing()` queries by lookup
   key, not by price ID, so the test mode and live mode Prices share the
   same key and the same code path. Without it, the dev build silently
   falls back to the hardcoded default.
5. Developers → API keys → copy your `sk_test_…` Secret Key into
   `web/.env`'s `STRIPE_SECRET_KEY` slot.
6. Confirm with `npm run build` — the console should NOT print
   `[pricing] STRIPE_SECRET_KEY not set` or `No active Price for
   lookup key boltmate_lifetime`.

If you flip `loadPricing()` to a live key by accident, the fix is the
same as the launch-day swap in reverse — replace `STRIPE_SECRET_KEY`
with the test value, re-run `npm run build`. The fallback log line is
how you'd notice; the rendered amount stays $14.99 either way because
the live + test mode Prices match.

## Build

```sh
npm run build              # outputs dist/
npm run preview            # serves dist/ at http://localhost:4321
```

`dist/` is what Azure Static Web Apps publishes. The CI build runs from
`.github/workflows/swa-deploy.yml` — it injects `STRIPE_SECRET_KEY` from
a GitHub Actions secret, so production renders the live amount even if
the secret rotates underneath.

## Structure

```
web/
├── .env.example          # template — copy to .env, never commit .env
├── astro.config.mjs      # output:'static', no SSR
├── package.json
├── staticwebapp.config.json
├── public/               # favicon, og-image, robots.txt
└── src/
    ├── layouts/Base.astro    # nav + footer + CSS variables
    ├── lib/
    │   ├── pricing.ts        # build-time Stripe Price fetch
    │   └── auth.ts           # /.auth/me + id_token extraction (used by /account)
    ├── pages/                # one .astro per route
    └── styles/
```

See [`doc/site-architecture.md`](../doc/site-architecture.md) for the
deeper architecture writeup (hosting + DNS, auth flow, Stripe wiring,
the site → LicenseApi contract).

## Common tasks

| Task | Command |
| :--- | :--- |
| Run dev server | `npm run dev` |
| Type-check + build | `npm run build` |
| Preview prod build | `npm run preview` |
| Update Astro / deps | `npm i astro@latest` then `npm ci` |

## Deploying

Don't. Pushes to `main` that touch `web/**` auto-deploy via
`.github/workflows/swa-deploy.yml`. PRs against `main` get a staging
preview environment automatically. The only manual deploy lever is
`repository_dispatch` of `stripe-price-updated`, which `StripeWebhookHandler`
fires automatically on a Stripe Price change.
