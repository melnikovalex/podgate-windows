# Windows behaviours PodGate depends on

Observed on Windows 11 with AirPods Pro 2 (USB-C) and the in-box Microsoft Bluetooth stack. Each item explains a piece of code or a constant; if you change that code, re-check the behaviour.

## Connecting and blocking

- **Windows reconnects within a second of a manual disconnect.** Disconnecting in Settings, or reacting to a connection with a script, is pointless while the device is enabled: the link comes straight back. A block has to remove the reason to connect, not the connection (ADR-001).
- **The baseband link forms without any audio profile.** With A2DP and Hands-Free disabled the device still connects at boot and the phone still loses it. Only disabling all services, or the device node, prevents it.
- **Enabling a profile is not a connect.** If the link is already up, re-enabling a service creates the audio endpoints but leaves them `Unplugged`. What actually starts audio is `KSPROPERTY_ONESHOT_RECONNECT` on the endpoint's kernel-streaming filter, the same call Windows' sound settings make (`KsBtAudio.OneShot`).
- **Disabling a device node is deferred while an audio stream is open.** A release during playback returns success and nothing happens. `Block` therefore sends `KSPROPERTY_ONESHOT_DISCONNECT` to active endpoints first, and the app hands the default output back and waits ~700 ms before blocking, so apps can move their streams.
- **Disable only the root node.** Disabling the audio child nodes as well makes the next connect take more than 12 s instead of well under one.
- **`DICS_FLAG_CONFIGSPECIFIC` is a silent no-op** without extra hardware profiles: `SetupDiCallClassInstaller` returns success and ConfigFlags is unchanged. Use `DICS_FLAG_GLOBAL`, and read ConfigFlags back (`DeviceNodes.SetEnabled`).
- **After a boot with the node disabled, enabling it can leave the live node disabled.** ConfigFlags is cleared, but the node, never started this boot, keeps problem code 22 (`CM_PROB_DISABLED`) and has no audio children, so the connect times out with the AirPods in range. `CM_Enable_DevNode` does nothing once the flag is clear; re-enumerating, restarting the node, cycling services and an uncached SDP query do not help either. `pnputil /enable-device` starts it. `BlockController.StartIfStillDisabled` checks the live problem code, tries `CM_Setup_DevNode`, and falls back to `pnputil`. It does not happen on every boot.
- **Endpoint states tell you where you are:** `NotPresent` after a boot with the device blocked (no KS filter exists yet), `Unplugged` after an in-session disconnect (the one-shot reconnect works), `Active` when connected.

## Switching between music and call quality

- **The Hands-Free side can be turned off without dropping the link.** Disabling the device's `0000111e` service while it is connected takes ~3 s, leaves the A2DP render endpoint `Active` and the baseband link up, and makes both Hands-Free endpoints `NotPresent`. Enabling it again takes ~3 s and brings a Hands-Free render and capture endpoint back as `Active`, again without touching the music stream. That is how music quality is switched on a connected pair, instead of disconnecting and reconnecting.
- **The Hands-Free endpoints come back with new identifiers**, and the old ones linger as `NotPresent`. Anything that remembers an endpoint id must resolve it again after a switch; match by container and transport, never by a stored id.

## Audio defaults

- **The Hands-Free render endpoint goes `Active` about a second before the A2DP one**, and Windows refuses it as a default output with `E_FAIL`. Connect waits for the A2DP render endpoint specifically (up to 12 s, polled every 200 ms).
- **An endpoint is listed as `Active` a moment before the audio service accepts it as a default.** Setting it immediately fails with `E_FAIL`. Retry (6 x 500 ms) and read the default back, because a silent no-op looks exactly like success.
- **The capture endpoint arrives after the render endpoint** (allow ~5 s).
- **Microphone for the communications role only.** Making the AirPods mic the default for all roles switches every app to Hands-Free and music drops to call quality. Role 2 (communications) lets calls use the mic while music stays on A2DP.
- `IPolicyConfig::SetDefaultEndpoint` is undocumented but works unelevated; it is what every audio switcher uses.

## Discovery

- **Unplugged endpoints have no PnP device node.** `Get-PnpDevice -Class AudioEndpoint` misses them; read `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{Render,Capture}\*\Properties` and match `PKEY_Device_ContainerId` (`{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2`, an 8-byte header followed by the GUID).
- **The device's container ID must come from the Configuration Manager** (`CM_Get_DevNode_PropertyW`): the `Enum\...\Properties` registry keys are not readable unelevated.
- **Tell A2DP from Hands-Free by instance path**, never by friendly name: A2DP endpoints sit under `BTHENUM\{0000110b-...}`, Hands-Free under `BTHHFENUM`. Friendly names are localized.
- **The KS filter path** is stored in endpoint property `{233164c8-1b2c-4c7d-bc68-b671687a2567},1` with a `{N}.` prefix to strip.
- **Apple devices** are recognised by `VID` 0x004C under `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<address>`.
- Hands-Free audio nodes (`BTHHFENUM`) do not carry the Bluetooth address in their instance ID; match them by container ID.

## Sessions and processes

- **Scheduled tasks that run "whether the user is logged on or not" have no user session**: default devices and media controls are per session, so audio work there silently does nothing (ADR-002).
- **A console-subsystem exe always gets a console window**; a `WinExe` never does.
- **Named pipes carry bytes, not messages.** Use line- or length-delimited framing, and drain the pipe before disconnecting or the reply is lost.
- **Explorer holds a shortcut's hotkey for a while** after the `.lnk` is deleted, so a freshly freed combination can be briefly unavailable.
- **Overlapping connect and release runs** can leave the AirPods connected in Hands-Free-only mode: one action at a time.

## Battery over Bluetooth LE

- **AirPods battery only exists in an advertisement.** Apple's "proximity pairing" broadcast (manufacturer `0x004C`, type `0x07`, 27 bytes) carries battery per pod and case as nibbles in ten-percent steps, plus charging and wear bits. Nothing else on Windows reports it while the AirPods are connected to a phone.
- **Other Apple products send type `0x07` too.** Their bytes decode into believable nonsense; byte 4 is `0x20` on every pair that reports battery, which is what tells them apart (measured: a neighbouring device read as "AirPods, left 0 %").
- **The advertising address rotates** every few minutes and carries nothing that ties it to a paired device, so a pair can only be picked by model and signal strength. Passive scanning is enough; no pairing and no elevation are involved.
- **The wear bits are the least certain part** of the layout: a pair that never reports them simply never triggers ear detection.

## Antivirus

- Heuristics react to scripts that write `RunOnce` values and relaunch themselves, to runtime compilation (`Add-Type`), `-ExecutionPolicy Bypass`, and to unsigned installers running executables from `%TEMP%` (Inno Setup's uninstaller copies itself there). Compiled binaries remove most of these triggers; code signing is the real fix.
