import Stripe from "stripe";

/**
 * Fetches the BoltMate lifetime price from Stripe at build time. Price
 * lookup key (`boltmate_lifetime`) is stable across price changes — if
 * we ever adjust the amount or currency, Stripe's lookup remapping
 * keeps the lookup key pointed at the active Price ID without code
 * changes here.
 *
 * Falls back to a hard-coded default when no STRIPE_SECRET_KEY is
 * present so local dev builds still render. CI must supply the key
 * via a GitHub Actions secret.
 */
export interface PricingInfo {
  /** Formatted amount, e.g. "$14.99". */
  amount: string;
  /** Currency code in upper case, e.g. "USD". */
  currency: string;
  /** Stripe Price ID — passed to checkout. */
  priceId: string;
  /** Stable lookup key for diagnostics. */
  lookupKey: string;
  /** True if served from fallback (no STRIPE_SECRET_KEY in env). */
  fromFallback: boolean;
}

const FALLBACK: PricingInfo = {
  amount: "$14.99",
  currency: "USD",
  priceId: "price_1TmdZyLkIsnQS4tDrUGhDrBg",
  lookupKey: "boltmate_lifetime",
  fromFallback: true,
};

export async function loadPricing(): Promise<PricingInfo> {
  const key = process.env.STRIPE_SECRET_KEY;
  if (!key) {
    console.warn("[pricing] STRIPE_SECRET_KEY not set — using fallback price.");
    return FALLBACK;
  }

  const stripe = new Stripe(key, { apiVersion: "2025-09-30.clover" as Stripe.LatestApiVersion });
  const prices = await stripe.prices.list({
    lookup_keys: ["boltmate_lifetime"],
    active: true,
    limit: 1,
  });

  const price = prices.data[0];
  if (!price || price.unit_amount == null) {
    console.warn("[pricing] No active Price for lookup key boltmate_lifetime — using fallback.");
    return FALLBACK;
  }

  return {
    amount: formatAmount(price.unit_amount, price.currency),
    currency: price.currency.toUpperCase(),
    priceId: price.id,
    lookupKey: "boltmate_lifetime",
    fromFallback: false,
  };
}

function formatAmount(unitAmount: number, currency: string): string {
  const major = unitAmount / 100;
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: currency.toUpperCase(),
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(major);
}
