# ADR-001: How PodGate blocks the AirPods

- Status: accepted
- Date: 2026-09-15

## Context

Stock Windows connects paired AirPods by itself at boot, often before logon, and the AirPods leave the phone they were playing from. Windows has no per-device "don't connect automatically" option. The block has to survive restart, shutdown with Fast Startup, and sleep, and must not affect other Bluetooth devices (a classic headset, an LE mouse).

Observations that shaped the decision:

1. **The link forms below the profile layer.** With the audio profiles (A2DP sink, Hands-Free) disabled, a restart still produced a connected device with zero audio endpoints, and the phone still lost the AirPods. Blocking audio profiles alone is not enough.
2. **Disabling all of the device's Bluetooth services does prevent it**, but blocking takes around 20 s, unblocking around 6 s plus another 5-10 s before audio endpoints appear, endpoints are destroyed and recreated with new IDs on every cycle (so Windows never remembers the AirPods as the default output), and re-enabling a profile does not start audio if the link is already up.
3. **Disabling the device's root Bluetooth device node prevents it too**, and far more cheaply.

## Decision

Block by disabling the AirPods' root Bluetooth device node `BTHENUM\DEV_<address>\...`; connect by enabling it again. The disabled flag (`CONFIGFLAG_DISABLED` in the node's ConfigFlags) persists across reboots, which is what "blocked at rest" needs. The Bluetooth services stay at their stock, enabled state.

## Measurements

| | All services disabled | **Device node disabled** |
|---|---|---|
| Block | ~19 s | **< 1 s** (tens of ms via SetupDi) |
| Connect to usable audio | ~6 s + 5-10 s for endpoints | **~0.6 s** after an in-session disconnect, 2-5 s after boot |
| Audio endpoints across a cycle | deleted, new IDs each time | **kept, same IDs** |
| Windows remembers the default output | no | **yes** |
| Services touched | all | none |
| No grab after | restart, shutdown | restart, shutdown with Fast Startup, sleep (Modern Standby) |

## Alternatives rejected

| Method | Why not |
|---|---|
| Disable only the audio services | The link still forms and the phone still loses the AirPods |
| Disable all services | Works, but slow, and it breaks endpoint identity and default-device memory |
| Disable the Bluetooth enumerator (`BthEnum`) | Blocks every classic Bluetooth device, not just the AirPods |
| Edit raw `BTHPORT` registry flags | Undocumented registry surgery, not needed |
| Turn the radio off at rest | Cuts Bluetooth mice and keyboards too |
| Unpair, or delete and restore pairing records | Requires re-pairing, link keys live in a SYSTEM-only key, and it is not safely reversible |

## Consequences

- While blocked, the AirPods show in Device Manager with a "disabled" icon and Windows' Bluetooth settings offer no Connect button. Connecting goes through PodGate.
- The block needs elevation, so it lives in the service (ADR-002).
- A Windows or driver update could re-enable the node; the service re-asserts the block when it starts.
- After a boot with the node disabled, enabling it only clears the flag in some cases and leaves the live node disabled; unblock checks the live state and starts the node explicitly (see `docs/windows-notes.md`).
- Rollback: uninstall PodGate, or `scripts/restore-bt.ps1 -Apply`, or `Enable-PnpDevice -InstanceId <root node>`.
