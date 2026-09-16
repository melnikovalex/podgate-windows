# PodGate: instructions for contributors and coding agents

PodGate keeps paired AirPods from auto-connecting to Windows and connects them on demand. Read `README.md` for what it does and `docs/adr/` for why it is built this way. `docs/windows-notes.md` holds the Windows behaviours the code depends on.

## This is a public open-source repository

Everything committed is published. Before every commit:

- **No personal or machine data.** No real Bluetooth addresses (use `AA:BB:CC:DD:EE:FF` / `AABBCCDDEEFF` in docs and examples), no usernames, computer names, email addresses other than the public author identity, local paths under a user profile, serial numbers or container IDs from a real machine.
- **No logs, backups, registry exports or test results from a real machine.** Summarise findings in general terms in `docs/windows-notes.md` instead ("the Hands-Free endpoint goes active first"), never paste raw output.
- **Machine-specific notes go to `private/`** (git-ignored): experiment logs, change ledgers, results. `backups/` (written by `scripts/backup-bt.ps1`) is git-ignored too.
- **Never commit** `*.secret.*` files (pairing link keys), `private/`, `backups/`, `artifacts/`.
- Read the staged diff before committing and look for addresses (`XX:XX:XX:XX:XX:XX` or 12 hex digits), user profile paths and names.

## Commits and branches

- **Commit messages:** one short lowercase line, imperative (`add msi installer`, `fix connect timeout after boot`). More lines only in exceptional cases. No `Co-Authored-By` lines, no tool or model attribution, no links.
- **Branches:** `dev` is where work lands; `main` only receives merges from `dev` and is what releases are built from. Small changes go straight to `dev`; anything larger goes through a feature branch and a pull request into `dev`.
- **Releases:** bump `VERSION` on `dev`, merge `dev` into `main`, tag `vX.Y.Z` on `main` and push the tag. CI builds the installer and publishes the GitHub release. The tag must match `VERSION`.

## Engineering rules

1. **Everything must be reversible.** Every system change PodGate makes has a matching restore path: the uninstaller (`PodGate.Service.exe --restore-stock`) and the standalone `scripts/restore-bt.ps1`, which must keep working when the app is broken. Never unpair devices, never delete `BTHPORT` keys.
2. **Back up before experimenting.** On your own machine, run `scripts/backup-bt.ps1 -Label <what>` before touching Bluetooth devices, services, audio defaults or power settings by hand or with a new build.
3. **Never match on localized names.** Identify devices by Bluetooth address, container ID, device instance path, hardware ID or service UUID. Friendly names change with the Windows display language and can be renamed by the user.
4. **Least privilege.** Only the service runs elevated. Its pipe has a fixed verb set (`GetVersion`, `GetStatus`, `Block`, `Unblock`, `Restore`), none takes a device argument, and the target device always comes from admin-written config. Configuration writes never cross the pipe.
5. **Split by session.** The service has no user session: it must never touch default audio devices, media sessions or UI. The tray app does all of that, unelevated (ADR-002).
6. **Measured, not guessed.** Timing constants and retry counts in `ConnectFlow` exist because of observed failures. Change them only with new evidence, and record the behaviour in `docs/windows-notes.md`.
7. **Verify, don't trust the API.** Device-node and default-device calls can return success and change nothing. Read the result back.
8. **No telemetry, no network.** PodGate transfers nothing to other systems (this is part of the code signing policy).
9. **Hardware tests stay local.** `tests/PodGate.Hardware.SmokeTests` needs real AirPods; CI only builds.

## Coding style

Simplest solution that works: reuse before writing, standard library and native Windows APIs before dependencies, one line before fifty. The `ponytail` skills in `.claude/skills/` (MIT, github.com/DietrichGebert/ponytail) describe this style for agents. Rules 1 and 2 are never simplified away.

- C# on .NET 10, nullable enabled, warnings are errors. Comments explain *why*, especially Windows quirks.
- PowerShell scripts must run on Windows PowerShell 5.1 and be **ASCII-only** (5.1 reads BOM-less files as ANSI).

## Layout and shipping

- `src/PodGate.Core` (device nodes, Bluetooth, audio, IPC), `src/PodGate.Service` (Windows service), `src/PodGate.App` (tray app, WPF card), `tests/PodGate.Hardware.SmokeTests`, `scripts/` (standalone backup, restore and diagnostics), `setup/PodGate.iss` (installer).
- `VERSION` holds the semver and is the only place it lives: the assemblies and the installer both read it.
- `build.ps1` publishes the service and the app self-contained into `artifacts\publish\PodGate` and compiles the installer into `artifacts\setup\PodGate-Setup-<version>.exe`.
- Installed layout: everything in `%ProgramFiles%\PodGate`, Windows service `PodGate` (LocalSystem, automatic), HKLM `Run` value `PodGate` for the tray app, config and service log in `%ProgramData%\PodGate`, the app's log and state in `%LOCALAPPDATA%\PodGate`.
- Anything new the installation needs must be added to the installer, and removed again by the uninstaller.
