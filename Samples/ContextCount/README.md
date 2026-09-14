# Counting Wintab contexts

Three small C programs that report the Wintab driver's own context counters, do one thing, and
report them again. They exist to demonstrate, without a framework or a language runtime in the
way, that **a process which dies without calling `WTClose` can leave contexts behind**.

What they showed, and what it means, is in
[Docs/WINTAB-CONTEXT-LEAK.md](../../Docs/WINTAB-CONTEXT-LEAK.md).

| | |
|---|---|
| `system-context.c` | Opens a system context — cursor control plus packets, the ordinary case. |
| `digitizer-context.c` | Opens a digitising context — tablet-native coordinates, no cursor. One is enough; a digitiser does not need a second, system context alongside it. |
| `leak-context.c` | Opens a system context and calls `TerminateProcess` on itself, which is what a task manager, a crash, and a debugger's stop button all look like to the driver. |

They load `Wintab32.dll` by name, so they need no Wacom SDK and no import library:

```
cl /W4 system-context.c    user32.lib
cl /W4 digitizer-context.c user32.lib
cl /W4 leak-context.c      user32.lib
```

Nothing is drawn and no messages are pumped. These are about opening and closing, not about pen
input — for that, see the Scribble sample applications.

Run `leak-context.exe` a few times and then `system-context.exe`, and the count it starts from
will be higher every time.

## Independent investigation probe

`investigate.c` is the independent instrument for [issue #121](https://github.com/TheSevenPens/WinPenKit/issues/121).
It declares its own ABI and calls `WTInfoA` directly; it does not use `wtcount.h` or WinPenKit.
Methods, results, and raw data are in [the investigation record](../../Docs/WINTAB-INVESTIGATION-2026-09.md).

Build with `build-investigate.cmd` (x64) or `build-investigate.cmd x86`. The script discovers
Visual Studio with `vswhere`; alternatively use a developer prompt:

```bat
cl /nologo /W4 /WX /wd4191 investigate.c user32.lib
```

Only the documented function-pointer casts from `GetProcAddress` suppress warning C4191.
The x86 script places its executable under `x86/`. No SDK, install, or import library is needed.

Commands below use the x64 executable:

```bat
investigate snapshot
investigate info
investigate manager
investigate lifecycle 3 clean mixed
investigate lifecycle 3 clean system 0
investigate lifecycle 1 clean null
investigate lifecycle 1 clean null-poll
investigate reclaim kill system 3
investigate reclaim return digitizer 3
investigate reclaim destroy-return system 3
investigate watch 3600 10000
investigate packet-check
```

- `snapshot`, `info`, and `watch` only read Wintab information. `watch` uses one persistent DLL
  connection; its arguments are duration in seconds and sampling interval in milliseconds.
- `manager` opens and closes manager handles, enumerates without closing contexts, and compares
  hidden, visible, and NULL manager windows. A visible test window appears briefly.
- `lifecycle N MODE KIND [DEVICE] [HOLD_MS]` opens up to 128 contexts. `clean` closes them;
  `cleanup` also calls `WacomCleanup` after all owned contexts are closed. `kill`, `return`, and
  `destroy-return` deliberately omit `WTClose` and can leave driver resources behind.
  Kinds are `system`, `digitizer`, `mixed`, `null`, and `null-poll`. Device -1 preserves the
  driver's default virtual selection. The optional hold pumps window messages before exit
  or cleanup, including after an open fails. On failure, successful opens are closed normally.
- `reclaim MODE KIND N` creates its own child, confirms its exit through a retained process
  handle, then closes only enumerated test contexts named for that child. A separate live
  sentinel must remain valid. It never treats a NULL or invalid owner HWND as sufficient
  evidence to close an arbitrary context. Zero candidates returns nonzero: no reclamation
  was demonstrated. Candidate overflow also returns nonzero rather than claiming full recovery.
- `packet-check` is an interactive hardware test. Keep drawing while its window is active.
  It requires pressure packets before and after reclaiming a killed test child's contexts,
  using the same live context. It flushes queued packets between phases and closes after
  success or a three-minute timeout. Mouse events cannot satisfy it.

**Confirm you can restore the tablet before deliberate leakage.** The programs do not restart
services. If a driver refuses or hangs, preserve the output before deciding how to recover.
The observed Wacom driver can leave a context even when `WTOpenA` returns NULL, so an unsuccessful
run is not necessarily balanced.

CSV records retain UTC timestamps, process IDs, handles, return values, timings, both counters,
and their returned byte counts. Treat a counter as reported only when its byte count is four.
`0xFFFFFFFF` with zero returned bytes means unsupported/unavailable, not an enormous count.
Do not infer global cleanup from a reading in a process that just called `WacomCleanup`:
cross-check with a fresh process.
For manager rows, device/options/name fields are meaningful only when `get=1` in the detail.
Failed `WTGetA` leaves the probe's zero-initialized structure, not a reported device 0.

`observe.ps1 -OutputDirectory <new-directory>` records both fresh-process and persistent readers
for one hour by default, plus Wacom process IDs, memory, thread and handle counts. It is read-only
and refuses to overwrite its core CSVs. Run it as the normal interactive user so the reader sees
the same driver session as the applications under investigation.

`summarize-observation.py <directory> [--output summary.json]` reads those CSVs using Python's
standard library and reports durations, gaps, counter ranges, invalid reads, and process metrics.
The persistent reader's final `sample_end` row distinguishes completion from an interim summary.
Distinct PID counts can be lower than fresh launches because Windows reuses process IDs.

`probe-capacity.ps1 -OutputDirectory <new-directory>` performs a bounded allocation experiment:
one process attempts 128 virtual system opens and holds its successful handles for two minutes.
Fresh processes capture counters, manager enumeration, driver metrics, and system/digitizer/
explicit-device controls while those allocations are held. Successful handles are then closed
and a fresh control is retried. Timeouts preserve the affected processes and logs for inspection.
Failed opens may leave residue; this is not a read-only or guaranteed balanced command.
