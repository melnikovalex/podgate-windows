# Test matrix

Manual tests on real hardware, run before a release. Record results in your own notes (`private/`, git-ignored); summarise new Windows behaviour in `docs/windows-notes.md`.

"Grab" means Windows connecting the AirPods by itself while they are playing from a phone.

| ID | Priority | Scenario | Expected |
|---|---|---|---|
| T1 | P0 | Cold boot, Fast Startup on | No grab |
| T2 | P0 | Cold boot, Fast Startup off | No grab |
| T3 | P0 | Restart | No grab |
| T4 | P0 | Sleep, then wake | No grab |
| T5 | P0 | Lid close, then open | No grab |
| T6 | P0 | Hibernate, then resume | No grab |
| T7 | P0 | Bluetooth turned off and on in Quick Settings | No grab |
| T8 | P0 | Left connected, then shutdown, restart or sleep | Released; no grab on next start |
| T9 | P0 | Other Bluetooth devices (classic headset, LE mouse or keyboard) throughout | Unaffected |
| T10 | P1 | Connect while the AirPods are in the closed case | "Not reachable" within ~12 s, blocked again |
| T11 | P1 | Connect while the phone is on a call | Behaviour documented |
| T12 | P1 | Connect | Default output and communications input switched |
| T13 | P1 | Disconnect | Phone can take the AirPods back; previous defaults restored |
| T14 | P1 | Service killed mid-operation | Reconciles to blocked |
| T15 | P1 | Log off and on, fast user switching | Consistent state |
| T16 | P1 | Windows or Bluetooth driver update | Block re-asserted |
| T17 | P1 | Uninstall | Stock Windows behaviour: device node enabled, services on, nothing left behind |
| T18 | P1 | Install over an older version | Settings kept, service and app restarted |
| T19 | P2 | Case opened, AirPods connected | Battery shown |
| T20 | P2 | Earbud out and back in, desktop player and browser | Pause and resume |
| T21 | P2 | Another pair of the same model nearby | Accuracy documented; setting to disable works |
| T22 | P2 | Player without Windows media controls | No pause (expected) |
