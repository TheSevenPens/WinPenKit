# Wintab contexts are leaked by processes that do not close them

A Wintab context opened by a process that then dies — killed from Task Manager, crashed, or
stopped from a debugger — is never given back. The driver goes on counting it as open for as long
as it is running.

This matters to whoever is **developing** a pen application rather than using one, because
stopping a debugger is how a developer ends a process fifty times a day. It is invisible until
something goes wrong, and when something does go wrong it looks like a bug in the application:
the pen moves, the canvas stays empty, and nothing says the samples never arrived.

Everything below was measured. Where a thing was **not** established, it says so.

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

Each kill costs two and never gives them back. The clean run afterwards still balances, so the
driver is not broken by this — it is just counting contexts that no longer have an owner.

## What one context costs

**Two**, on this driver. A system context moves both counters by two; a digitising context moves
`STA_CONTEXTS` by two and `STA_SYSCTXS` by nothing. Whatever the unit is, it is not "one context",
so do not read `STA_CONTEXTS` as a number of contexts.

## `IFC_NCONTEXTS` is not a ceiling

The Wintab specification calls it "the number of contexts supported". This driver reports **32**
and does not enforce it. Contexts were leaked deliberately past that figure — 32, 54, 108, 334 —
and **every single open still succeeded**, including from the real application.

This is worth stating plainly because it is an easy and wrong inference to draw. A count above the
stated maximum means contexts have been leaked. It does not mean the driver has run out, and it is
not a reason for anything.

## The failure that started this, which is a different thing

Two applications stopped taking pen input entirely. `WTOpenA` returned NULL for every kind of
context — system, digitising, hi-res, screen-output, to every application on the machine — each
refusal taking about 90 ms. `STA_CONTEXTS` was **frozen at 253**: it did not move when
applications closed, and it did not move for the refused opens either.

That is a driver that has stopped working, not a driver that has run out. The 253 was a symptom
sitting next to the fault, and reading it as the cause was wrong — a mistake made twice here
before the exhaustion test above ruled it out.

**What cleared it**, in seconds and with no reboot, was restarting the tablet service — see
[Resetting the driver](#resetting-the-driver) below. The count went straight back to 2 and
everything worked. Killing and restarting the user-level `Wacom_TabletUser.exe` did **not** help;
the counts live in the service.

**Why the driver wedges is not known.** It had been running for two days and had accumulated a
great many leaked contexts, so the leak is a plausible contributor, but it is not proven and the
exhaustion test argues against a simple "ran out of slots" explanation.

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

**So: restart the application after restarting the service.** Opening a *new* context afterwards
works normally — the count was back to a healthy 2 and fresh opens succeeded — so anything that
reopens its session recovers. An application that never reopens one will not.

This is worth fixing in a pen library rather than documenting: a session could notice that its own
context has gone and say so, or reopen it. Filed as part of the follow-up work.

## Other applications leak identically

Measured the same way, on the same machine, in the same session:

| Application | On launch | Closed by its window | Killed |
|---|---|---|---|
| PenDynamicsPaint (WinPenKit) | +2 | returns | **leaks 2** |
| Clip Studio Paint | +2 | returns | **leaks 2** |
| Krita | +2 | returns | **leaks 2** |

Clip Studio and Krita are long-established painting applications and neither goes anywhere near
WinPenKit. This is the driver's behaviour, not any application's.

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

So the first line of any log answers "how many contexts had already been leaked when this process
started", which is the question that identifies a leaking application without needing to have been
watching at the time.

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

The count alone says how much has accumulated. This says whether the driver is the cause:

1. Run the script. Note the `contexts` number.
2. Open a drawing application, then **close it normally**. Run the script again.
   The number should be back where it started.
3. Open it again, then **End Task** it from Task Manager. Run the script a third time.

If the third number is higher than the first, that driver leaks contexts from killed processes.
On Wacom 6.4.14-1 it goes up by two and stays up.

What is worth reporting back: all four lines from step 1, and the three `contexts` numbers.

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
- Do not treat the counters as a capacity check.

## Not established

- Whether any other vendor's driver behaves this way. Nothing but Wacom was tested.
- What actually wedges the driver.
- Whether the leak contributes to the wedge at all.
- Whether logging off, or any lighter action than restarting the service, reclaims leaked
  contexts.
- Whether a context leaked by one user's process is visible to another user's session.
- Whether older or newer Wacom driver versions differ. One version was tested.

## Related public reports

Neither is the same fault, but both are the same shape — a Wintab driver that has stopped
behaving, with no cause identified:

- Blender [#111152](https://projects.blender.org/blender/blender/issues/111152), "Wintab rarely
  gets into a bad state and prevents Blender startup". Theirs crashes inside `WTOpenA` rather than
  returning NULL. The workaround offered is to switch to Windows Ink.
- Godot [#38533](https://github.com/godotengine/godot/issues/38533), "WinTab context creation
  failed". Unresolved.

Restarting the tablet service is folk knowledge across support pages for
[Maxon](https://support.maxon.net/hc/en-us/articles/7945504326044-WinTab-Service-Not-Available)
and ZBrush, always as a cure and never with a cause attached.
