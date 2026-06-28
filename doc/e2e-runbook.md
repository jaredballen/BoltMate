# End-to-end test environment

A hybrid local + Azure setup that exercises the full BoltMate flow on a
single dev machine: real OAuth sign-in → trial provision → Stripe
purchase → license upgrade → peer crypto handshake → email send. Three
terminals stay running; everything iterates on save.

> ⚠️ Use this once everything is wired and you want to smoke real money
> flows (in test mode). For day-to-day code edits, the site has its own
> `web/README.md` and the .NET projects build standalone.

## What lives where

| Component | Where it runs | Why |
| :--- | :--- | :--- |
| Site (Astro) | `swa-cli start` → http://localhost:4280 | Provides `/.auth/me` + `/api/*` proxy that prod uses. |
| LicenseApi | `func start` → http://localhost:7071 | Hot reload on every save. |
| Stripe webhooks | `stripe listen --forward-to localhost:7071/api/stripe-webhook` | Tunnel — Stripe doesn't reach localhost otherwise. |
| Cosmos DB | Azure free tier in real subscription | Emulator is buggy on macOS arm64; free tier costs $0. |
| KeyVault | Azure real subscription | JWT signing key + verify key live here. |
| Entra External ID | Already provisioned (`boltmateauth.onmicrosoft.com`) | OAuth provider, see `memory/project_b2c_resources.md`. |
| Stripe | Test mode account | Real test cards, no money moves. |
| Resend | Test API key or real Resend account | Sends real emails on the dev mailbox. |
| Desktop app | Standard `dotnet run` against the local LicenseApi | New env var flips its endpoint. |

## One-time setup

Approx. 90 minutes.

### 1. Azure free-tier resources

```sh
# Login.
az login
az account set --subscription "<your subscription id or name>"

# Resource group for test resources.
az group create -n boltmate-dev -l eastus2

# Cosmos serverless account (free tier, no charge under 1k RU/s + 25GB).
az cosmosdb create -n boltmatedev-cosmos -g boltmate-dev \
  --capabilities EnableServerless --default-consistency-level Session
az cosmosdb sql database create -a boltmatedev-cosmos -g boltmate-dev -n boltmate
az cosmosdb sql container create -a boltmatedev-cosmos -g boltmate-dev \
  -d boltmate -n Licenses --partition-key-path "/partitionKey"
az cosmosdb sql container create -a boltmatedev-cosmos -g boltmate-dev \
  -d boltmate -n RefreshLog --partition-key-path "/partitionKey"

# KeyVault for the JWT signing key.
az keyvault create -n boltmatedev-kv -g boltmate-dev -l eastus2 \
  --enable-rbac-authorization true

# Generate the signing key inside KV. RSA-3072, sign only.
az keyvault key create --vault-name boltmatedev-kv -n license-signing-key \
  --kty RSA --size 3072 --ops sign verify

# Grant yourself + the local Function App's executing identity rights.
ME=$(az ad signed-in-user show --query id -o tsv)
SUB=$(az account show --query id -o tsv)
RG_SCOPE="/subscriptions/$SUB/resourceGroups/boltmate-dev"
az role assignment create --assignee "$ME" --role "Key Vault Crypto User" \
  --scope "$RG_SCOPE/providers/Microsoft.KeyVault/vaults/boltmatedev-kv"
az role assignment create --assignee "$ME" --role "Cosmos DB Built-in Data Contributor" \
  --scope "$RG_SCOPE/providers/Microsoft.DocumentDB/databaseAccounts/boltmatedev-cosmos"
```

### 2. Stripe test mode

Stripe Dashboard → toggle **Test mode** on (top-right).

1. Products → New product → "BoltMate Lifetime (Test)".
2. One-time price `$14.99 USD`.
3. **Critical**: set lookup key to `boltmate_lifetime` in advanced settings — `loadPricing()` queries by that, not by ID.
4. Developers → API keys → copy the `sk_test_…` Secret Key.

Stripe CLI for webhook tunnel:

```sh
brew install stripe/stripe-cli/stripe
stripe login   # authorizes the CLI against your test account
```

Webhook signing secret comes from `stripe listen` (see Run section).

### 3. Resend test setup

Either reuse the prod Resend account with a dedicated dev "from" address,
or create a separate test account. Copy a `re_…` key. The `IEmailNotifier`
no-ops when no key is set, so this is optional if you don't need to verify
the actual delivery in your scenario.

### 4. `local.settings.json`

Copy and fill (`src/BoltMate.LicenseApi/local.settings.json` is
gitignored). The placeholder template is checked in; populate with the
real values from steps 1–3:

```jsonc
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "LicenseApi__Issuer": "http://localhost:7071",
    "LicenseApi__KeyVaultUri": "https://boltmatedev-kv.vault.azure.net/",
    "LicenseApi__SigningKeyName": "license-signing-key",
    "LicenseApi__CosmosEndpoint": "https://boltmatedev-cosmos.documents.azure.com:443/",
    "LicenseApi__CosmosDatabase": "boltmate",
    "LicenseApi__EntraTenantId": "da0651cd-08cf-42d3-bf57-e36b3c960aee",
    "LicenseApi__EntraClientId": "fe3bbdfe-a387-4b8f-be0a-03c5b3e9e593",
    "LicenseApi__StripeWebhookSecret": "<from `stripe listen` output>",
    "LicenseApi__StripeSecretKey": "sk_test_<your test secret>",
    "LicenseApi__StripePriceLookupKey": "boltmate_lifetime",
    "LicenseApi__SiteOrigin": "http://localhost:4280",
    "LicenseApi__SupportEmailTo": "<your email>",
    "LicenseApi__ResendApiKey": "<optional re_… key>",
    "LicenseApi__StorageAccountUri": "https://boltmatedev.blob.core.windows.net",
    "LicenseApi__SupportBundlesContainer": "support-bundles",
    "LicenseApi__GitHubRepoOwner": "",
    "LicenseApi__GitHubRepoName": "",
    "LicenseApi__GitHubPat": ""
  }
}
```

`DefaultAzureCredential` in `Program.cs` reads your `az login` token, so
KeyVault + Cosmos calls authenticate as your user account against the
RBAC grants from step 1.

### 5. Tools

```sh
# Azure Functions Core Tools (the `func` CLI).
brew tap azure/functions
brew install azure-functions-core-tools@4

# SWA CLI for the local Easy Auth + /api/* proxy.
npm i -g @azure/static-web-apps-cli
```

## Run

Three terminals stay open. Order matters — start `func` first so the
SWA proxy has a backend to talk to.

### Terminal 1 — LicenseApi

```sh
cd src/BoltMate.LicenseApi
func start
# Functions: http://localhost:7071/api/...
```

Edit `.cs` files; the Functions host reloads automatically.

### Terminal 2 — Stripe webhook tunnel

```sh
stripe listen --forward-to localhost:7071/api/stripe-webhook \
  --events checkout.session.completed,charge.refunded,charge.dispute.created,price.updated,price.created
```

First run prints `whsec_…` — paste that into `LicenseApi__StripeWebhookSecret`
in `local.settings.json` (Terminal 1 picks it up on next request).

### Terminal 3 — Site + Easy Auth proxy

```sh
cd web
cp .env.example .env       # paste your sk_test_… here
swa start http://localhost:4321 --api-location http://localhost:7071 --run "npm run dev"
# Site: http://localhost:4280
```

`swa start` proxies `/api/*` → `func start` and serves a local Easy
Auth emulator at `/.auth/me`. The emulator lets you pick a mock principal
(`http://localhost:4280/.auth/login/aad`) — useful when you want to test
the entitlement loop without a real Entra round-trip.

For an actual Entra sign-in: skip the Easy Auth emulator and hit
`http://localhost:4321` directly (Astro dev), then bypass the missing
`/.auth/me` by hand-providing a Bearer in the browser console. Or
deploy the site to a SWA preview env, where Easy Auth lights up properly.

### Desktop app

```sh
cd src/BoltMate.App
BOLTMATE_LICENSE_BASE_URL=http://localhost:7071 dotnet run
```

`ServiceRegistration.cs` reads that env var and flips both `Issuer` and
`EntitlementEndpoint` to localhost. OAuth still hits the real Entra
tenant (one tenant covers both prod and dev), so the id_token the app
sends to localhost is a genuine signed JWT.

## Scenarios to smoke-test

### A. Fresh sign-in → trial provision → peer crypto

1. Launch the desktop app with the env var set.
2. Walk through the welcome wizard until "Sign in" appears.
3. Sign in via Apple or Google through the real Entra tenant.
4. App POSTs `/api/entitlement` with the Bearer id_token.
5. Watch Terminal 1 — should log "Provisioned Trial lic_…".
6. App's tray icon should land healthy.
7. `Settings → Status → Network` shows the local SyncKey is bound (no
   "Peer crypto unavailable" warning).
8. Launch a second instance pointed at the same env var on a different
   machine on the LAN; confirm they discover each other (UDP) without
   the hostname-advisory tripping (decrypt-success is the trust check).

### B. Stripe purchase → license upgrade

1. From step A you're signed in. Open `http://localhost:4280/checkout` in a browser.
2. Easy Auth emulator: pick the same email you signed into the app with.
3. Page redirects you to Stripe Checkout — pay with `4242 4242 4242 4242`.
4. Stripe sends `checkout.session.completed` → Stripe CLI forwards it → Terminal 1 logs "Upgraded lic_… → Boltmate".
5. Resend logs the purchase-confirmation email (or `IEmailNotifier`
   no-ops it with a warning if no key).
6. Desktop app forces a refresh (Settings → About → "Check for updates"
   or wait for the next 1-hour cycle). Tier flips to Boltmate.

### C. Refund → license revoke

1. Stripe Dashboard → Payments → refund the test charge.
2. `charge.refunded` lands at Terminal 1. License row status flips to "revoked".
3. Desktop app's next entitlement refresh returns 403; tray badge goes amber.

### D. GDPR delete

1. `http://localhost:4280/account` → Danger zone → Delete account.
2. `DELETE /api/entitlement` returns 204.
3. Cosmos Licenses partition is empty; RefreshLog partition is empty.
4. Re-signing-in from the same hardware in the next 12 months → the
   trial-reuse block kicks (`TrialReused` error). Expected behaviour.

### E. Price refresh → site rebuild dispatch

1. Stripe Dashboard → edit the `boltmate_lifetime` price (e.g. drop a
   cent for the test).
2. `price.updated` lands at Terminal 1 → `IGitHubDispatcher` would call
   `repository_dispatch` on the BoltMate repo if `GitHubPat` is set.
3. For e2e dev: leave the PAT empty — Terminal 1 just logs "GitHub
   dispatcher not configured; skipping" and you confirm the filter +
   payload built correctly in the log line above it.

### F. Trial reminder timer

The daily TimerTrigger at `0 0 14 * * *` doesn't fit a same-day smoke
test. Two options:

- Use `func start`'s admin endpoint to force a manual invocation:
  ```sh
  curl -X POST http://localhost:7071/admin/functions/TrialReminderTimer \
    -H "content-type: application/json" -d '{}'
  ```
- Or seed a Cosmos row with `tier="Trial"`, `status="active"`,
  `expiresAt=today+1d`, and run the manual invocation above. Confirm the
  T-1 email lands and the `trialNotifiedT1` flag flips.

## Common gotchas

- **DefaultAzureCredential picks the wrong identity.** If you have
  multiple `az login` accounts, force the right one:
  `az account set --subscription <sub>` + `AZURE_TENANT_ID=<tenant>
  func start`.
- **Cosmos free tier 429s under churn.** Serverless caps at burst; if
  you hammer it with the SDK's auto-retry off, you'll get
  `TooManyRequestsException`. Wait 30 seconds.
- **Stripe CLI session times out.** It silently stops forwarding after
  ~90 min of idle. Re-run `stripe listen` if a webhook stops landing.
- **SWA CLI 4280 vs Astro 4321.** Always hit `4280` — that's the one
  with the Easy Auth + `/api/*` proxy. `4321` is bare Astro.
- **Browser cookies stale on tenant flips.** If you change Entra tenant
  IDs, clear cookies for `boltmateauth.ciamlogin.com` — Easy Auth will
  otherwise present the previous session.

## Teardown

```sh
az group delete -n boltmate-dev --yes  # nukes Cosmos + KV + everything
stripe logout                          # revokes the CLI session
# Stripe webhook endpoint in Dashboard → Delete
```

## Cost ceiling

Realistically $0/month at smoke-test volume:

- Cosmos serverless: free tier covers 1k RU/s + 25 GB.
- KeyVault: $0.03/10k operations — under the noise floor.
- Functions Consumption Tier: 1M executions/mo free.
- Stripe test mode: $0.
- Resend: 100 emails/day free.

If you forget to `az group delete` and leave it running for a year,
expect single-digit dollars total. Set a billing alert at $5 if paranoid.
