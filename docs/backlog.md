# Roadmap

In order. Each milestone ends with a hardware test on a real PC and a release.

## 1. Onboarding and settings - shipped in 0.6

Setup (welcome, choose AirPods, permission, shortcuts with live tests, connect test, done), the settings window, autostart and connect-on-startup are in. Still open here:

- Default sound devices per state, chosen in settings: output (and microphone) for "connected", "connected for music", and "disconnected" (default: return to the previous device).
- Pairing link on the "no AirPods" page opens Windows' add-device page; confirm it lands there on other machines.

## 2. Battery and ear detection - shipped in 0.7

Battery in the tray menu and on the progress card, warnings at 20% and 5%, ear detection, and the first unit tests (the advertisement parser). Still open:

- Confirm the battery numbers against the phone (test T19) and the wear bits by taking a pod out (T20): both are inferred from an undocumented advertisement.
- Show battery for the configured pair rather than the strongest one in range, if a reliable way to tell them apart turns up.

## 3. Audio configurations per action (0.8)

- Choose the output, the microphone and the "communications" microphone separately for **Connect**, **Connect music** and **Disconnect** (disconnect defaults to handing back the previous devices).
- Switch between music and call quality **without disconnecting**: turn the AirPods' Hands-Free side off and on (the `0000111e` service, or the Hands-Free endpoint) instead of dropping the link. Measure what that does to a live A2DP stream before shipping it.

## 4. Other AirPods models and switching pairs (0.9)

- Model table for the BLE parser (AirPods 2/3/4, Pro 1/2, Max, and Beats that use the same advert): pod layout, case or no case.
- Change the target AirPods after installation: re-run onboarding, unblock the old pair, block the new one.
- Later, possibly: several pairs managed at once.

## 5. Release on sleep

- Disconnect the AirPods when the PC goes to sleep or hibernates while they are connected (T8 for sleep). Today only shutdown and restart release them.

## 6. Hardening before 1.0

- Test matrix T2, T5-T7, T10-T16, T19-T22 on the installed build.
- Confirm the fallback for a node left live-disabled after boot when it actually triggers (it logs when it runs).
- Diagnostics command that checks service, pipe, versions, device node and endpoints in one report.

## 7. Signing

- MSIX proof of concept: packaged LocalSystem service (`packagedServices`, `localSystemServices`), startup task for the tray app, hotkeys. MSIX uninstall cannot run custom actions, so the service must restore the AirPods itself when it is stopped outside a system shutdown.
- If MSIX works: Microsoft Store submission (Microsoft signs the package, updates through the Store).
- If not: MSI (WiX) instead of Inno Setup, signed via SignPath Foundation if accepted, otherwise a Certum open-source certificate.
- Ship `scripts/restore-bt.ps1` with the installation as a diagnostics entry.

## Later

- A2DP-only mode for setups that route the microphone elsewhere (e.g. Voicemeeter).
