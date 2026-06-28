# Stripe live mode swap runbook

Promote BoltMate from Stripe test mode to live mode. Run as the very
last step before public launch — every step is reversible, but a
half-swapped state can drop real money on the floor or silently dispatch
to the test webhook handler.

## 0. Before you start

- Confirm Stripe account is in "Activated" state (Stripe Dashboard → Settings → Business settings → Account status).
- Confirm payout method is connected (bank account or debit card).
- Confirm tax + business address are entered (Settings → Tax + Business).
- Confirm the `support@boltmate.app` Resend domain is verified and
  capable of sending (we send a purchase-confirmation email from the
  Stripe webhook; you don't want that bouncing on launch day).
- Have the Azure App Configuration blade open for the LicenseApi resource group.

## 1. Create the live-mode Price

1. Stripe Dashboard → toggle from "Test mode" to "Live mode".
2. Products → New product → "BoltMate Lifetime".
3. Add a one-time price of $14.99 USD.
4. **Critical**: under the price's advanced settings, set the **lookup key** to `boltmate_lifetime`. This is what `web/src/lib/pricing.ts` queries against; without it, the site build will fall back to the hardcoded price string.
5. Note the live Price ID (`price_…`) — useful for sanity-checking the dispatched webhook payloads.

## 2. Mint a Restricted Key

A full secret key works but reads broader than necessary. Use a
Restricted Key with only what BoltMate needs.

1. Stripe Dashboard (live mode) → Developers → API keys → "Create restricted key".
2. Name: `boltmate-licenseapi-live`.
3. Permissions:
   - **Prices**: Read (for the site build's `loadPricing()`)
   - **Checkout Sessions**: Write (for `CheckoutSessionFunction`)
   - **Customers**: Read + Write (the Checkout Session creates a Customer if absent)
   - **PaymentIntents**: Read (the refund webhook reverse-looks-up a license by PI ID)
   - Everything else: None
4. Copy the `rk_live_…` key out — Stripe shows it once.

## 3. Set up the live webhook endpoint

1. Stripe Dashboard (live mode) → Developers → Webhooks → "Add endpoint".
2. Endpoint URL: `https://api.boltmate.app/api/stripe-webhook`.
3. Events to send:
   - `checkout.session.completed`
   - `charge.refunded`
   - `charge.dispute.created`
   - `price.updated`
   - `price.created`
4. Save. Copy the new signing secret (`whsec_…`) — Stripe shows it once.

## 4. Swap secrets in Azure App Configuration

1. Azure Portal → App Configuration (the one the LicenseApi reads from) → Configuration explorer.
2. Update these keys:
   - `LicenseApi:StripeSecretKey` → the `rk_live_…` value from step 2
   - `LicenseApi:StripeWebhookSecret` → the `whsec_…` value from step 3
3. Save. The next Function invocation will pick up the new values (cold start cycle, no manual restart needed).

## 5. Sanity check

1. `curl https://api.boltmate.app/api/healthz` (or hit `/api/entitlement` with a known good token) — confirm the Function App restarted cleanly.
2. Trigger a `price.updated` event from the Stripe Dashboard ("Send test webhook"). Confirm:
   - The webhook delivery shows 2xx in the Stripe webhook log.
   - GitHub Actions shows a fresh `swa-deploy` run kicked off by `repository_dispatch`.
3. Place a real $14.99 purchase from a test account. Confirm:
   - `/api/checkout-session` returns a `https://checkout.stripe.com/...` URL.
   - On success redirect, `/account` shows the Boltmate tier.
   - Resend logs the `PurchaseConfirmationAsync` email.
4. Issue a refund from Stripe Dashboard for the test purchase. Confirm:
   - `/account` reflects the revoked state after the next entitlement refresh.

## 6. Rollback plan

If anything misbehaves, revert in this order:

1. Re-add the old test signing secret + secret key in App Configuration. Save. (LicenseApi flips back instantly.)
2. Toggle the live webhook endpoint to "Disabled" in Stripe.
3. Stripe will pause delivery; no in-flight money is at risk because no real cards have hit live mode yet (assuming step 5 hadn't completed).

## 7. Done

Once step 5 verifies, this runbook is in done state. Delete the test
mode webhook endpoint to avoid confusion in the dashboard. Keep the test
mode secret key around in a 1Password vault for staging environments.

## Related

- `doc/web-portal-plan.md` — phased rollout, Phase 0 deferred this swap
- `doc/licensing_architecture.md` — section 4 lists this as the last
  pre-launch risk
- `src/BoltMate.LicenseApi/Services/StripeWebhookHandler.cs` — every
  event handler the live endpoint will hit
- `web/src/lib/pricing.ts` — the build-time Stripe call the lookup key
  unlocks
