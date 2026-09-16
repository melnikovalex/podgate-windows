# Backlog

## Onboarding

- Show onboarding on first start: what PodGate does and that it is reversible, pick the AirPods, set and test the hotkeys.
- If no paired AirPods are found, ask the user to pair them in Windows' Bluetooth settings, with a link that opens them (`ms-settings:bluetooth`; add-device mode if possible).
- Show progress while testing connect and disconnect, and whether the test passed.
- Do not leave the AirPods blocked right after onboarding: if the test blocked them, connect them again.
- Write `config.json` (elevated, with the chosen device shown again at the elevation prompt).

## Battery and ear detection

- Read battery and in-ear state from Apple's proximity-pairing BLE advertisement (manufacturer 0x004C, type 0x07).
- Tray menu: battery per pod and case (`L 80% · R 75% · Case 60%`), one value when equal.
- Low-battery notifications at 20% and 5%, with a setting to turn them off.
- Progress card: battery glyph, grey above 20%, yellow at or below 20%, red at or below 5%, percentage on hover.
- Ear detection: one pod out pauses media, back in resumes what PodGate paused.

## Installer and releases

- Evaluate an MSI (WiX) instead of Inno Setup: no self-copied uninstaller in `%TEMP%`, native service handling, transactional rollback.
- Code signing via SignPath Foundation once the first public release exists.
- Ship `scripts/restore-bt.ps1` with the installation as a diagnostics entry.

## Other

- Support more than one pair of AirPods.
- A2DP-only mode for setups that route the microphone elsewhere.
