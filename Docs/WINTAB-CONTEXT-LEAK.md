# Wintab contexts are leaked by processes that do not close them

A Wintab context opened by a process that exits without `WTClose` can remain in the
Wacom driver's tables after its owner is gone. This was reproduced with process termination,
ordinary return from `main`, and window destruction before exit.

**A service restart is not the only way to reclaim it.** On the tested driver, a Wintab manager
can enumerate and close known leaked contexts while preserving a live application's context.
See [Reclaiming known contexts](#reclaiming-known-contexts-without-a-service-restart).

This matters to whoever is **developing** a pen application rather than using one, because
stopping a debugger is how a developer ends a process fifty times a day. It is invisible until
something goes wrong, and when something does go wrong it looks like a bug in the application:
the pen moves, the canvas stays empty, and nothing says the samples never arrived.

The original measurements and the September 14 independent recheck are distinguished below.
The recheck corrected several earlier conclusions. Its methods, raw CSVs, and limitations are in
[the investigation record](WINTAB-INVESTIGATION-2026-09.md).

## What was tested against

| | |
|---|---|
| Driver | **Wacom Tablet 6.4.14-1** (`Wacom_Tablet.exe`, `WTabletServicePro.exe` both 6.4.14-1) |
| `wintab32.dll` | 1.0.5-10 |
| Tablet | Wacom Cintiq 24 touch (DTH246) |
| OS | Windows 11 Pro, build 26200 |
| Date | 2026-09-13 |

**Only a Wacom driver was tested.** Nothing here has been checked against Huion, XP-Pen, Gaomon,
Xencelabs, Veikk, or any other vendor's Wintab implementation. Their drivers are separate
implementations of the same API and may count, limit, or reclaim contexts differently. Treat the
numbers as one data point from one driver on one machine.

## The demonstration programs

Three of them, in `Samples/ContextCount`, in plain C. They load `Wintab32.dll` by name, so they
build with nothing but a compiler — no Wacom SDK, no import library:

```
cl /W4 system-context.c    user32.lib
cl /W4 digitizer-context.c user32.lib
cl /W4 leak-context.c      user32.lib
```

Each reports the driver's context counters, does one thing, and reports them again. The counters
are:

| | |
|---|---|
| `WTI_STATUS` / `STA_CONTEXTS` | "the number of contexts currently open" |
| `WTI_STATUS` / `STA_SYSCTXS` | "the number of system contexts currently open" |
| `WTI_INTERFACE` / `IFC_NCONTEXTS` | "the number of contexts supported" |

### system-context.c

Opens the ordinary kind: a **system** context, which drives the cursor as well as delivering
packets. This is what WinPenKit's Wintab sessions ask for.

```c
#include "wtcount.h"

int main(void)
{
    if (!wtLoad()) return 1;

    HWND window = wtWindow("system-context");

    report("before opening");

    LOGCONTEXTA lc;
    if (!wtInfo(WTI_DEFSYSCTX, 0, &lc)) {
        printf("  the driver would not give its default system context\n");
        return 1;
    }

    lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
    wantEverything(&lc);

    HANDLE context = wtOpen(window, &lc, TRUE);
    if (!context) {
        printf("  WTOpenA returned NULL -- the driver refused\n");
        report("after the refusal");
        return 1;
    }

    report("with one open");

    wtClose(context);
    report("after closing it");
    return 0;
}
```

```
  before opening          open  12   system  12   (driver says it supports 32)
  with one open           open  14   system  14   (driver says it supports 32)
  after closing it        open  12   system  12   (driver says it supports 32)
```

### digitizer-context.c

The same, but a **digitising** context: tablet-native coordinates, no cursor control. The only
difference in the code is one line.

```c
    lc.lcOptions &= ~CXO_SYSTEM;
    lc.lcOptions |= CXO_MESSAGES;
```

```
  before opening          open  12   system  12   (driver says it supports 32)
  with one open           open  14   system  12   (driver says it supports 32)
  after closing it        open  12   system  12   (driver says it supports 32)
```

**One context is enough.** It had been suggested that a digitiser needs two — one digitising for
resolution and one system so the cursor still works. It does not: a single digitising context
opens on its own. Note that `STA_SYSCTXS` stays at 12 while `STA_CONTEXTS` rises, which is how the
two counters can be told apart.

### leak-context.c

Opens a system context and then calls `TerminateProcess` on itself — `exit()` would run the C
runtime's shutdown, and the point is to model a process that gets no shutdown at all.

```c
    report("with one open");
    printf("  about to be killed while holding it\n");
    fflush(stdout);

    TerminateProcess(GetCurrentProcess(), 0);
```

Run three times in a row, then a clean run afterwards:

```
  before opening          open  12   ...      with one open  open  14   [killed]
  before opening          open  14   ...      with one open  open  16   [killed]
  before opening          open  16   ...      with one open  open  18   [killed]

  before opening          open  18   system  18
  with one open           open  20   system  20
  after closing it        open  18   system  18
```

Each kill in these runs left two counted contexts behind. The clean run afterwards still
balanced. These observations establish a retained context, not infinite lifetime or a
driver-wide failure.

## What one context costs

**It depends on device selection.** The default virtual device (`lcDevice = UINT_MAX`)
produced two manager-enumerated contexts, one for device 0 and one for device 1, per successful
`WTOpenA`. An explicit device-0 or device-1 open produced one. Three opens in one process
confirmed that this is a per-open difference, rather than fixed process overhead.

A system context moves both counters; a digitising context moves only `STA_CONTEXTS`.
For the virtual device these increments were two; for an explicit device they were one.
`STA_CONTEXTS` matched the number of enumerated device contexts. It is not a count of application
sessions or successful `WTOpenA` calls. The two device IDs both report `WACOM Tablet`, although
`IFC_NDEVICES` reports one; the reason for that topology remains unknown.

There is no separate standard digitizing-context counter among the eight `WTI_STATUS` indices.
The total minus system count describes the remaining counted contexts. Manager enumeration
offers an additional per-context view: inspect `lcOptions & CXO_SYSTEM` with `WTGetA`.

## `IFC_NCONTEXTS` is not an enforced ceiling of 32

The Wintab specification calls it "the number of contexts supported". This driver reports **32**.
The independent recheck successfully opened beyond that value, including ordinary live contexts
with no leak. Earlier runs also reported successful opens at 54, 108, and 334.

A count above 32 proves neither a leak nor exhaustion. Conversely, exceeding 32 does **not** prove
that all other resource limits are absent. The September 14 recheck reached 510 counted contexts;
the next virtual open returned NULL and increased the count to 511. Closing successful opens
allowed new opens again. The failed open left one additional enumerated context, despite
returning no handle. This is a different failure from the historical frozen-counter report.
See the investigation record for the conditions and subsequent checks.

## The failure that started this, which is a different thing

Two applications stopped taking pen input entirely. `WTOpenA` returned NULL for every kind of
context — system, digitising, hi-res, screen-output, to every application on the machine — each
refusal taking about 90 ms. `STA_CONTEXTS` was **frozen at 253**: it did not move when
applications closed, and it did not move for the refused opens either.

Those observations establish that the tested opens stopped working. They do not identify why.
The counter alone does not show which resource, if any, was exhausted. The earlier statement
that high-count successes ruled out exhaustion generally was too strong: they rule out an
enforced ceiling of 32, not every resource limit or state-dependent failure.

**What cleared it**, in seconds and with no reboot, was restarting the tablet service — see
[Resetting the driver](#resetting-the-driver) below. The count went straight back to 2 and
everything worked. Killing and restarting the user-level `Wacom_TabletUser.exe` did **not** help;
the counts live in the service.

**Why the historical driver-wide refusal occurred is not known.** Its relation to leaked
contexts, the independently observed allocation limit, and IPC timeouts remains unproven.
The old 1074 observation has not been independently reproduced by the same opening sequence.

## What the specification says

Read September 14, 2026: [Wintab 1.4 specification](https://www.wacomeng.com/windows/docs/Wintab_v140.htm),
sections 4.2, 5.1, 5.5, 5.6 and tables 7.4-7.6.

- External manager support is optional; exported functions may return failure when unsupported.
- `WTMgrContextEnum` enumerates context handles. `WTMgrContextOwner` returns an **HWND**, not a
  process ID. `WTMgrDefContext` provides a read-only default context, not an orphan list.
- `WTClose` destroys a valid context. The counters describe open contexts and supported counts;
  the specification does not promise an unlimited context supply.
- `WTOpen` identifies its HWND as the owning window and message recipient. It does not document
  NULL as a supported headless input mechanism.

Wacom's current [shutdown guidance](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/)
also recommends `WacomCleanup()` after closing the application's contexts. That is not a documented
foreign-context collector. In our test, the caller subsequently read zero while a fresh process
still saw the original count; always cross-check post-cleanup readings independently.

## Reclaiming known contexts without a service restart

The independent plain-C probe in [Samples/ContextCount](../Samples/ContextCount/README.md)
demonstrates this on both x64 and x86:

1. Open a manager handle against a real, optionally hidden window.
2. Retain the Windows process handle for a test child and confirm its exit.
3. Enumerate contexts and identify only that child's deliberately named test contexts.
4. Recheck identity and `WTClose` each selected device context.
5. Verify a live sentinel with `WTGetA`, then close that sentinel normally.

Three leaked virtual opens generated six entries; closing those entries reduced the count by
one each. A live sentinel remained valid and its own close restored the original baseline.
This worked for system and digitizer contexts, killed children, ordinary process return without
`WTClose`, and window destruction before process exit. None needed a service restart.

**Do not turn `!IsWindow(owner)` into a cleanup button.** Our initial two built-in system
contexts had NULL owners. A destroyed window also does not prove its process has exited, and
Windows can reuse HWNDs. The sample therefore reclaims only its own identified child contexts.
Safe retrospective identification of arbitrary old orphans is not established. This result also
does not prove that manager cleanup cures the historical wedged state.

## Resetting the driver

The fix when the pen has stopped working. It takes seconds and does not need a reboot.

**1. Open PowerShell as administrator.** Press Start, type `powershell`, and choose *Run as
administrator*. The commands below will not work in an ordinary window — stopping a service needs
the elevation, and without it you get "cannot open service on computer".

**2. Find the tablet's service.** The name differs by vendor:

```powershell
Get-Service | Where-Object {
  $_.Name -match 'wacom|wtablet|huion|xp-?pen|gaomon|xencelabs|veikk|ugee|parblo|tablet|pentablet' -and
  $_.Name -ne 'TabletInputService'
} | Select-Object Status, Name, DisplayName
```

On a Wacom machine that finds one:

```
Status  Name               DisplayName
------  ----               -----------
Running WTabletServicePro  Wacom Professional Service
```

**3. Restart it by name:**

```powershell
Restart-Service WTabletServicePro -Force
```

The count drops back to its floor within a few seconds. Measured twice on the day this was
written: 253 to 2, and 334 to 2.

**Not `TabletInputService`.** That is Windows' own pen and touch service and has nothing to do with
Wintab. It is excluded from the search above deliberately.

Rebooting does the same thing, more slowly.

### Restarting it without a prompt every time

Stopping a service needs administrator rights, so every reset raises a UAC prompt. That is fine
once. It is a nuisance when a test run wants to reset the driver between cases, and it rules out
resetting it from anything unattended.

A service's permissions can be widened so that **one account may start and stop that one service**
and nothing else. No general elevation, no standing "run as administrator" anything: three rights
on one service.

Whether that is worth doing is a judgement about the machine. On a development machine with a
tablet, where the driver is reset several times a day, it removes a prompt that is otherwise
answered without reading it, which is its own small argument. On a shared or production machine,
leave it alone.

**To find out how a machine is currently configured**, run
[`Scripts/Test-TabletServiceAccess.ps1`](../Scripts/Test-TabletServiceAccess.ps1). It finds the
tablet service by itself, asks Windows whether this account may start and stop it, and prints both
commands if it may not:

```
service   WTabletServicePro
account   MACHINE\you
elevated  False

This account can start and stop the service with no prompt.
Restart-Service will work from an ordinary window:
    Restart-Service WTabletServicePro -Force
```

It asks the service control manager rather than reading the descriptor and interpreting it: the
service is opened for `SERVICE_START | SERVICE_STOP` and the answer is whether that succeeded.
Opening a service neither starts, stops nor changes it, so the script is safe to run at any time,
and it needs no elevation itself. Run it from an **ordinary** window: from an elevated one the
answer is yes whatever the permissions say, and the script says so rather than reporting a false
result.

**To grant it**, run the line the script prints, once, in an elevated PowerShell. It is the
descriptor the service already has with one entry appended:

```
sc.exe sdset WTabletServicePro "<the descriptor now>(A;;LCRPWP;;;<your SID>)"
```

`LCRPWP` is query status, start, stop. Not change-configuration, not change-permissions, not
delete. The worst that account can then do to the service is stop your own tablet.

**To put it back**, the script prints that line too — the descriptor as it was, with nothing
appended. Keep it. Reverting is not "remove the entry"; it is "set the descriptor back to this",
and the only reliable copy of *this* is the one taken before the change.

**It must be `sc.exe`, not `sc`.** In PowerShell `sc` is an alias for `Set-Content`, so the bare
name quietly does something else entirely.

**A driver update will probably undo it.** The installer recreates the service with a fresh
descriptor and the entry goes with it. If prompts come back one day that is why: run the script
again and reapply the line it prints.

Only Wacom was tested. The mechanism is not vendor-specific — it is a Windows service permission,
and the script takes any service name — but no other vendor's service has been tried.

### Applications running at the time do not survive it

Tested, because the answer matters and is not the comfortable one. A program was left holding a
context and re-checking it once a second with `WTGetA`, which returns FALSE for a handle the driver
no longer knows:

```
   9s  WTGetA TRUE    open  26   system  26
  10s  WTGetA FALSE   open   2   system   0     <- the service restarted here
  11s  WTGetA FALSE   open   2   system   2
  ...
  25s  WTGetA FALSE   open   2   system   2
```

The context is invalidated at the moment of the restart and **never becomes valid again**. Fifteen
further seconds changed nothing.

Worse, from the application's point of view nothing happened. A real application left running
across the restart stayed up, kept its status line reading "Wintab (high-res)", and wrote nothing
to its log — while its context was dead and the driver reported it gone. The pen simply stops
working, silently, which is the same misleading failure this whole document is about.

### WinPenKit now recovers by itself

Since that was measured, a Wintab session checks its own context and reopens it. `WTGetA` returns
false for a handle the driver no longer knows, so the check is cheap and unambiguous; it runs from
the drain the consumer already calls on its frame timer, paced to once a second.

An application left running across a service restart now does this on its own:

```
[22:02:17.125] Context 0x202 is no longer known to the driver -- it was taken away rather than
               closed, which is what restarting the tablet service does.
[22:02:21.643] BeforeOpen (HiRes): Options=0x00000005 Device=0 ...
[22:02:21.659] Could not reopen the context: Fallback context also failed to open.
[22:02:26.679] BeforeOpen (HiRes): Options=0x00008015 Device=4294967295 ...
[22:02:26.737] Context reopened; the pen should work again.
[22:02:26.738] Contexts after reopening: 4 open, of a stated maximum of 32
```

The failed attempt in the middle is worth keeping in view. **While the service is coming back the
driver answers with degenerate defaults** — `Options=0x5`, `Device=0`, where a healthy answer is
`0x8015` and `4294967295` — and a context opened from those is refused. A single retry would have
given up there. The interval backs off to five seconds after a failure, both to get past that
window and because a refusing driver takes about 90 ms to say so, which is not something to do
once a second on the thread that draws.

Restarting the application is therefore no longer necessary, though it remains the certain fix. An
application that never calls one of the drain methods will not recover, since that is where the
check runs.

## Other applications leak identically

Reported by the original investigation on the same machine; Clip Studio Paint and Krita were
not rerun in the September 14 independent C recheck:

| Application | On launch | Closed by its window | Killed |
|---|---|---|---|
| PenDynamicsPaint (WinPenKit) | +2 | returns | **leaks 2** |
| Clip Studio Paint | +2 | returns | **leaks 2** |
| Krita | +2 | returns | **leaks 2** |

The independent C reproduction also leaves entries without loading WinPenKit. That establishes
that WinPenKit is not required for the retention; the per-application figures above remain the
original report, not independent replications.

## Automated tests are the fastest way to leak contexts

This is the part most likely to matter to somebody writing a Wintab application, and it is not
obvious until it has already happened.

A test that builds a window and drops it costs a context every time. On this driver that is two
units left after exit in these runs, and a test suite runs far more often than a person opens an
application. On the machine this was written on, one afternoon looked like this:

| | contexts |
|---|---|
| after restarting the tablet service | **4** |
| two launches of the application, used and closed normally | +2 each |
| **forty-one test-suite runs over eight minutes** | 22 → **1074** |

Each run showed thirteen windows and closed four of them, so each run cost **26** units. Nothing in
that was unusual: a change was being checked by running the suite, which is what a suite is for.

Counted from the logs, 43 processes ran in that window: 41 test hosts, which opened **522**
contexts between them, and two launches of the application, which opened **two** and gave both
back. The application was not what filled the driver up. At 2 contexts a launch it would have taken
261 launches to do what the tests did in eight minutes.

### The null-window explanation was not reproduced

An earlier version asserted that `WTOpenA(NULL, ...)` succeeded and explained the test-suite
leaks. Independent x64 and x86 programs on September 14 instead received NULL, without any
counter increase, both with `CXO_MESSAGES` enabled and in polling configuration. Valid-window
controls succeeded. The unqualified null-window claim is withdrawn.

A hidden real HWND and a NULL HWND are different cases. In
[`WintabSessionBase.Start`](../WinPenKit/Wintab/WintabSessionBase.cs), the application HWND sets
capture scope. The method creates a [`WintabMessagePump`](../WinPenKit/Wintab/WintabMessagePump.cs)
and passes `_pump.Hwnd` to `OpenContext`; the pump creates a real hidden top-level Win32 window.
Consequently `session.Start(IntPtr.Zero)` can allocate a context in a headless test without ever
calling `WTOpenA(NULL, ...)`. This source path explains why headless tests are not automatically
isolated from the physical driver.

The earlier prose also alternated between successful opens at 1074 and
a global refusal there; that historical state was not captured sufficiently to resolve the
conflict. Neither assertion should be used as a reproduction recipe.

### What to do about it

**Close the windows your tests open.** A window that is merely constructed costs nothing; the
context is taken when it is shown, and given back when it closes. So the rule is only about shown
windows, and it is worth enforcing in the harness rather than at each call site — the test written
next is the one that will forget.

The shape that works: have tests take their windows from something that remembers them, and close
whatever is outstanding when the test body ends, passed or failed. In this repository's own
application that is thirty-odd lines in the test host and one `finally`.

**Two things are worth asserting**, and they are different: that a tracked window really closes,
and that something actually calls the close. The second cannot be checked from inside a test body,
because it happens afterwards — make the window in one dispatch and look at it in the next.

### How to measure whether it worked

Do not take it on trust. Any application built on WinPenKit writes a per-process log to the
temporary folder with a reading before and after every context it opens (see
[What WinPenKit writes to its log](#what-winpenkit-writes-to-its-log)), so the cost of a run is
already recorded.

**Compare the first reading of consecutive runs**, not the first and last reading within one run.
The last line of a run is written just before its final window closes, so a run whose true cost is
zero still shows a residue of two.

```powershell
Get-ChildItem $env:TEMP\WinPenKit.*.log |
  Sort-Object LastWriteTime |
  ForEach-Object {
    $lines = Select-String 'Contexts (before|after) opening: (\d+)' $_.FullName
    if ($lines) {
      [pscustomobject]@{
        Log   = $_.Name
        Opens = ($lines | Where-Object { $_ -match 'after' }).Count
        First = [int]($lines[0].Matches.Groups[2].Value)
        Last  = [int]($lines[-1].Matches.Groups[2].Value)
      }
    }
  } | Format-Table -AutoSize
```

Run the suite twice and read the `First` column of the two runs. Equal means the runs cost nothing.
Rising by a constant means that is what each run leaks.

Measured here, three runs each way on the same machine and driver:

| | first reading, three consecutive runs |
|---|---|
| before closing the windows | 4, 30, 56 — **+26 a run** |
| after closing the windows | 56, 56, 56 — **no cost** |

### It does not fix the leak

Nothing above changes what the driver does. A context that is not closed is still not returned, a
killed process still leaks whatever it was holding, and the counts still climb for every
application on the machine that misbehaves. This only stops your own tests being the thing that
drives the number up — which, on a machine where the suite runs dozens of times a day, they
otherwise will be, faster than any human use of the application.

**A build machine with no tablet is unaffected.** No Wintab driver means no context to take, so
this costs nothing where there is nothing to leak. It matters exactly on the developer machine
with the tablet attached, which is also the machine where losing the pen hurts most.

### Some leaked contexts came back

Worth recording because it contradicts the simple story. After the runs above the count stood at
1074. Nine minutes later, with those processes long exited and no service restart and nothing else
done, it read **538** — almost exactly half of them had been returned.

This was observed once and the cause was not captured. It is not evidence of a guaranteed timer
or reclamation policy. A virtual context expanding into two device contexts provides a testable
alternative hypothesis (one device's entries disappearing), but has not established what happened.
See the independent timed observation in [the investigation record](WINTAB-INVESTIGATION-2026-09.md).

## What WinPenKit does, and what it does not cause

There is exactly one `WTOpenA` and one `WTClose` in the assembly. A session opens one context and
`Stop()` closes it; `Dispose()` calls `Stop()`. A cleanly closed session always gives its context
back.

**Switching pen API does not leak**, so there is no need to look there again. Checked at two
levels:

- All nine ordered pairs among `WintabDigitizer`, `WintabSystem` and `WmPointer` through
  `PenSessionFactory` — 24 switches, count returned to baseline every time.
- All six ordered pairs among Wintab, Wintab (high-res) and Avalonia Pointer through a real
  application's own settings dialog — 12 switches, likewise.

Nor does history matter. A process killed after forty switches leaks exactly what a process killed
immediately leaks: whatever it held at the moment it died, and nothing more.

## What WinPenKit writes to its log

**One file per process**, `%TEMP%\WinPenKit.<pid>.log`, named so that two pen applications running
at once both get one — which is exactly the situation diagnosing a driver puts you in. Files older
than a week are removed when a new one is created.

Every Wintab session brackets itself with the driver's context counters:

```
[21:39:34.684] Contexts before opening: 18 open, of a stated maximum of 32
[21:39:34.925] Contexts after opening: 20 open, of a stated maximum of 32
[21:39:42.523] Contexts after closing: 18 open, of a stated maximum of 32
```

That is a run that closed properly. This is a run that was killed:

```
[21:39:49.389] Contexts before opening: 18 open, of a stated maximum of 32
[21:39:49.614] Contexts after opening: 20 open, of a stated maximum of 32
```

**The missing third line is the whole signal.** And the next run says what that cost:

```
[21:40:01.446] Contexts before opening: 20 open, of a stated maximum of 32
```

The first line records all counted contexts when the process starts, including live contexts and
driver defaults. It cannot by itself identify leaks or their owner. Consecutive baselines can
show retention when other applications and driver state are controlled.

The first line of each file carries the date and the application, which the per-line timestamps do
not:

```
[21:44:29.193] Log start: PenDynamicsPaint pid 54472, 2026-09-13
```

It is an ordinary log line rather than a banner, so anything reading the file line by line needs no
special case for it.

Two faults were found here while adding this, both of which had been quietly costing information:

- **A second application could not log at all.** With one shared file, the first process holds it,
  the second's writer throws, and the exception went somewhere nobody reads. Running two pen
  applications logged one of them and said nothing about the other.
- **Changing pen API wiped the log.** Disposing a session closed the writer, and the next write
  reopened the file — which truncates. Everything logged before the switch was lost, which is why
  the log never seemed to show more than one session.

`WintabDiagnostics.LogPath` names this process's file, for an application that wants to point at
it in a bug report.

## A window that shows the number

[**WinTabUtils**](https://github.com/TheSevenPens/WinTabUtils) has a small application whose whole
job is to display the count, with a button to restart the Wacom driver beside it. Leave it open,
kill a drawing application, and the number goes up and stays up.

![the tool](images/wintab-contexts.png)

It lives in its own repository rather than here, because it is for somebody whose pen has stopped
working rather than for somebody writing an application, and a shared release list would sooner or
later have had one downloading the other. Its releases are a single self-contained executable.

It opens no contexts of its own: every call is `WTInfoA`, which only reads.

## Checking a machine you do not own

Anyone can read the counters without installing anything. Paste this into PowerShell — no
administrator rights, nothing downloaded, and it only reads:

```powershell
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WT {
  [DllImport("Wintab32.dll", CharSet=CharSet.Ansi)]
  public static extern uint WTInfoA(uint c, uint i, System.Text.StringBuilder o);
  [DllImport("Wintab32.dll")]
  public static extern uint WTInfoA(uint c, uint i, out uint o);
}
'@
function N($c,$i){ $v=0; if([WT]::WTInfoA($c,$i,[ref]$v) -eq 0){ "not reported" } else { $v } }
function S($c,$i){ $b=New-Object Text.StringBuilder 256; if([WT]::WTInfoA($c,$i,$b) -eq 0){"not reported"}else{$b.ToString()} }
try {
  $v = (Get-Item "$env:SystemRoot\System32\wintab32.dll" -ErrorAction SilentlyContinue).VersionInfo
  $hw = (Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
           $_.Status -eq 'OK' -and
           $_.FriendlyName -match 'wacom|huion|xp-?pen|gaomon|xencelabs|veikk|ugee|parblo|tablet|pen display' -and
           $_.FriendlyName -notmatch 'billboard|monitor|helper|update|component|pointer|composite|hub|audio'
         } | Select-Object -ExpandProperty FriendlyName -Unique) -join ', '
  "wintab   : {0}   (wintab32.dll {1}, {2})" -f (S 1 1), $v.FileVersion, $v.CompanyName
  "tablet   : {0}" -f $(if ($hw) { $hw } else { S 100 1 })
  "contexts : {0} open, {1} system, driver claims a maximum of {2}" -f (N 2 1), (N 2 2), (N 1 6)
  "uptime   : {0:0.0} hours" -f ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalHours
} catch [EntryPointNotFoundException] {
  "No Wintab on this machine - the tablet driver either is not installed or does not provide it."
} catch {
  if ($_.Exception.InnerException -is [DllNotFoundException] -or $_.Exception -is [DllNotFoundException]) {
    "No Wintab on this machine - the tablet driver either is not installed or does not provide it."
  } else { "Could not read Wintab: $($_.Exception.Message)" }
}
```

It prints five lines:

```
wintab   : Wintab Digitizer Services   (wintab32.dll 1.0.5-10, Wacom Co. Ltd.)
tablet   : Wacom Cintiq 24 touch, Wacom Tablet
contexts : 22 open, 22 system, driver claims a maximum of 32
uptime   : 55.7 hours
```

Tested on Windows PowerShell 5.1 and PowerShell 7, by extracting the block from this file and
running it, so what is printed here is what the published copy does.

The first two lines are the ones that matter for collecting reports from other people. The Wintab
implementation name and `wintab32.dll`'s company identify **whose** Wintab this is — a Huion
machine has Huion's DLL under the same filename — and the tablet line names the actual model,
which `WTI_DEVICES` alone does not: it answers "WACOM Tablet" whatever is plugged in.

The uptime is there because a count means nothing without it: twenty after three weeks is
unremarkable, twenty after two hours is not.

**It asks for Wintab, not for Wacom.** `Wintab32.dll` is the standard entry point and every vendor
installs their own implementation under that one filename, so the `DllImport` resolves to whatever
is on the machine — checked here by asking the running process which file it loaded:

```
ModuleName  : Wintab32.dll
FileName    : C:\Windows\SYSTEM32\Wintab32.dll
FileVersion : 1.0.5-10
Company     : Wacom Co. Ltd.
```

On a Huion machine that same line loads Huion's DLL, which is exactly why the company name is
worth printing. **Untested against any other vendor**, though, like everything else here.

The one part that does name vendors is the tablet-model lookup, which searches the device list for
a handful of known makers or the words "tablet" and "pen display". A vendor not in that list falls
back to the name Wintab itself gives, so an unrecognised tablet still reports — just less
precisely.

**"not reported" is not the same as zero.** A driver that does not implement one of these counters
returns no bytes at all, and the script says so rather than printing 0 — which matters most for
the vendors nothing has been tested against, where a silent 0 would read as "does not leak".

### Testing whether a particular driver leaks

The count includes both live and retained contexts. With other applications and driver state
held constant, this lifecycle comparison tests whether contexts survive a killed owner:

1. Run the script. Note the `contexts` number.
2. Open a drawing application, then **close it normally**. Run the script again.
   The number should be back where it started.
3. Open it again, then **End Task** it from Task Manager. Run the script a third time.

If the third number is higher than the first, that driver leaks contexts from killed processes.
On Wacom 6.4.14-1 it goes up by two and stays up.

What is worth reporting back: all five lines from step 1, and the three `contexts` numbers.

## How to check a machine

Read the two counters — `WintabDiagnostics.ContextTable()` does it from C#, and the sample
programs do it from C. A machine that has been used normally sits in the low single figures. A
machine in the middle of a development session does not.

WinPenKit puts both numbers into the message it returns when a context will not open, so a
refusal now says what the driver thinks of itself rather than only that it said no.

## Advice

- **Close pen applications by their window.** `Stop-Process`, Task Manager's End Task, and a
  debugger's stop button all leak.
- **When the pen stops working, [restart the tablet service](#resetting-the-driver) before
  suspecting your own code.** The symptom is indistinguishable from a broken renderer, which is
  what cost two sessions here.
- **Close the windows your tests open**, in the harness rather than test by test. A suite that
  drops shown windows will leak faster than any human use of the application, and it is the
  developer machine with the tablet on it that pays. See
  [Automated tests are the fastest way to leak contexts](#automated-tests-are-the-fastest-way-to-leak-contexts).
- **If you reset the driver often, check whether this machine needs a prompt for it** with
  `Scripts/Test-TabletServiceAccess.ps1`. One service permission removes the prompt without
  granting anything else: see
  [Restarting it without a prompt every time](#restarting-it-without-a-prompt-every-time).
- Do not treat the counters as a capacity check.

## Not established

- Whether any other vendor's driver behaves this way. Nothing but Wacom was tested.
- What actually wedges the driver.
- Whether the leak contributes to the wedge at all.
- Whether logging off reclaims leaked contexts. Manager closure of known test contexts is
  established; safe automatic identification of arbitrary old orphans is not.
- Why about half of a large number of leaked contexts came back on their own, minutes after the
  processes holding them had exited, when every deliberate measurement here says a leaked context
  stays leaked. Seen once, at 1074 falling to 538. Not reproduced on purpose.
- Whether a context leaked by one user's process is visible to another user's session.
- Whether any configuration on this driver accepts a null `HWND`. Independent x64/x86 attempts
  with default virtual-device settings were refused; the original successful-open claim was
  not reproduced.
- Why two enumerated device IDs coexist with `IFC_NDEVICES=1`, and whether the topology changes
  after hardware, driver, or user-session changes.
- The exact resource and scope of the allocation limit observed near 510 counted contexts,
  and its relation, if any, to the historical 253/90 ms refusal.
- Whether older or newer Wacom driver versions differ. One version was tested.

## Related public reports

These reports need to be distinguished from this investigation:

- Blender [#111152](https://projects.blender.org/blender/blender/issues/111152), "Wintab rarely
  gets into a bad state and prevents Blender startup". Theirs crashes inside `WTOpenA` rather than
  returning NULL. The workaround offered is to switch to Windows Ink.
- Godot [#38533](https://github.com/godotengine/godot/issues/38533) was **closed** by
  [#38535](https://github.com/godotengine/godot/pull/38535) in May 2020. The discussion confirms
  no crash; the patch makes a failure message verbose-only when a driver cannot open a tablet.
  It does not report measured context leakage or establish a wedged Wacom service. The previous
  description of it as unresolved supporting evidence was incorrect (rechecked September 14, 2026).

Restarting the tablet service is folk knowledge across support pages for
[Maxon](https://support.maxon.net/hc/en-us/articles/7945504326044-WinTab-Service-Not-Available)
and ZBrush, always as a cure and never with a cause attached.
