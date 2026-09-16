# Roadmap

In order. Each milestone ends with a hardware test on a real PC and a release.

## 1. Onboarding and settings (0.6)

- First start: what PodGate does and that it is reversible, pick the AirPods (Apple devices first, by address), set and test hotkeys with live pass/fail, test connect and disconnect with progress, and leave the AirPods connected afterwards.
- No paired AirPods found: explain how to pair, with a link to Windows' Bluetooth settings (`ms-settings:bluetooth`).
- Settings window: device, hotkeys, connect mode, notifications. The device choice is written elevated, showing the device again at the prompt.
- Re-run onboarding from the tray menu at any time.

## 2. Battery and ear detection (0.7)

- Watch Apple's proximity-pairing BLE advertisement (manufacturer 0x004C, type 0x07) in the tray app: model, battery per pod and case (10% steps), charging, in-ear, lid open. The address rotates, so match by model and signal strength; a nearby pair of the same model can be mistaken for yours.
- Parser with unit tests against recorded adverts (the first unit test project, runs in CI).
- Tray menu, first row: `L 80% · R 75% · Case 60%`, one value when equal, `—` when unknown.
- Low-battery notifications at 20% and at 5%, once per threshold per charge; on/off as a checked tray menu item stored per user (no elevation needed).
- Progress card: battery glyph, grey above 20%, yellow at or below 20%, red at or below 5%, percentage on hover.
- Ear detection: a pod taken out pauses media that is playing on the AirPods; back in within 60 s resumes only what PodGate paused. Off switch in the tray menu.

## 3. Other AirPods models and switching pairs (0.8)

- Model table for the BLE parser (AirPods 2/3/4, Pro 1/2, Max, and Beats that use the same advert): pod layout, case or no case.
- Change the target AirPods after installation: re-run onboarding, unblock the old pair, block the new one.
- Later, possibly: several pairs managed at once.

## 4. Release on sleep (0.8.x)

- Disconnect the AirPods when the PC goes to sleep or hibernates while they are connected (T8 for sleep). Today only shutdown and restart release them.

## 5. Hardening before 1.0

- Test matrix T2, T5-T7, T10-T16, T19-T22 on the installed build.
- Confirm the fallback for a node left live-disabled after boot when it actually triggers (it logs when it runs).
- Diagnostics command that checks service, pipe, versions, device node and endpoints in one report.

## 6. Packaging and signing

- MSIX proof of concept: packaged LocalSystem service (`packagedServices`, `localSystemServices`), startup task for the tray app, hotkeys. MSIX uninstall cannot run custom actions, so the service must restore the AirPods itself when it is stopped outside a system shutdown.
- If MSIX works: Microsoft Store submission (Microsoft signs the package, updates through the Store).
- If not: MSI (WiX) instead of Inno Setup, signed via SignPath Foundation if accepted, otherwise a Certum open-source certificate.
- Ship `scripts/restore-bt.ps1` with the installation as a diagnostics entry.

## Later

- A2DP-only mode for setups that route the microphone elsewhere (e.g. Voicemeeter).
