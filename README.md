# PodGate

Stops Windows from grabbing your AirPods on its own, and connects them when you ask: with a hotkey or from the tray.

Windows reconnects paired Bluetooth audio devices at boot, often before you log in. AirPods follow the newest connection, so your phone loses them mid-song. Windows has no per-device "don't connect automatically" setting, and unpairing isn't an option if you also use the AirPods with the PC.

PodGate keeps the AirPods **blocked at rest** by disabling their Bluetooth device node, which survives reboots, sleep and Fast Startup, and enables it again when you connect. Other Bluetooth devices are not touched.

> Status: early (0.x). Developed and tested with AirPods Pro 2 (USB-C) on Windows 11. Other AirPods models and Bluetooth adapters may behave differently; reports are welcome.

## Install

1. Pair your AirPods with Windows once, in **Settings > Bluetooth & devices**.
2. Download `PodGate-Setup-<version>.exe` from [Releases](https://github.com/melnikovalex/podgate-windows/releases) and run it. It asks for administrator rights once.
3. The tray icon appears. The AirPods are disconnected from the PC from now on until you connect them.

Installing a newer version over an existing one keeps your settings. A downgrade is refused.

## Use

| | |
|---|---|
| `Ctrl+Alt+Shift+A` | Connect or disconnect, whichever applies |
| `Ctrl+Alt+Shift+S` | Connect for music only: no microphone, so audio stays in full A2DP quality |
| Tray icon (left or right click) | Menu with the same actions and their hotkeys |

- **Connect** makes the AirPods the default output and the default communications microphone, so calls use them while music stays in high quality. It gives up after about 12 s if they don't answer (in the case, or out of range) and blocks them again.
- **Disconnect** pauses playback if it was going to the AirPods, hands audio back to the previous device, and blocks the AirPods so your phone can take them.
- A small card at the bottom of the screen shows progress and disappears by itself. Its close button only hides it; the action continues.
- **Shutdown and restart** release the AirPods automatically.
- Tray icon: white = connected, purple = connected for music, grey = disconnected.

## Uninstall

**Settings > Apps > Installed apps > PodGate > Uninstall.** The uninstaller first gives the AirPods back to stock Windows (device node enabled, all Bluetooth services on), then removes the service, autostart entry, files, configuration and logs.

If PodGate is broken and the uninstaller cannot run, `scripts\restore-bt.ps1 -Apply` from an elevated PowerShell re-enables everything on its own.

## How it works

Two processes, split by session rather than by privilege ([ADR-002](docs/adr/ADR-002-service-split.md)):

- **PodGate.Service** runs as LocalSystem and owns one thing: whether the AirPods' device node is enabled ([ADR-001](docs/adr/ADR-001-block-strategy.md)). It re-asserts the block when it starts and gives the device up at shutdown. It answers five fixed verbs on a named pipe, none of which takes a device argument.
- **PodGate** (the tray app) runs unelevated in your session and does everything that belongs to a logged-on user: hotkeys, the progress card, default audio devices, pausing media.

The details that make it work reliably (and the Windows behaviours behind every timing constant) are in [docs/windows-notes.md](docs/windows-notes.md).

## Build

Requirements: Windows 10 2004 or later, .NET 10 SDK, Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet build PodGate.slnx
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1   # -> artifacts\setup\PodGate-Setup-<version>.exe
```

`tests/PodGate.Hardware.SmokeTests` needs real, paired AirPods and is never run in CI. Before experimenting with Bluetooth state on your own machine, take a snapshot with `scripts\backup-bt.ps1`.

## Privacy

PodGate does not transfer any information to other networked systems. It has no telemetry, no update check and no network code. Logs and configuration stay on your PC (`%ProgramData%\PodGate`, `%LOCALAPPDATA%\PodGate`) and are removed on uninstall.

## Code signing policy

Releases are built from this repository by GitHub Actions. Code signing for release binaries is being applied for with the [SignPath Foundation](https://signpath.org), which provides free code signing for open-source projects; until then releases are unsigned.

| Role | Members |
|---|---|
| Committers and reviewers | [melnikovalex](https://github.com/melnikovalex) |
| Approvers | [melnikovalex](https://github.com/melnikovalex) |

Once signing is in place, each signed release is built from a tagged commit on `main`, and only binaries built from this repository's source are signed.

## License

[MIT](LICENSE)
