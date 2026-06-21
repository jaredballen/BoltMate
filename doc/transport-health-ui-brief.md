# Transport health UI — design brief

A self-contained brief that can be handed to a design pass without project
context. Describes what's already wired in code, what needs to be added to
the UI, and the copy/state semantics the surface needs to honour.

## What it is

BoltMate runs a cross-machine sync layer over **three independent transports**.
Each can fail on its own (different OS permission, different firewall rule,
different protocol), so each gets its own health signal. The point of
running multiple transports is resilience: one being blocked shouldn't take
the feature down. The point of surfacing them independently is so the user
can fix the right one.

Today the Core layer emits a per-transport `IObservable<TransportHealth>`.
**Nothing in the UI binds to these yet.** This brief is for the binding.

## The three transports

| Transport | Code surface | What "Blocked" actually means |
|---|---|---|
| **UDP broadcast/multicast** | `UdpTopologyService.UdpHealth` | macOS Local Network permission denied, Windows Defender Firewall blocking inbound on UDP 41420, multicast filtered by the network |
| **Bonjour / mDNS** | `MdnsTcpChannel.MdnsHealth` | macOS Local Network denied, Windows "Bonjour Service" not running, multicast UDP 5353 filtered |
| **TCP backchannel** | `MdnsTcpChannel.TcpHealth` | Discovery works but the peer's firewall is blocking inbound TCP on the configured port |

## The shape of each signal

Each observable emits this record:

```csharp
TransportHealth {
  TransportState State,          // Unknown | Healthy | Blocked
  string Endpoint,               // e.g. "239.255.41.42:41420"
  string DetailMessage,          // actionable copy, see below
  DateTimeOffset LastChangeUtc,
}
```

`State` is intentionally three-valued. **Unknown** isn't an error — it's
"we don't have enough data yet" (warmup, no peers discovered yet, etc.).
Render it differently from Blocked.

### Concrete `Endpoint` strings the Core will produce

- UDP: `239.255.41.42:41420`
- mDNS: `_boltmate._udp.local. (mDNS 224.0.0.251:5353)`
- TCP: `TCP backchannel (port 41420)`

### Concrete `DetailMessage` examples

**UDP — Healthy**
`"23/25 recent broadcasts echoed back (92%)"`

**UDP — Blocked**
`"only 1/25 recent broadcasts echoed back. Multicast loopback is being dropped — check Local Network permission (macOS) or firewall inbound rule (Windows)."`

**mDNS — Unknown (warmup)**
`"warming up (8/30s)"`

**mDNS — Blocked**
`"Bonjour service has not echoed our own advert in 75s. On Windows: confirm the 'Bonjour Service' is running. On macOS: confirm Local Network access is granted. If both look right, multicast (224.0.0.251:5353) may be filtered on this network."`

**TCP — Unknown**
`"no Bonjour peers discovered yet"`

**TCP — Blocked**
`"discovered peer(s) via Bonjour but couldn't open TCP port 41420. The peer likely has a firewall inbound rule blocking the port — verify BoltMate is allowed through Windows Defender Firewall / macOS Local Network access on that machine. Last error: Connection refused"`

The detail copy is the user's primary path to a fix. Don't truncate it
behind a tooltip — show it.

## What to add — Settings → Status tab

Add a **Network** section to the existing Status tab. One row per transport,
shown in this order (UDP first, then mDNS, then TCP — it's the order the
signals matter to a confused user):

```
Network
─────────────────────────────────────────────────
●  UDP broadcast              239.255.41.42:41420
   Healthy — 23/25 recent broadcasts echoed back (92%)

●  Bonjour discovery          _boltmate._udp.local.
   Blocked — Bonjour service has not echoed our own
   advert in 75s. On Windows: confirm the "Bonjour
   Service" is running. On macOS: confirm Local
   Network access is granted.

●  TCP backchannel            port 41420
   Unknown — no Bonjour peers discovered yet
```

State indicator (`●`):
- **Healthy** — green dot
- **Unknown** — neutral grey/blue dot
- **Blocked** — red dot

Each row has three parts:
1. **Label** — name of the transport (constant, not from the signal).
2. **Endpoint** — right-aligned, smaller/secondary colour. Comes verbatim
   from `TransportHealth.Endpoint`.
3. **State word + detail message** — wraps under the label. State word
   matches `TransportState`. Detail message wraps freely. Use the full
   `TransportHealth.DetailMessage` verbatim; don't restyle it.

The `LastChangeUtc` field can optionally be rendered as a small "5s ago"
beside the state word for diagnostic feel (`Healthy · 5s ago`), but isn't
required.

### Layout intent

The three rows should look like three peers of the same shape, not three
unrelated panels. The user should be able to glance, see one red dot, jump
to that row's detail. No collapse/expand — the detail is too important to
hide.

## What to add — tray badge

The existing tray icon already has permission-alert badging (see
`TrayIconStatusController`). Extend the composite logic:

- **Add another alert input**: "any transport has been Blocked for ≥ 30s".
- **Threshold matters** — Unknown is transient at startup; we don't want
  a startup flash. 30s of sustained Blocked.
- **Tooltip on the tray icon should name which transport** when the
  network-blocked branch is active:
  - `"BoltMate · network blocked (UDP broadcast)"`
  - `"BoltMate · network blocked (Bonjour)"`
  - `"BoltMate · network blocked (TCP backchannel)"`
  - If multiple, list them comma-separated.

Permission alerts (the existing path) and transport-blocked alerts
should both flip the badge to the same alert state. The tooltip
disambiguates.

## State semantics for the designer

A few non-obvious rules the binding must honour:

- **Unknown ≠ broken.** Two transports can be Unknown at startup while
  the third is already Healthy. Don't render Unknown as alarming. It's
  just "not yet."
- **Independent signals.** Don't roll the three into one summary. The
  whole reason for showing three is that one being healthy doesn't tell
  you anything about the other two.
- **State changes are sparse.** A transport that stays Healthy might
  emit only one event in the BehaviorSubject lifetime. Always render
  the current snapshot — don't wait for an event.
- **Reactive surface.** The signals are `IObservable<TransportHealth>`.
  Subscribe on UI thread (use `ObserveOn(AvaloniaScheduler.Instance)` or
  the existing Dispatcher.Post pattern in `SettingsWindow`). Dispose
  subscriptions on window close.

## Out of scope for this pass

- A separate "Network diagnostics" page. The Status tab is enough for
  the first iteration.
- A manual "Retest now" button. The Core re-derives health on its own
  cadence (UDP: every broadcast tick, mDNS: every 10s, TCP: on
  discovery + every 10s).
- History / sparklines. Just the current state + detail.
