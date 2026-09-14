# Counting Wintab contexts

Three small C programs that report the Wintab driver's own context counters, do one thing, and
report them again. They exist to demonstrate, without a framework or a language runtime in the
way, that **a process which dies without calling `WTClose` never gives its context back**.

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
