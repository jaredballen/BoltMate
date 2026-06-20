# LogiPlusSwitcher — Network behaviour

This document covers, in full detail, every network operation
LogiPlusSwitcher performs. Power users can use it to write firewall rules;
sceptical users can use it to verify the app isn't doing anything nefarious.

**TL;DR**:
- All network activity is LAN-only and opt-in.
- We broadcast a small JSON message every 2 seconds describing which
  Bolt receivers + Logitech devices are attached to this machine.
- We listen for the same message from peers on the same LAN.
- We never transmit your keystrokes, clipboard, file paths, browsing
  history, or anything from your other applications.
- Every network feature can be disabled in Settings → Network.
- The full source for this is at
  [`LogiPlusSwitcher.Core/Topology/`](../LogiPlusSwitcher/LogiPlusSwitcher.Core/Topology/).

---

## Why does this app talk on the network at all?

The Bolt receiver's hardware Easy-Switch button cannot be detected by software
(see [`project_easyswitch_firmware_direct.md`](../.claude/projects/-Users-jallen-Workspace-jaredballen-LogiPlusXSwitcher/memory/project_easyswitch_firmware_direct.md)
in the source repo for the empirical proof). That means: when you press the
keyboard's Easy-Switch button to move from Mac to your Win VM, the **Mac
side never gets a "device went to host N" event** — it only sees that the
keyboard disappeared.

To make the rest of your devices follow that switch (a mouse, headset,
etc.), the Mac-side app needs to know "the keyboard just appeared on the
Win VM". The only way to know that without proprietary 2.4GHz radio access
(which we cannot have) is for the Win VM's instance of LogiPlusSwitcher to
**announce on the LAN** that a device with WPID 0xB378 just came online
there. The Mac-side instance hears that announcement and can then move the
remaining devices.

That's the entire network story. **No other feature uses the network.**

---

## Configuration knobs

All persisted in your `settings.json` (path varies by OS — see
[`AppPaths.cs`](../LogiPlusSwitcher/LogiPlusSwitcher.Core/AppPaths.cs)):

| Field | Default | What it controls |
|---|---|---|
| `Topology.Enabled` | `false` | Master switch. Off = zero network activity. |
| `Topology.Port` | `41420` | UDP port (chosen to be high and uncommon). |
| `Topology.BroadcastIntervalSeconds` | `2` | Normal cadence. |
| `Topology.BurstIntervalMs` | `200` | Cadence during a 3s window after any local device's link drops. |
| `Topology.BurstDurationMs` | `3000` | Length of the post-link-lost burst window. |
| `Topology.RepeatCount` | `3` | How many times each announcement is sent back-to-back (cheap defence against single dropped UDP packets — peers dedup by sequence id). |
| `Topology.RepeatGapMs` | `25` | Spacing between the N× repeats. |
| `Topology.UseMulticast` | `true` | Send to multicast group as well as LAN broadcast. |
| `Topology.MulticastGroup` | `239.255.41.42` | The admin-scoped multicast group (the `239.x.x.x` block does not leave the local network). |
| `Topology.MachineId` | (UUID generated on first enable) | Stable identifier for this machine in announcements. |
| `Topology.CorrelationWindowSeconds` | `3` | After a local device's link drops, how long the correlator listens for peer announcements that match. |

If `Topology.Enabled = false`, **the UDP socket is never bound** and zero
packets enter or leave the machine for any of these features.

---

## Wire format

### 1. UDP broadcast + multicast (current default channel)

Outbound destinations every `BroadcastIntervalSeconds`:
- IPv4 broadcast address of every active, non-loopback, non-tunnel
  interface (computed per-interface as `addr | ~mask`).
- The configured multicast group (default `239.255.41.42:41420`).

Each announcement is sent `RepeatCount` times back-to-back with `RepeatGapMs`
spacing. All repeats carry the same `Seq`; peers dedup by `(MachineId, Seq)`.

#### Announcement payload (JSON, UTF-8)

```json
{
  "V": 1,
  "MachineId": "5a7b32814...",
  "Hostname": "macbook-pro",
  "Timestamp": "2026-06-20T14:33:12.4470000+00:00",
  "Seq": 1234,
  "Receivers": [
    {
      "Serial": "CEB26A85",
      "BluetoothAddressHex": "011818000000",
      "OnlineDevices": [
        { "Slot": 1, "WpidHex": "B378", "Name": "MX Keys S" },
        { "Slot": 2, "WpidHex": "B034", "Name": "MX Master 3S" }
      ]
    }
  ]
}
```

**Every field is observable in the source:** see
[`ReceiverAnnouncement.cs`](../LogiPlusSwitcher/LogiPlusSwitcher.Core/Topology/ReceiverAnnouncement.cs).

#### What this payload reveals about you

- Your operating system's hostname (the value
  [`Dns.GetHostName()`](https://learn.microsoft.com/dotnet/api/system.net.dns.gethostname)
  returns).
- The serial numbers of any Bolt receivers physically connected.
- A 6-byte receiver-side identifier per device pairing (this is **not** a
  real Bluetooth MAC — Bolt uses Logitech's proprietary 2.4GHz protocol,
  not standards BLE).
- The wireless product IDs (WPID) of Logitech devices currently online —
  e.g. `B378` = MX Keys S, `B034` = MX Master 3S For Mac. These identify
  the model of device, not anything personal.
- The product names as stored on the device itself.

#### What this payload does NOT contain

- **No keystrokes or input data of any kind.**
- **No clipboard contents.**
- **No file paths, application names, window titles, or browsing data.**
- **No personal identifiers beyond the hostname** (which you control).
- **No telemetry, analytics, or anything sent off-LAN** — multicast group
  `239.x.x.x` is administratively scoped to the local network by IETF spec
  and is not routable to the public internet.

### 2. Inbound

Same UDP socket, same port. We accept any IPv4 packet on `Port` whose
content parses as the schema above. Anything else is silently dropped. The
service is bound to `0.0.0.0:41420` so it receives both broadcast and
multicast traffic destined to that port.

---

## Firewall rules

### Windows Defender Firewall

```powershell
# Allow inbound (so we hear peers)
New-NetFirewallRule -DisplayName "LogiPlusSwitcher topology in" `
    -Direction Inbound -Protocol UDP -LocalPort 41420 `
    -Action Allow -Profile Private

# Allow outbound (so we send announcements)
New-NetFirewallRule -DisplayName "LogiPlusSwitcher topology out" `
    -Direction Outbound -Protocol UDP -RemotePort 41420 `
    -Action Allow -Profile Private
```

Restrict to `Private` profile only — we do not want this rule active on a
public coffee-shop network. If your VM is on a `Public` network profile,
either change the profile or omit `-Profile Private`.

### macOS

macOS firewall (`Settings → Network → Firewall`) by default allows
outbound traffic and prompts on first inbound. When LogiPlusSwitcher first
listens on port 41420, you'll get a one-time prompt to allow incoming
connections. Click **Allow**.

Granular control via `pf` (advanced):
```
# /etc/pf.conf — allow topology only on en0
pass in on en0 inet proto udp from any to any port 41420 keep state
```

### pfSense / OPNsense / OpenWrt

Allow UDP `41420` on the LAN interface, both directions, in the `LAN` →
`LAN` chain. Do **not** allow it on the WAN interface — there is no reason
for these packets to leave your network, and a sane gateway will already
drop them.

---

## How to verify what we're actually sending

LogiPlusSwitcher writes a log file at:
- macOS: `~/Library/Logs/LogiPlusSwitcher/logiplus-app-YYYYMMDD.log`
- Windows: `%LOCALAPPDATA%\LogiPlusSwitcher\Logs\logiplus-app-YYYYMMDD.log`

Look for lines tagged `LogiPlusSwitcher.Core.Topology.UdpTopologyService` —
they show every send attempt and every received announcement.

To see the actual wire data with a packet capture tool:
```bash
# macOS / Linux — Wireshark filter:
udp.port == 41420

# Windows — pktmon:
pktmon filter add -p 41420 -t UDP
pktmon start --etw
# (reproduce activity, then `pktmon stop`, then `pktmon etl2pcap …`)
```

The captured payloads are plain JSON; you can read them byte for byte.

---

## How to fully disable

Open `Settings → Network` and uncheck "Enable cross-machine sync". The UDP
socket is released immediately; no further network activity occurs.

If you want to be doubly sure: quit the app entirely, edit your
`settings.json`, and set `Topology.Enabled` to `false`. (It's already
`false` by default — opt-in.)

---

## Threat model — what could go wrong

- **A malicious peer on your LAN spoofs an announcement** claiming a device
  is on their machine when it isn't. Worst case: your devices try to
  CHANGE_HOST to a slot they aren't paired to. Bolt firmware silently
  ignores the write. Effect: nothing happens. **No code-execution path,
  no information disclosure.**
- **An eavesdropper on your LAN captures the announcements.** They learn:
  your hostname, what Logitech device models you own, and how many Bolt
  receivers you have. They do NOT learn anything about what you type,
  click, or do.
- **A misconfigured router forwards your multicast to a wider network.**
  The `239.x.x.x` block is admin-scoped by IETF design and no compliant
  router will do this; if yours does, it's broken — but the only data
  leaked is the same hostname / device-model info above.

---

## Roadmap — additional channels (not yet shipped)

- **mDNS / Bonjour discovery + TCP delivery**: peers register a
  `_logiplus._udp.local` service so discovery works even when broadcast is
  filtered. Actual messages go over a TCP connection, which gives explicit
  delivery confirmation. mDNS uses UDP 5353 on the link-local multicast
  group `224.0.0.251`.
- **Per-channel health diagnostics**: the in-app diagnostics panel will
  show which transport is delivering, packet loss per peer, and which
  channels are being filtered by the local network.

When these ship, this document will be updated to reflect the new ports
and addresses they use, **before** they become the default.
