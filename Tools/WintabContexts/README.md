# Wintab contexts

A window with one big number in it: how many Wintab contexts the driver currently has open.

![the tool](../../Docs/images/wintab-contexts.png)

It exists to make a leak visible. Leave it open, kill a drawing application, and the number goes up
and stays up. Close one properly and the number comes back down. That is the whole of
[the context leak](../../Docs/WINTAB-CONTEXT-LEAK.md), on screen, without a debugger.

## Building it

**Deliberately not in `WinPenKit.slnx`, and not in any release.** CI builds the solution and then
stages a named list of projects; this is in neither, so it ships with nothing. Build it by hand:

```
dotnet run --project Tools/WintabContexts
```

## What it shows

| | |
|---|---|
| **contexts open** | every context open on the machine, whoever opened it |
| **system contexts** | how many of those drive the cursor as well as delivering packets |
| digitising contexts | said in words, not set as a number, because it is **inferred**: Wintab has no counter for these, so it is the two numbers subtracted |
| driver maximum | what the driver says it supports, and the constant it came from. It does not enforce it |
| vendor | the company recorded in `wintab32.dll` — whose driver is answering |
| wintab32.dll | that file's version |
| implementation | the name Wintab gives itself |
| spec / impl | the API version it claims, and its own |
| devices | every device the driver lists, by the name it gives each |

The number starts blank. Until Refresh is pressed nothing has been asked, and a figure whose age
is unknown is worse than no figure. **every second** keeps it current, which is what to use while
demonstrating.

**Nothing here opens a context.** Every call is `WTInfoA`, which only reads. A tool for counting
contexts that took one of its own would be adding to the number it reports.

## Restarting the driver

Clears leaked contexts in a few seconds, without a reboot. Windows asks for administrator rights
for that, so there is a prompt; declining it is fine and the tool says so.

**Wacom only.** The service name differs by vendor and only Wacom's has been tested, so the button
disables itself with an explanation on any machine that does not have it, rather than guessing at
a name and restarting something else.

Applications that were running at the time lose the contexts they were holding. WinPenKit-based
ones notice and reopen within a few seconds; others generally need restarting.

## What it works with

Any vendor, for the reading. `Wintab32.dll` is the standard entry point and every vendor installs
their implementation under that one filename, so the counts and the vendor line come from whoever
is actually installed.

That said — **only a Wacom driver has been tested.** Huion, XP-Pen, Gaomon, Xencelabs and the rest
are unexamined, and a driver that does not implement a counter reports "not reported" rather than
a misleading zero.
