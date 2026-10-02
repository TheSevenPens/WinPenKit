# Timestamps: measurements and history

How `PenPoint.TimestampMicroseconds` was measured on each backend, what the measurements
corrected, and how wrapping counters are handled. The rules for using the value are in
[HOW_TO_USE.md](HOW_TO_USE.md#what-timestampmicroseconds-is-for). The conversion code and the
per-backend source fields are in [ARCHITECTURE.md](ARCHITECTURE.md#timestamps-per-backend).

## Measured resolution, and why injection could not measure it

**Every backend in this table has now been drawn on by hand**, on a Wacom DTH246, 13 Sep 2026.
No injected figures remain.

| backend | points | distinct timestamps | step | one stamp per point? |
| --- | --- | --- | --- | --- |
| **WM_POINTER (WinForms)** | 2070 | 2070 | **1 µs** | yes |
| **WinUI 3** | 1878 | 1878 | **1 µs** | yes |
| **Avalonia** | 2167 | 2167 | 1 ms | yes |
| **Wintab (high-res)** | 1683 | 1683 | 1 ms | yes |
| **WPF Stylus** | 2442 | 885 | 1 ms clock, 15.6 ms batches | **no**: ~3 points share one |
| **Qt (`Scribble.Qt`, not WinPenKit)** | 2280 | 810 | **15.6 ms** | **no**: coarse clock repeats |

Five of these six had an injected figure to compare against; Wintab never did, because it
ignores injected input entirely. **Four of those five were wrong.** Only Qt's survived. That is
the headline finding of this whole exercise, and it is a fact about the instrument rather than
about any backend: measuring a clock through `InjectSyntheticPointerInput` mostly produces the
injector's properties.

Avalonia was the last measured and corrected its figure in a different direction from the rest.
Injection gave 172 points carrying 113 distinct timestamps, which reads as a clock too coarse to
separate consecutive points. On hardware there are **no repeats at all**: 2167 points, 2167
timestamps, 2166 gaps and not one of them zero. Its 1 ms resolution is real, but that comes from
the source type rather than from the recording: `PointerEventArgs.Timestamp` is a `ulong` count
of milliseconds. Recorded in `testdata/avalonia-hardware-stroke.csv`.

The Wintab row was measured on a Wacom DTH246 over the hi-res digitizer context, 13 Sep 2026.
**Every one of 1683 points carried its own timestamp**, with no repeats and no backward steps.
Gaps were 5 ms or 6 ms and nothing else, their greatest common divisor exactly 1000 µs,
averaging 5555 µs: a **180 Hz** device reported on a millisecond clock, which is why it
alternates rather than landing on 5.556 every time.

It is also the one row synthetic injection did not shape, because Wintab ignores injected input
entirely. Recorded in `testdata/winuinative-wintab-hires-stroke.csv`.

Qt is in the table because `Scribble.Qt` exists to be compared against, not because WinPenKit
produces it. It is the one backend whose injected figure was confirmed on hardware: across
809 gaps the **smallest is 15 ms**, with 504 of 16 ms and 303 of 15 ms. Nothing finer occurs at
all. `QInputEvent::timestamp` is the coarsest clock in the table, which is worth knowing before
treating Qt as the reference implementation.

**A greatest common divisor is evidence of resolution only when the smallest gap is near it.**
The Qt recording has a gcd of 1000 µs and no gap under 15 ms, because `gcd(15000, 16000)` is
1000: alternating between the two ticks of a 15.625 ms timer produces that number
arithmetically. On the WM_POINTER and WinUI recordings the same statistic meant something,
because gaps that small occurred. Quote the minimum alongside the gcd, or the statistic will
report a resolution the clock does not have.

**Injection was setting the floor it appeared to measure, and this is the proof.**

Under injection, WM_POINTER's `PerformanceCount` arrived as exact millisecond multiples and
matched `dwTime` one for one; this page recorded 1 ms and warned the figure was an upper bound.
Drawn on by hand, the greatest common divisor of all 2069 gaps is **1 µs**, every one of 2070
points carries a distinct timestamp, and consecutive gaps read 5001, 4943, 4999, 4946. It is
the finest clock of any backend here.

WinUI told the same story. Under injection every reading ended in the same sub-millisecond
remainder (171 µs in one run, 622 µs in another), which is what a millisecond clock with a fixed
offset looks like. On hardware the gcd is **1 µs** across 1877 gaps with 1878 distinct
timestamps. The constant tail was the injector's, not WinUI's.

The general lesson is worth more than either number: `InjectSyntheticPointerInput` stamps its
own events, so a backend cannot be shown to resolve finer than the thing feeding it. A
measurement taken through it can only ever bound a clock from above. Recorded in
`testdata/wmpointer-hardware-stroke.csv` and `testdata/winui-hardware-stroke.csv`.

One thing all six hardware recordings agree on, and the reason to trust them: a **180 Hz**
device. The four per-point backends read it directly, as gaps averaging 5559 to 5560 µs. WPF and
Qt cannot, because their timestamps step by the timer tick, but dividing points by elapsed span
gives 180.1 Hz and 179.9 Hz. Six unrelated code paths: the native C ABI, WinForms, WinUI,
Avalonia, WPF, and Qt's own stack.

## WPF is different in kind, not degree, and its clock was never the problem

WPF's `StylusEventArgs` carries a whole `StylusPointCollection`, and the timestamp belongs to
the **event**, not the point. Every point in a batch gets the same one. Drawn on by hand: 2442
points, **885 distinct timestamps**, two to four points per value and three most of the time.

The hardware recording separates two things this page used to run together. **The clock is a
millisecond clock.** Sixteen gaps of exactly 1000 µs appear, spread through the stroke rather
than bunched at its start, so `StylusEventArgs.Timestamp` does express a millisecond when it is
given the chance. What steps by 15.6 ms is the **delivery**: 531 gaps of 16 ms and 329 of 15 ms,
which is the Windows timer tick, not a property of the clock. The old 15.6 ms figure described
the batch cadence and was attributed to the clock.

That distinction matters because it says which effect a better clock would remove: none of it.
The batching is the whole of what reaches a caller, and WPF exposes no per-point time at all, so
this is a limit of the framework rather than a choice made here.

Qt reaches a similar-looking number (2280 points, 810 timestamps) by the opposite route, and
the two should not be run together. `QTabletEvent` is a `QSinglePointEvent`, so those 2280
points are 2280 separate events, each with its own timestamp. They repeat because the *clock*
only advances on the 15.6 ms timer tick. WPF has a fine clock and coarse delivery; Qt has fine
delivery and a coarse clock. A finer clock would fix Qt and would do nothing for WPF.

Recorded in `testdata/wpf-hardware-stroke.csv` and `testdata/qt-hardware-stroke.csv`.

Avalonia sits with the per-point group rather than with these two, and the intermediate-point
recovery added in #110 did not change that on the run measured: `GetIntermediatePoints` returned
a single point every time, so nothing was coalesced and nothing shared a timestamp. That path
produces several points per timestamp when the application falls behind, because every point
recovered from one event takes that event's timestamp.

## Two more things worth stating

- **`dwTime` and `PerformanceCount` are not two readings of one clock.** Both are populated on
  every `POINTER_INFO`. `dwTime` is milliseconds on the `GetTickCount64` epoch, `PerformanceCount`
  is QPC, and they sat 27.08 ms apart, identically, across every sample. These backends use
  `PerformanceCount`.
- **Sampling rate is now established; latency still is not.** While every figure here came from
  injection, the gaps were the injection script's and said nothing about a device. The six
  hardware recordings do measure the device: 180 Hz, agreed on by all six. They still say nothing
  about latency, which is the delay between the pen touching glass and the point reaching your
  handler, and no recording of timestamps alone can measure it.

## Reading it as wall-clock time

You cannot, through the API. The origin is unspecified on purpose, because it differs per
backend and only one machine has been measured.

If you need wall clock anyway (lining a stroke up against a log, say), calibrate it yourself.
At the moment a point arrives, read the wall clock too:

```csharp
long offsetUs = long.MaxValue;   // keep the smallest seen

// in your point handler, per point:
long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
offsetUs = Math.Min(offsetUs, nowUs - pt.TimestampMicroseconds);

// then, for any point:
DateTimeOffset wall = DateTimeOffset.FromUnixTimeMilliseconds(
    (pt.TimestampMicroseconds + offsetUs) / 1000);
```

The **minimum** matters. An event happens at T and your handler runs at T + latency, so every
sample overestimates the offset by that run's latency and never underestimates it. The smallest
difference over many points is the closest you get to the true offset. Measured handler latency
in one run was 0.372 ms to 26.6 ms, the largest on the first point after startup; calibrating
on a single sample would have been 26 ms out.

Recalibrate per session. The offset is not valid across a backend switch. On WPF the clock
being read is a millisecond clock, so the calibration is bounded by that rather than by the
15.6 ms delivery cadence; on Qt the 15.6 ms *is* the clock, and no calibration improves on it.

## Wintab's pkTime: granularity and origin

Wintab's `pkTime` is requested on every packet (`lcPktData` is `PK_PKTBITS_ALL`), and Wintab
documents it as milliseconds with no origin. Its **granularity is now measured** at 1 ms, with
one timestamp per point and no repeats, which makes it the most usable clock of any backend
here.

Its **origin is now measured too**: it is the `GetTickCount64` epoch. No stroke recording could
have answered that, because `StrokeRecorder` writes times relative to the first point by design
and so carries no machine uptime. It took a separate probe reading raw `pkTime` against the
system clock (`WintabEpochProbe`, run with `--probe-wintab-epoch`). Over 6217 packets spanning
41.7 s, `pkTime` advanced 41703 ms against 41703 ms of wall clock, with the offset between them
staying inside a 40 ms band. The run contained a deliberate five-second pause: a counter
advancing only while packets arrived would have fallen five seconds behind across it.

That is why **this backend now anchors** rather than detecting a backward jump, like the other
millisecond backends. Readings are in `testdata/wintab-epoch-probe.csv`, and
`--verify-wintab-anchoring testdata/wintab-epoch-probe.csv` replays them through the
conversion. Absolute values are still not part of the contract: the origin is documented, not
promised.

## Counters that wrap

The width of a property is not the width of the clock behind it:

| backend | property type | what is underneath | wraps after | handled |
| --- | --- | --- | --- | --- |
| WPF | `int` ms | 32-bit tick count | ~24.9 days (the raw value passes `int.MaxValue` and continues negative) | anchored in the session |
| Wintab | `uint` ms | `pkTime` | ~49.7 days (the raw value returns to 0) | anchored in the session |
| Avalonia | `ulong` ms | `GetMessageTime`, 32 bits, widened | ~49.7 days | anchored in the session |
| Qt (`Scribble.Qt`) | `quint64` ms | `GetMessageTime`, 32 bits, widened | ~49.7 days | anchored in the sample |
| WinUI | `ulong` µs | not established | not established | **not anchored** |
| WM_POINTER, WinForms | `ulong` QPC ticks | 64-bit counter | not in any relevant time | not needed |

Left alone, a stroke drawn across a 32-bit boundary would produce a difference wrong by the
entire range: about −49.7 days, from two points a millisecond apart. The anchored backends
extend the value inside the session, so `TimestampMicroseconds` stays continuous and positive
and the caller never sees the wrap.

**Anchoring rather than detection.** Anchoring derives the wrap count from the reading itself:
the true value is the nearest multiple of 2³² ms that agrees with the system clock. It carries
no state between packets, so an idle session, a first packet after a wrap, and a session
restarted across one all come back correct. It became available to Wintab only once its epoch
was measured.

Detection, a backward step of more than half the range, was what Wintab used while its origin
was unknown, and it has a blind spot that anchoring does not: it sees only the packets it is
given. A wrap that happened while the session was stopped, or across a gap where the capture
region discarded every packet, was missed, and the difference across that gap was wrong by
49.7 days. That gap is now closed.

**WinUI is the exception.** Its `PointerPoint.Timestamp` is already microseconds and is cast
without anchoring. Anchoring needs the source's width and epoch, and whether this value comes
from a 32-bit millisecond clock was not traced. If it does, it wraps after about 49.7 days of
uptime like Avalonia's and Qt's. Anchoring a clock that is 64-bit and on another epoch would
corrupt every reading, so it is left unanchored until that is measured.

Neither wrap can be reached by ordinary testing, so there is a check that does not need to
wait for one:

```bash
dotnet run --project WinPenKit.TestConsole -- --selftest-clock
```

Fifteen cases, no tablet and no window. Two are the wraps themselves. Five exercise the epoch
probe's analysis against synthetic readings shaped like a foreign epoch, a counter that stalls
when idle, and a clock the tick count lags. The probe needs a tablet and a person, so its
verdict would otherwise only ever have been produced once, on one machine, with no evidence it
could produce the other answer.

Verified in both directions, which is the only claim worth making about a suite like this. With
the extension removed, the two wrap cases fail by exactly −4,294,967,295,000 µs. With the epoch
analysis reverted to the unsigned subtraction it originally used, `probe/tick-lags-pktime` fails
with the same `2/3 passed` the first real hardware run produced.

Four cases covering the backward-jump wrap detector were removed with the detector. No backend
uses that approach now, and a suite that tests code no caller reaches reports a pass it cannot
support.
