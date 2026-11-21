# DoorNet — Architecture

DoorNet is a Windows network activity monitor: live throughput, per-application usage,
and a readable view of every connection on the machine. This document is the working
plan; it will change as the product does.

> `reverse.eng/` holds the GlassWire installer and a Ghidra export of its **NSIS installer
> stub**, not its application code. It is kept as a behavioural reference only. DoorNet is
> an independent implementation: no GlassWire binaries, assets, branding, or decompiled
> logic are used, and the UI is our own design.

---

## 1. Scope

### v1 (shippable)
| Capability | Detail |
|---|---|
| Live throughput | Aggregate up/down per second across active adapters, 1 s cadence |
| Session totals | Bytes in/out since launch, resettable |
| Connection inventory | TCP v4/v6 + UDP, owning PID, process name, local/remote endpoint, TCP state |
| Application list | Connections grouped per process, active vs. listening, remote host count |
| Tray presence | Minimise to tray, tooltip with current speed, pause/resume capture |
| History | Per-adapter and per-app samples persisted (SQLite), 24 h retention |

### v2
- Per-application byte usage via an **elevated ETW collector** (see §3).
- Alerts: new application talking to the internet, first contact with a remote host,
  data-usage threshold per app/day.
- Host resolution + reputation lookup for remote endpoints.
- Usage timeline (hour/day/week), export to CSV.
- Optional Windows Firewall rule management (block/allow per app).

### Non-goals
Packet capture / DPI, VPN functionality, cross-platform, kernel drivers.

---

## 2. Data sources (no kernel driver required)

| Need | API | Admin? | Notes |
|---|---|---|---|
| Total bytes up/down | `NetworkInterface.GetIPStatistics()` | No | Sum of IPv4+IPv6 counters on `Up`, non-loopback, non-tunnel adapters |
| Connection list | `GetExtendedTcpTable` / `GetExtendedUdpTable` (`iphlpapi.dll`) | No | Returns owning PID per row, v4 and v6 |
| PID → process | `Process.GetProcessById` | No | `MainModule.FileName` can throw for protected processes; degrade to name |
| **Per-app bytes** | **ETW** `Microsoft-Windows-Kernel-Network` | **Yes** | Real-time session, per-event PID + byte count. This is what GlassWire does |
| Interface metadata | `NetworkInterface`, `NetworkInterfaceType` | No | Friendly adapter name, link speed, status |
| Internet blocking | `HNetCfg.FwPolicy2` COM (Windows Defender Firewall rules) | **Yes** | Inbound + outbound block rules per executable, grouped `DoorNet` |

Important: Windows exposes **no unprivileged per-process byte counters**. A given design
must choose between "no admin, aggregate only" and "admin, per-app". DoorNet's answer is
to run unprivileged by default and light up per-app usage when the user opts into the
elevated collector.

Also: interface counters include loopback and, on some drivers, VPN/tunnel overhead.
v1 filters adapters by type and status; v2 adds per-adapter selection in Settings.

---

## 3. Elevation model

```
┌─────────────────────────────┐      named pipe      ┌──────────────────────────────┐
│ DoorNet.App (WPF)           │ ◄──── JSON lines ───► │ DoorNet.Service (SYSTEM)     │
│  - UI, tray, history view   │                       │  - ETW Kernel-Network session│
│  - aggregate counters       │                       │  - per-app byte rollups      │
└─────────────────────────────┘                       └──────────────────────────────┘
                                                                    ▲
                                                          SQLite (ProgramData)
```

- **Current state:** one process. When launched elevated, it opens the kernel ETW session
  itself (`EtwPerAppUsageSource`); unelevated, the source reports why it is unavailable and
  the header offers **Restart elevated** (UAC relaunch). This is the fastest path to a
  working feature and is fine for a single-user desktop app.
- **Kernel provider gotcha:** `TraceEventSession.EnableKernelProvider` returns `false` on
  the *normal* success path for a `NT Kernel Logger` session (the bool is only `true` when
  it restarts an existing session). Check `session.IsActive` instead — this cost us an hour.
- **Loopback:** kernel network events include `127.0.0.1` / `::1` transfers. They are
  filtered in `AddressScope.IsLocalOnly` because adapter counters never include them.
- **Known limit:** `NT Kernel Logger` is a single system-wide session. The collector creates
  with `Create | NoRestartOnCreate` and pre-checks active sessions, so a conflict produces
  a message rather than silently stopping Process Monitor / GlassWire / WPR.
- **Milestone 3 (service):** move the ETW session into `DoorNet.Service` so per-app usage
  works without the whole UI running elevated, survives logoff, and stays the only writer
  of rollups. The UI subscribes over a named pipe; messages are length-prefixed and
  validated so a compromised UI cannot make a SYSTEM process do arbitrary work.
- **Never** ship a manifest with `requireAdministrator`: it kills auto-start with Windows
  and breaks drag/drop, tray interaction and updates. Elevation is opt-in per feature.

---

## 3b. Firewall model

Blocking uses Windows Defender Firewall rules, not a filter driver.

| Decision | Why |
|---|---|
| One **inbound and one outbound** block rule per executable, `Grouping = "DoorNet"` | Explicit blocks beat any allow rule; blocking only outbound still left inbound flows and established replies moving |
| Rule name `DoorNet block <file> #<sha256(path)[..8]>` | Deterministic, unique per path, so we can create/remove by name without enumerating every rule |
| State file `%LocalAppData%\DoorNet\firewall.json` | UI can mark blocked apps before touching COM; validated against live rules when elevated |
| No driver | GlassWire ships `gwdrv.sys` to enforce its own firewall; DoorNet intentionally stays user-mode |

Limitations, all surfaced in the UI: elevation is required; rules target an executable, not
an app family; the rule is inert if another product disables Windows Firewall; protected
processes (PPL/EDR) expose no path and the menu action is disabled rather than guessed.

Verified on this machine: blocking `curl.exe` made connections fail (`curl` exit 7) with the
rule present, and allowing it removed the rule and restored connectivity.

---

## 4. Project layout

```
DoorNet.slnx
src/
  DoorNet.Core/        net10.0          models, interfaces, sampling math, formatting, firewall contracts
  DoorNet.Capture/     net10.0-windows  Windows API, ETW and firewall implementations of Core interfaces
  DoorNet.App/         net10.0-windows  WPF shell (views, view-models, custom chart, palettes)
docs/ARCHITECTURE.md
```

Rules:
- `Core` has no Windows dependencies and no UI references — it is unit-testable.
- `Capture` depends on `Core`; it is the only place P/Invoke and ETW live.
- `App` depends on both and never calls `iphlpapi` or `Process` directly for monitoring.
- Everything the UI draws comes from an interface in `Core`, so the ETW service can be
  swapped in later without touching views.

---

## 5. Sampling model

- One 1 s `DispatcherTimer` drives all capture. No per-widget timers.
- Counters are **monotonic totals**; throughput is `Δbytes / Δt`. A negative delta means
  the adapter reset (sleep/resume, driver reload) and is dropped, not plotted.
- A 1 s grid can miss short bursts and over-reports on a 1.4 s tick. Rates are smoothed
  with an exponential moving average (α ≈ 0.35) and the chart keeps its own 90-sample
  ring buffer so one slow tick cannot distort the curve.
- Connection snapshots are cheap but not free; they refresh on the same tick, and each
  snapshot is diffed into "new / closed" events for v2 alerts.

---

## 6. UI direction

Dark, dense, instrument-panel feel — closer to Task Manager's Performance tab than to a
consumer dashboard. No GlassWire visual language (its palette, iconography, layout and
branding are not reused).

- Header: product mark, adapter/status pill, capture on/off.
- Three stat tiles: download rate, upload rate, session total. Large tabular numerals.
- Main chart: 90 s dual-series area chart, auto-scaled, drawn natively (no chart library).
- Split pane: top applications (left) and live connections (right, sortable grid).
- Restrained palette: `#0E1217` canvas, `#151B22` cards, `#35E08E` download,
  `#4C9BFF` upload, `#7C8A99` secondary text.

Typography: Segoe UI Variable; numeric columns use tabular figures so digits don't jitter.

**Theming:** every colour is a semantic brush in `Styles/Palette.Dark.xaml` /
`Styles/Palette.Light.xaml`; no view contains a literal hex value. `ThemeManager` recolours
the live brush instances in place, so the window switches without rebuilding the visual
tree, and the choice is stored in `%LocalAppData%\DoorNet\settings.json`. On Windows 10 the
native caption keeps following the system theme (caption colour APIs are Windows 11-only).

---

## 7. Milestones

| # | Deliverable | Exit criteria |
|---|---|---|
| **1** | **Skeleton (this session)** | Window shows real live throughput, real connections, real per-app connection counts; builds with `dotnet build`; runs unelevated |
| 2 | Persistence | SQLite samples + rollups survive restart; 24 h chart |
| 3 | ETW per-app bytes | **done in-process** (kernel session, loopback filtered, conflict-safe). Service host pending |
| 3b | Firewall | outbound block/allow per executable via Windows Firewall rules, verified |
| 4 | Tray + autostart + alerts | Tray tooltip, start with Windows, first-contact notifications |
| 5 | Packaging | Signed installer, auto-update, crash reporting |

---

## 8. Packaging & distribution

- **Installer:** Inno Setup or MSIX. MSIX if we want Microsoft Store/App Installer
  auto-update; Inno if we want a classic per-machine install plus a Windows service
  (needed at milestone 3). Current lean: **Inno Setup**, because the service story and
  per-machine ProgramData store are simpler.
- **Signing:** Authenticode certificate (OV minimum; EV avoids SmartScreen reputation
  warm-up). Sign both the app exe and the installer.
- **Updates:** separate updater exe that verifies a signed manifest; never self-replace a
  running image.
- **Data:** `%ProgramData%\DoorNet\` for service-owned history, `%LocalAppData%\DoorNet\`
  for UI preferences.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| SmartScreen flags unsigned builds | Sign early; test on a clean VM |
| Anti-cheat / EDR dislikes ETW sessions | Only create the kernel-network session, stop it on exit, document it in the UI |
| Per-app attribution wrong after PID reuse | Key rollups by `(PID, start time)` where available |
| Counter overflow on long sessions | Use `ulong` totals, persist deltas not raw counters |
| UI stalls if a process query hangs | All capture on a background `Task`, marshal results to the UI thread; per-call timeout |
| Cloning a commercial product | Independent code, independent design, no extracted assets; keep `reverse.eng/` out of the repo |

---

## 10. Testing

- `DoorNet.Core`: unit tests for rate math, EMA, ring buffer, nice-axis scaling, byte
  formatting (including 0 B/s and counter resets).
- `DoorNet.Capture`: integration tests that assert the connection table parses a known
  fixture, plus a smoke test that the sampler returns non-negative rates.
- Manual matrix: Ethernet + Wi-Fi + VPN, sleep/resume, adapter disable, 15-minute soak.

---

## 11. Open decisions

1. Per-app bytes at milestone 3: Windows service vs. on-demand elevated helper.
   *Leaning service* — survives logoff/restart, single ETW session, simpler rollups.
2. History store: SQLite (easy, queryable) vs. flat binary ring file (fast, no dependency).
   *Leaning SQLite*, batched writes every 10 s.
3. Distribution: Inno (classic, service-friendly) vs. MSIX (Store, auto-update).
   *Leaning Inno.*
