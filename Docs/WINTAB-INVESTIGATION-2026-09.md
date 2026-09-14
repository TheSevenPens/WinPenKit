# Wintab investigation, September 2026

Work order: [issue #121](https://github.com/TheSevenPens/WinPenKit/issues/121).
Started 2026-09-14. This is the experiment record; settled guidance belongs in
[WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md).

## Initial checks

- Checkout: `d366f5b`, clean `main` before investigation.
- Windows 11 Pro build 26200; boot time 2026-09-11 14:05:53 local.
- System Wintab DLL: Wacom, version 1.0.5-10; driver processes 6.4.14-1;
  Wacom Cintiq 24 touch (DTH246). Inventory is in `data/wintab-2026-09-14/environment.json`.
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
The final probe adds a per-run performance-counter tag to the child PID in that prefix;
PID reuse must not cause an earlier run's contexts to match. The initial CSVs predate
that extra identity guard. Final hardware validation uses the tagged implementation.

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

At 18:13-18:17 UTC, read-only x86 capability queries further distinguished the two profiles.
The user confirmed only the Cintiq was physically connected, and a fresh present-device
inventory listed that display. Device 0 reports six cursor types starting at 0, 200 Hz,
X/Y maxima 53084/30034, hardware flags 13, and a nonempty hardware identifier. Device 1
reports six cursor types starting at 6, 100 Hz, X/Y maxima 30930/17398, hardware flags 5,
and an empty identifier. Both axes use 1000 units/cm, giving approximately 531 by 300 mm
and 309 by 174 mm respectively. These are reported capabilities, not measured packet rates.
Hardware identifier text is deliberately omitted from the CSV.

The persistent reader loads both `Wintab32.dll` ("Wintab Coordinator") and
`Wacom_Tablet.dll` ("WINTAB32", 6.4.14-1). A separate `investigate direct-info` process
loaded the latter directly for read-only queries: it reported the same 405/405 counters,
one connected device, twelve cursors, and both distinct profiles. Its module check confirmed
the coordinator was not loaded. Thus the duplicate profile/counter behavior is also exposed
by the backend, not solely introduced by coordinator aggregation. Direct backend loading is
an investigation technique, not a recommended application API.

Raw files: `device-capabilities-x86.csv`, `direct-backend-info-x86.csv`, `reader-modules.json`.
All twelve `CSR_ACTIVE` reads returned 1, including both profiles' puck/stylus/eraser entries,
so that flag did not distinguish the source of profile 1. The user later recalled that a
Cintiq Pro 32 may have been connected a few weeks earlier. This makes a retained profile a
plausible lead, but neither these queries nor that recollection identifies profile 1 as that
tablet. The origin of profile 1
(for example a remembered device or an internal virtual device)
remains unproven. The older [Wacom developer FAQ](https://www.wacomeng.com/windows/docs/WacomWindevFAQ.html),
version 1.4 updated January 2, 2014 and read September 14, 2026, says in section 3.22 that
`IFC_NDEVICES` counts connected tablets; section 3.27 warns that Wintab is not a model lookup.
This helps interpret the metadata without identifying the second profile's origin.

At 18:41 UTC, a read-only examination of selected fields in the existing Wacom preference
file found two tablet entries: **Wacom Cintiq 24 touch**, physically on, dimensions
53085/30035; and **Wacom One 14**, physically off, dimensions 30931/17399. Each pair is
exactly one greater than the corresponding Wintab X/Y maxima, as expected when comparing
size with a zero-based maximum coordinate. This strongly supports a retained Wacom One 14
profile as the source of device 1, rather than the recalled Pro 32. It remains an inference
from matching metadata, not a proven internal mapping or evidence that the One 14 was
previously physically connected. No preferences were removed or modified to test causality.

The selected metadata is in [preference-profiles.json](data/wintab-2026-09-14/preference-profiles.json).
`read-preference-profiles.ps1` reproduces the extraction without loading Wintab or copying
serials, sensor identifiers, button mappings, or per-application settings.

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

## Replication, service recovery, and source tracing

At 17:33 UTC, restarting `WTabletServicePro` cleared the deliberately retained two test entries:
4 before restart, then 2 baseline, 4 with a fresh virtual system context, 2 after its successful
close. The service was Running. Files: `before-first-restart.csv`, `after-first-restart.csv`,
and `environment.json` in [the data directory](data/wintab-2026-09-14/).

At 17:34 UTC an x86 build repeated the NULL-window message and polling probes: both returned
NULL, at 0.025 and 0.012 ms, with the baseline unchanged at 2. x86 manager reclamation also
succeeded: three virtual contexts from its killed child yielded six candidates, closed to
4 while the live sentinel remained valid, then back to 2 after sentinel close. Files:
`x86-info.csv`, `x86-null.csv`, `x86-null-poll.csv`, `x86-reclaim.csv`.

**The headless explanation is visible in source.** At checkout `d366f5b`,
`WinPenKit/Wintab/WintabSessionBase.cs` lines 66-85 stores the application's HWND for capture
scope, constructs `WintabMessagePump`, and calls `OpenContext(_pump.Hwnd)`.
`WintabMessagePump.cs` creates a real hidden Win32 top-level window. The local
PenDynamicsPaint project references this WinPenKit project; its `session.Start(hwnd)` call
therefore does not imply that the same HWND reaches `WTOpenA`. No PenDynamicsPaint files
were modified or tests launched. This corrects the causal inference about Avalonia's zero stub.

## Large-leak run: a failed open can itself leak

The intended experiment was eight sequential processes, each leaking 67 virtual system
contexts, followed by an hour at the predicted 1074 count. It **did not reach that state**.

At 17:35 UTC:

- Cohorts 0, 1 and 2 opened all 67 and terminated themselves. Counts ended at 136, 270, 404.
- Cohort 3 opened 53 more: its last successful virtual handle was `0x2FF`, total 510.
- Its next `WTOpenA` returned NULL after 27.113 ms, yet the count rose to **511**.
- The probe's error path closed all 53 successful opens, reducing the count to **405**.
  This left 402 entries from the first three cohorts, two built-ins, and one new orphan.
- A manager enumerated 202 readable entries for each of devices 0 and 1, plus one extra
  handle, `0xB00`, for which `WTGetA` failed. Its zero-filled device/options fields are not
  valid metadata. The CSV contains two complete enumerations (hidden and visible manager);
  count each pass separately. The unreadable entry was not selected for manager closure.
- Fresh virtual system, virtual digitizer, explicit device-0 and explicit device-1 controls
  all succeeded at 17:36:43-44 UTC and closed normally. Virtual opens took about 51-54 ms;
  explicit-device opens took about 25-26 ms.

These observations distinguish this allocation failure from a persistent driver-wide refusal.
They do not yet identify the internal exhausted resource. The numerical proximity to an 8-bit
handle index is a hypothesis only. Critically, successful opens above 32 cannot rule out a
different allocation limit. No service restart was used to recover these control opens.

Successful cohort opens ranged approximately 42-57 ms. The first two cohorts averaged about
44 ms, the third about 48 ms, and the fourth's successful opens about 54 ms. Early balanced
explicit-device opens took approximately 22-24 ms. The 27.113 ms refusal is shorter than these
successful virtual opens; it does not reproduce the historical 90 ms signature. Count and
elapsed time both increased during the sequence, so this is not an isolated causal test of
latency versus count.

Raw data: [hour-observation](data/wintab-2026-09-14/hour-observation/), especially `cohort-0.csv`
through `cohort-3.csv`, `failure-info.csv`, `failure-manager.csv`, `failure-processes.json`,
and the four `failure-*-control.csv` files. The original orchestration stopped as soon as
cohort 3 returned nonzero; preserving the failure replaced the planned larger accumulation.

## Timed observation

The observation started at 17:36:43 UTC with 405 retained counted contexts, immediately after
the failure capture. `observe.ps1` records one persistent reader and a fresh-process reader
about every 10 seconds, along with process IDs, memory, thread and handle counts for the Wacom
processes. The four balanced controls above occurred during the first second; subsequent
observation is read-only.

The observation completed normally at 18:36:48.813 UTC. The persistent reader produced
361 valid rows over 3605.173 seconds, including `sample_end`; the fresh readers produced
359 valid rows over 3597.151 seconds. Every total and system reading was **405**, with
four bytes returned. Maximum sampling gaps were 10.028 and 10.072 seconds respectively.
All three Wacom process IDs remained unchanged and every service-status sample was Running.
No observer or persistent-reader error text was emitted.

The main Wacom process had 727-742 handles, 68-73 threads, and 45,748,224-47,292,416
private bytes across the recorded samples. These ranges are observations, not a diagnosis
of a memory leak. Full metrics, timing summaries, and reader validity are in
[summary.json](data/wintab-2026-09-14/hour-observation/summary.json).

This run found no spontaneous count decrease. It does not reproduce or explain the original
1074-to-538 observation: the allocation failure changed the starting population to 405,
and continuous queries could themselves affect driver cleanup. A separate ten-minute control
with all investigation readers closed started at 18:37:40.475 UTC; its result follows below.

### Follow-up protocol prepared during the quiet interval

`Samples/ContextCount/probe-capacity.ps1` will attempt 128 virtual opens in one process,
keeping successful handles open for two minutes even after a failure. During that hold it
captures a manager enumeration, driver metrics, and fresh system, digitizer, device-0, and
device-1 controls. It then retries a control after the holder has closed its successful handles.
This distinguishes persistent refusal, refusal only while resources are held, and an isolated
allocation failure. The script does not assume which outcome will occur or restart the service.

The next hardware control uses `investigate packet-check`: real positive-pressure packets
must arrive on the same live context both before and after manager reclamation. The probe
discards queued pre-reclamation packets before counting the second phase. This check needs
the user to draw; it cannot be satisfied by synthetic mouse messages.

### Read-only side checks

At approximately 17:56 UTC, queries of existing events for `Wacom WUHA Service`, `WacomPen`,
and `WacomWUHAServiceSource` since 17:20 UTC returned no matching events. No logging was
enabled or configuration changed. The query outcomes are in `hour-observation/wacom-events.json`.
This is limited coverage, not proof that no driver event occurred.

A bounded static inspection of the installed x64 DLL's exports, imports, and `WTOpenA`
dispatch code found imported named-pipe and wait APIs, but did not identify the cause or a
90 ms timeout. Imports alone cannot establish which path a failed open took. No debugger was
attached and no running process memory was dumped. Reproduce the entry-point inspection with
MSVC `dumpbin /exports`, `/imports`, and `/disasm /range:0x18000229d,0x1800022a1` against the
installed binary. That jump leads to `0x1800EA6B0`, then the open dispatcher at `0x1800E1B60`.
These addresses belong only to the inspected binary (SHA-256
`6E1E9188C67AB96273F70C9E2BCEE38932BD5CBBCEC8F39EB94CB7037585D7C9`), not a supported API.
The numerical handle-boundary lead remains a hypothesis; binary inspection has not proved it.

The backend's `WTOpenA` export was also followed to its implementation at
`0x1800E3AA0` (backend SHA-256
`8DF25A88AF1A2FB8D7A7DCBEF2800CCE9D0E0C087815B9768C25A207B7FA74CB`). It contains an
explicit NULL-HWND return at `0x1800E3AE8`, corroborating the measured fast refusals. Its
allocation path crosses several internal request/validation calls; the bounded inspection
did not establish which failed at count 511. No private entry point was invoked.

The C probe compiles with MSVC `/W4 /WX /wd4191` on x86 and x64. Both architectures were rebuilt
after the hour's observer exited, including the later child-identity guard.
Nine malformed or out-of-range numeric argument cases
returned exit code 2 without emitting an operation row (`argument-validation.json`). All
three PowerShell scripts passed the PowerShell syntax parser. The CSV summarizer uses Python's
standard library; interim output correctly reported no `sample_end` marker, and the completed
output reports the final marker and all 720 reader rows as valid. Build and syntax results are
recorded in `final-validation.json`.

## External research: what helps and what does not

All sources below were read September 14, 2026. Reading did not include posting anywhere
outside this repository, installing software, or changing tablet/security settings.

- [Wintab 1.4 specification](https://www.wacomeng.com/windows/docs/Wintab_v140.htm), revised
  October 2010 with a 2014 copyright update: sections 5.5-5.6 specify optional manager support,
  enumeration, HWND ownership, and read-only defaults. Tables 7.4-7.6 distinguish supported
  counts, current counts, and virtual-device selection. Unlike the newer web reference, this
  version includes the full `WTMgrContextEnum` and `WTMgrContextOwner` sections.
- [Wacom's sample loader](https://github.com/Wacom-Developer/wacom-device-kit-windows/blob/8c8fe264697a2720c2679c046c0c016a18990a45/WintabSDK/Utils.cpp)
  dynamically resolves manager functions and `WacomCleanup`; its unload path calls cleanup
  immediately before releasing the DLL. It does not continue counting through that connection.
- [Wacom's packet troubleshooting](https://developer-support.wacom.com/hc/en-us/articles/12844524637975-Wintab)
  describes queue overflow as a reason for missing packets. That is a separate diagnosis from
  `WTOpenA` returning NULL; neither symptom should be inferred solely from the other.
- [6.4.14-1 release notes](https://cdn.wacom.com/u/productsupport/drivers/win/professional/releasenotes/Windows_6.4.14-1.html)
  date the release to August 26, 2026 and mention model-specific responsiveness fixes. They do
  not identify a context-leak correction or explain the measured allocation behavior.
- [Godot #38533](https://github.com/godotengine/godot/issues/38533) and its comments report no
  crash. [Merged PR #38535](https://github.com/godotengine/godot/pull/38535) changes the failure
  message to verbose logging. This is not an unresolved measured context leak, contrary to
  the old reference document. That citation is corrected there.
- [Inkscape #1588](https://gitlab.com/inkscape/inkscape/-/issues/1588) attributes missing device
  listings to GTK device registration; pressure could still work. This is an example of a
  superficially related symptom with a different diagnosed cause, not evidence for our leak.
- Blender #111152 could not be freshly inspected because its host blocks the available web
  reader. Targeted Krita and GIMP searches did not provide an accessible instrumented report
  that explains these measurements. No claim of exhaustive tracker coverage is made.

## Status

Experiments are in progress. No explanation for the historical 90 ms refusals or
1074-to-538 observation has yet been established by this investigation.
