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

## Status

Experiments are in progress. No explanation for the historical 90 ms refusals or
1074-to-538 observation has yet been established by this investigation.
