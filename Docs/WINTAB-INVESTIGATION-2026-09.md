# Wintab investigation, September 2026

Work order: [issue #121](https://github.com/TheSevenPens/WinPenKit/issues/121).
Started 2026-09-14. This is the experiment record; settled guidance belongs in
[WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md).

## Initial checks

- Checkout: `d366f5b`, clean `main` before investigation.
- Windows 11 Pro build 26200; boot time 2026-09-11 14:05:53 local.
- System Wintab DLL: Wacom, version 1.0.5-10. Further hardware inventory pending.
- At 10:22 PDT the sandbox identity could not open the service for start/stop.
  Repeating the existing read-only access script as the ordinary user at approximately
  10:24 PDT succeeded, without elevation. No permission or security configuration changed.
- Read issue #121, issue #117, existing leak documentation, and the C demonstrations.
  Their conclusions remain hypotheses until independently rechecked here.

## Method

`Samples/ContextCount/investigate.c` independently declares the small ABI it uses and
calls `WTInfoA(2, 1, ...)` directly. It does not use `wtcount.h`, WinPenKit, or the viewer.
CSV rows preserve returned byte counts, UTC time, process ID, operation results, and
timings. A failed counter read is not interpreted as zero.

First test manager support, then balanced lifecycle operations and device selection.
Only after confirming service recovery access, introduce bounded known leaks. Compare
persistent and fresh-process readers during a timed observation. Keep all generated
measurements under `Docs/data/`. Never close an unowned application context.

## Research leads (read 2026-09-14)

- [Wacom Wintab reference](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-reference/):
  external manager support is optional. Exported manager functions can return failure
  when unsupported. `WacomCleanup()` concerns memory allocated to the calling application.
- [Wacom Wintab basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/):
  recommends closing every context and then calling `WacomCleanup()` during shutdown.
  This is a new experimental lead, not evidence of foreign-context reclamation.

## First results, 17:26-17:30 UTC

The independent reader reported `STA_CONTEXTS=2`, `STA_SYSCTXS=2`, both with
four returned bytes. `IFC_NCONTEXTS=32`, `IFC_NDEVICES=1`, `IFC_NCURSORS=12`,
`IFC_NMANAGERS=9`. `LOGCONTEXTA` is 172 bytes.
Raw record: [initial-info.csv](data/wintab-2026-09-14/initial-info.csv).

### Manager reclamation works for known test contexts

Hidden and visible manager windows both worked; a null manager window was refused.
The initial manager enumeration had two built-in contexts with NULL owners, device
IDs 0 and 1. **An invalid or NULL owner HWND is not a safe deletion criterion.**

The `reclaim` command creates its own child and retains its Windows process handle.
After confirming that child exited, it enumerates contexts and selects only names
with that child's generated prefix, rechecks identity, and closes each selected
handle. It does not reclaim contexts from arbitrary applications.

- Three virtual system contexts leaked by a killed child produced six enumerated
  entries, with device IDs 0 and 1. Counter: 4 baseline, 6 with a live sentinel,
  12 after child exit, then 11/10/9/8/7/6 during foreign closes. The sentinel still
  passed `WTGetA`; closing it returned 4.
- Repeated successfully for three digitizer contexts, three system contexts left
  by normal return without `WTClose`, and three left after `DestroyWindow` plus
  500 ms of message pumping before return.
- Sixteen virtual opens also succeeded: 38 total, then all 32 leaked enumerated
  contexts closed, with the sentinel valid and the baseline restored to 4.
- No service restart occurred during these tests.

See [reclaim-kill-system.csv](data/wintab-2026-09-14/reclaim-kill-system.csv),
[digitizer](data/wintab-2026-09-14/reclaim-kill-digitizer-3.csv),
[return](data/wintab-2026-09-14/reclaim-return-system-3.csv),
[destroy-return](data/wintab-2026-09-14/reclaim-destroy-return-system-3.csv), and
[16 opens](data/wintab-2026-09-14/reclaim-kill-system-16.csv).

This settles that known leaked contexts can be closed without restarting this
driver. It does not establish safe retrospective identification of every orphan,
packet delivery through the sentinel, or recovery from the historical wedged state.

### The counter unit depends on device selection

Three mixed virtual opens gave totals 2/4/6/8 and system counts 2/4/4/6;
closing all three restored 2/2. Three explicit device-0 opens instead gave
2/3/4/5. Explicit device-1 opens also added one each. All closes balanced.
The virtual handle itself differs from the two device handles enumerated by a
manager. Thus the observed factor of two comes from virtual-device expansion,
not a universal cost per `WTOpenA` call. Why two device IDs exist while
`IFC_NDEVICES=1` is a separate question.

Raw records: `balanced-mixed.csv`, `balanced-device0.csv`, `balanced-device1.csv`
under [the data directory](data/wintab-2026-09-14/).

### Null HWND did not reproduce

With default virtual-device settings and all standard packet fields,
`WTOpenA(NULL, ...)` returned NULL both with and without `CXO_MESSAGES`.
The counts did not move; refusals took 0.018 and 0.013 ms respectively.
This contradicts the existing unqualified statement that a null HWND allocates.
It does not prove no configuration or different ABI can ever accept NULL.
See [message case](data/wintab-2026-09-14/balanced-null.csv) and
[polling case](data/wintab-2026-09-14/balanced-null-poll.csv).

### WacomCleanup is an instrument trap

After opening and closing its own context, the probe called exported
`WacomCleanup()`, which returned 1. Its following counter reads returned four
bytes containing zero. However, an independent process immediately read 4;
manager enumeration still showed the two built-in contexts and both entries
from our first killed process. **A zero read after cleanup is not proof of
global reclamation.** It is consistent with the caller's Wintab connection
being torn down, but that internal mechanism has not been proven.

See [wacom-cleanup.csv](data/wintab-2026-09-14/wacom-cleanup.csv),
[fresh reader](data/wintab-2026-09-14/after-cleanup-fresh.csv), and
[manager verification](data/wintab-2026-09-14/after-cleanup-manager.csv).

## Status

Experiments are in progress. No explanation for the historical 90 ms refusals or
1074-to-538 observation has yet been established by this investigation.
