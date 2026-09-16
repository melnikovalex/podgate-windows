# ADR-002: Split PodGate by session, not just by privilege

- Status: accepted
- Date: 2026-09-16

## Context

Blocking and unblocking (ADR-001) needs administrator rights. Connecting also needs work that belongs to the logged-on user. A first design ran the privileged part as scheduled tasks:

- Tasks running in the user's interactive session flashed a console window on every hotkey press.
- Tasks set to "run whether the user is logged on or not" (S4U) removed the flash and broke connecting: the device node was enabled, but audio never switched over.

The cause is that such a task has **no user session**, and audio is per-session:

- default output and input devices are per user, so `IPolicyConfig::SetDefaultEndpoint` from a session-less context does not change what the logged-on user hears;
- `Windows.Media.Control` only sees the media sessions of a user session, so pausing playback silently does nothing;
- no window can be drawn.

Meanwhile the one thing that genuinely needs elevation, changing the Bluetooth device node, is session-independent.

## Decision

Two processes, divided by which session they need rather than by which privileges they need.

| | `PodGate.Service` | `PodGate` (tray app) |
|---|---|---|
| Runs as | LocalSystem, session 0, automatic start | the logged-on user, unelevated, autostart |
| Owns | the device node: block, unblock, restore-stock; re-assert the block at start and periodically; give the device up at shutdown | tray icon, hotkeys, progress card, settings; all audio: endpoint scan, default devices, KS one-shots during a connect, media pause |
| Talks | answers five verbs on `\\.\pipe\PodGate.v1` | asks |

The verb set is frozen at `GetVersion`, `GetStatus`, `Block`, `Unblock`, `Restore`, and **none takes a device argument**: the target comes from admin-written `config.json`, so an unelevated caller can never steer an elevated action. The pipe grants SYSTEM and Administrators full control and interactive users read/write; unknown verbs are rejected before any work starts.

`Block` also sends the KS one-shot disconnect, so shutting down is a single call the service can complete with no user session present.

## Consequences

- The service contains no audio code, so audio can never be switched from the wrong session.
- No console window anywhere: both components are compiled Windows binaries, nothing launches PowerShell or `schtasks`.
- Releasing at shutdown becomes possible, and so does re-asserting the block after a driver update.
- One process owns the connect flow and the progress card; the service serialises block and unblock.
- Changing the device node via `SetupDiCallClassInstaller` takes tens of milliseconds (the PnP cmdlets took ~0.6 s); a pipe round trip is ~65 ms.
- New failure mode: if the service is stopped, the app can read state but cannot block or unblock. It says so, and `scripts/restore-bt.ps1` remains the standalone escape hatch.

## Alternatives rejected

| Option | Why not |
|---|---|
| Scheduled tasks for the privileged part, audio in the tray | Works, but several tasks for the installer to manage, and still no way to act at shutdown |
| Interactive scheduled tasks | Console window flash on every action |
| One elevated process doing everything | Either no session (audio breaks) or an elevated process in the user's session (UAC prompt per action) |
| Service doing audio via session impersonation | Much more code and failure modes for something the unelevated app does correctly |
