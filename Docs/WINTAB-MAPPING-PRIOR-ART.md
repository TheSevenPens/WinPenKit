# WinTab position mapping: how other open-source apps do it

This was researched on 2026-09-28 for issue #129. The source was read through `gh api` (GitHub), the GitLab API (invent.kde.org), the Jira REST API (Qt) and the Bugzilla REST API (KDE). Secondary sources were read with a web fetch.

Legend: **[src]** means I read it in the source code myself. **[doc]** means it comes from official documentation or a bug tracker. **[inferred]** is my own reasoning, not verified.

Background, for reference (our measurements). With the Wacom driver on Windows 11 and a mixed-DPI desktop, `lcSysOrg/lcSysExt` from `WTI_DEFSYSCTX` do not match the physical desktop. When the tablet is mapped to one display, WinTab positions equal physical × (primary DPI ÷ lowest DPI). When it is mapped to all displays, some regions are rescaled in a way that isn't uniform. WM_POINTER is always correct. Clip Studio Paint shows the offset; Krita does not.

---
## What our own measurements say about these approaches

These are from the 24-step mapping-wizard session in `testdata/mapping-wizard-2026-09-28/` (Wacom driver, Windows 11, a 3840×2160 monitor above a 2560×1600 one). They change how the approaches below should be read:

- **Raw tablet counts are distorted too.** Qt, Krita, Blender and GTK all ask the driver for raw counts (`lcOut = lcIn`) and scale them themselves, expecting that to get around the driver's pixel output. On this driver it doesn't. In step 1 (mapped to monitor 1, 250% above 225%), the full 50800-count width landed on **3457** px of a 3840 px monitor, the same 0.9 factor as the system context. With equal scaling (steps 7 and 19) it landed on exactly 3840. Counts outside `lcInExt` also arrived (up to 56195 of a stated 50800).
- **`lcSys` doesn't depend on the caller's DPI awareness.** `WTI_DEFSYSCTX` read the same (3840×4178) from Unaware, System-aware, Per-Monitor and Per-Monitor-v2 processes. Whether the *packets* depend on it hasn't been tested.
- **`lcSys` is not the space the positions are in.** In the first measurements, a system context sent X values up to 4241 against a reported `lcSysExtX` of 3840. So "map raw counts onto `lcSys`" (Krita 4.2+, Blender) also inherits the distortion, and the driver's reported screen can't be used to undo it.
- **The cursor is always right**, and so is Windows Ink (WM_POINTER). Every approach that survives on this rig does so by falling back to the cursor (Qt's MouseMode, Blender's trust gate) and gives up sub-pixel precision to do it.

---


## 1. Qt (qtbase, `src/plugins/platforms/windows/qwindowstabletsupport.cpp`)

Files read: `dev` at `abd78e6d005f` ([link](https://github.com/qt/qtbase/blob/abd78e6d005fafa1f54b78c9043717ce80923846/src/plugins/platforms/windows/qwindowstabletsupport.cpp)) and `5.15` at `c68297f66533` ([link](https://github.com/qt/qtbase/blob/5.15/src/plugins/platforms/windows/qwindowstabletsupport.cpp)). The mapping logic is the same in both. In Qt 5.12+ and Qt 6, WM_POINTER is the default. WinTab is opt-in: in Qt 6 through `QWindowsApplication::setWinTabEnabled(true)` [src, `qwindowsapplication.cpp`], and in Qt 5.15 through `Qt::AA_MSWindowsUseWinTabAPI`.

**Context setup [src]** (dev L248-255). It starts from `WTI_DEFSYSCTX`, then replaces the output range with the raw tablet input range, intending the driver to do no scaling (on our rig it still does; see above):
```cpp
QWindowsTabletSupport::m_winTab32DLL.wTInfo(WTI_DEFSYSCTX, 0, &lcMine);
// Go for the raw coordinates, the tablet event will return good stuff
lcMine.lcOutOrgX = 0;
lcMine.lcOutExtX = lcMine.lcInExtX;
lcMine.lcOutOrgY = 0;
lcMine.lcOutExtY = -lcMine.lcInExtY;
```
`maxX/maxY` come from `WTI_DEFCONTEXT` `lcInExt - lcInOrg` (dev L441-444).

**Position [src]** (dev L591-592, L625-626, L61-76). The full tablet input range is mapped linearly onto the union of all screens' **native** (physical-pixel) geometries. `lcSys*` is never used:
```cpp
const QRect virtualDesktopArea =
    QWindowsScreen::virtualGeometry(QGuiApplication::primaryScreen()->handle());
QPointF globalPosF = current.scaleCoordinates(packet.pkX, packet.pkY, virtualDesktopArea);
```
`QWindowsScreen::virtualGeometry` is `result |= sibling->geometry()` over the platform screens, which are in native pixels. It was added in commit [8abbb6ec72](https://github.com/qt/qtbase/commit/8abbb6ec725c41e08db8421f3e10b15b66ba28b1), "Fix scaling of tablet coordinates for High DPI scaling", 2018. The local position uses `platformWindow->mapFromGlobal`, also native. So the DPI handling amounts to: do everything in physical pixels, and let the per-monitor-aware process see a physical desktop.

**Mouse-mode check [src]** (dev L632-641). This happens once per proximity entry, on the first packet:
```cpp
const QPoint mouseLocation = QWindowsCursor::mousePosition();   // GetCursorPos
if (m_state == PenProximity) {
    m_state = PenDown;
    m_mode = (mouseLocation - globalPosF).manhattanLength() > m_absoluteRange
        ? MouseMode : PenMode;
}
if (m_mode == MouseMode)
    globalPosF = mouseLocation;
```
`m_absoluteRange` defaults to 20 px (L215). It can be changed with the platform option `-platform windows:tabletabsoluterange=N` [src, `qwindowsintegration.cpp` L169]. The source comment admits "there is no way to find out the mode programmatically". History [src/doc]: until 2018 this check ran on **every packet**, which mixed integer cursor positions into strokes ([QTBUG-36937](https://bugreports.qt.io/browse/QTBUG-36937)). Commit [8aeed99d77](https://github.com/qt/qtbase/commit/8aeed99d77b8876d42e19be7123142e5fe2d2a78) made it once per proximity, and [708fa860bc](https://github.com/qt/qtbase/commit/708fa860bc877995256f08cd55bf1421d510e5f3) removed a one-packet delay.

**Consequences for our case [inferred]:**
- **Tablet mapped to one display:** Qt's assumption (full tablet → full virtual desktop) is wrong. The first packet is usually more than 20 px from the cursor, so Qt switches to **MouseMode for the whole proximity session** and uses `GetCursorPos`. Positions end up correct but integer, and follow the cursor. So stock-Qt WinTab apps "work" by accident, and lose sub-pixel precision.
- **Mapped to all displays, with the driver's mixed-DPI distortion:** the first-packet check may pass or fail depending on where the pen enters. If it passes (under 20 px), Qt trusts WinTab for the rest of the stroke, and the offset grows elsewhere.

**Relevant bugs [doc]** (read through the Jira REST API):
- [QTBUG-39579](https://bugreports.qt.io/browse/QTBUG-39579), "Qt Tablet Support doesn't work in multimonitor setup". Tablet mapped to one monitor, tablet events land "as if mapped to two monitors". The reporter proposed using `WTGet` on the opened context instead of the default context. Closed Incomplete.
- [QTBUG-69595](https://bugreports.qt.io/browse/QTBUG-69595), Surface Book plus external monitor. Offset because Qt maps to the whole virtual desktop. Closed "Out of scope": "WinTab code will be superseded by… WM_POINTER messages in 5.12".
- [QTBUG-36937](https://bugreports.qt.io/browse/QTBUG-36937): the per-packet cursor fallback caused integer/jagged points. A Wacom engineer (A. Krebsbach) said in the thread that there is no API to query mouse mode.
- [QTBUG-12593](https://bugreports.qt.io/browse/QTBUG-12593): WinTab ignores Tablet PC calibration. Old, closed.
- I found no QTBUG specifically about Wacom plus mixed per-monitor DPI.

---

## 2. Krita

### 2a. Krita 3.x–4.1: its own WinTab code (`libs/ui/input/wintab/kis_tablet_support_win.cpp`, tag v4.4.8, [link](https://github.com/KDE/krita/blob/v4.4.8/libs/ui/input/wintab/kis_tablet_support_win.cpp))
**[src]** This is a fork of Qt's code. It uses `WTI_DEFSYSCTX` with `lcOut = lcIn` (L488-500), and maps to a rectangle picked through **`KisScreenSizeChoiceDialog`** (L660-686). The dialog compares `QApplication::desktop()->geometry()` against the `lcSysOrg/lcSysExt` of the opened context (`wTGet`). If they differ, it asks the user to pick "Wintab", "Qt" or a manual rect, which can be remembered.
```cpp
QRect qtDesktopRect = QApplication::desktop()->geometry();
QRect wintabDesktopRect(lc.lcSysOrgX, lc.lcSysOrgY, lc.lcSysExtX, lc.lcSysExtY);
... if (modifiers.contains(Qt::Key_Shift) || (!dlg.canUseDefaultSettings() && qtDesktopRect != wintabDesktopRect)) dlg.exec();
result.virtualDesktopArea = dlg.screenRect();
```
It then divides by the **active window's** `devicePixelRatio` (L768-772, L807): `globalPosF /= dpr`. That is only right on uniform DPI [inferred]. It explicitly does **not** use the cursor or mouse mode ("we don't support mouse mode", L800).

### 2b. Krita 4.2+ / 5.x: Qt's WinTab plus Krita's Qt patches
Krita builds its own Qt 5.15 (`invent.kde.org/szaman/qtbase`, pinned `d96d0576…` through `packaging/krita-deps-management` `ext_qt/CMakeLists.txt`). The patches, originally in `3rdparty/ext_qt/` in Krita 4.x:
- `0025-Disable-tablet-relative-mode-in-Qt.patch` [src] forces `m_mode = PenMode`. **The cursor fallback is removed**: "Krita doesn't support mouse mode. And this code may break normal painting".
- `0026-Fetch-mapped-screen-size-from-the-Wintab-driver.patch` [src] ([v4.4.8 link](https://github.com/KDE/krita/blob/v4.4.8/3rdparty/ext_qt/0026-Fetch-mapped-screen-size-from-the-Wintab-driver.patch); same code in the current fork at L420-475). The mapping target becomes `lcSys` from `WTI_DEFSYSCTX`. It is re-read on primary-screen or virtual-geometry change, and re-evaluated on every proximity-enter. There are env-var overrides:
```cpp
QWindowsTabletSupport::m_winTab32DLL.wTInfo(WTI_DEFSYSCTX, 0, &lc);
m_wintabScreenGeometry = QRect(lc.lcSysOrgX, lc.lcSysOrgY, lc.lcSysExtX, lc.lcSysExtY);
...
m_effectiveScreenGeometry = !customGeometry.isValid()
    ? (dontUseWintabDesktopRect ? QWindowsScreen::virtualGeometry(primary) : m_wintabScreenGeometry)
    : customGeometry;           // QT_WINTAB_DESKTOP_RECT="x;y;w;h" / QT_IGNORE_WINTAB_MAPPING=1
```
This came from [KDE bug 406520](https://bugs.kde.org/show_bug.cgi?id=406520), "Qt's WinTab impl may map wrong screen geometry on some devices". On a Surface Pro 5 plus an external monitor, the tablet maps only to the built-in display, and `lcSysExt` was 2736×1824 while Qt's desktop was 3288×1815.
- The UI is **`KisDlgCustomTabletResolution`** [src] ([link](https://github.com/KDE/krita/blob/master/libs/ui/dialogs/KisDlgCustomTabletResolution.cpp)). It has three options: "Use information provided by tablet" (lcSys, the default `wintabResolutionMode=wintab`), "Map to entire virtual screen" (sets `QT_IGNORE_WINTAB_MAPPING`), and "Map to custom area" (sets `QT_WINTAB_DESKTOP_RECT`). The docs page is [Tablet Settings](https://docs.krita.org/en/reference_manual/preferences/tablet_settings.html).
- In Qt 6 builds, `krita/main.cc` L660-663 calls `nativeWindowsApp->setWinTabEnabled(...)`. I could **not** confirm whether Krita's Qt 6 build carries the same lcSys/PenMode patches. If it doesn't, Qt 6 Krita behaves like stock Qt, including the cursor fallback.

**Why Krita might not show our offset [inferred, unverified]:**
1. If the build uses stock Qt 6 WinTab, the MouseMode fallback hides the error.
2. If the build uses the patched Qt 5.15, positions go into the lcSys rect. That is self-consistent *with the driver's own coordinate space*. It is only correct if the driver's lcSys/positions match what that process sees. Qt 5.15 defaults to Per-Monitor **v1**, not v2, and the driver's `wintab32.dll` runs in-process, so `lcSys` may depend on the calling process's DPI awareness. This is worth testing: log `WTI_DEFSYSCTX` from PMv2, PMv1, System-aware and Unaware test processes.
3. The user may have Windows Ink selected in Krita.

Krita's tablet log (Ctrl+Shift+T) prints lcSys, which would settle it.

Other Krita bugs [doc]: [352282](https://bugs.kde.org/show_bug.cgi?id=352282) (dialog mixed up the rects), [373191](https://bugs.kde.org/show_bug.cgi?id=373191) (offset versus Screen Resolution choice on a multi-screen Cintiq), [406996](https://bugs.kde.org/show_bug.cgi?id=406996) (WinTab coordinates relative to the top-left of all displays), [383407](https://bugs.kde.org/show_bug.cgi?id=383407) (no mouse/relative mode, open). Krita maintainers usually blame the driver ([488328 c1](https://bugs.kde.org/show_bug.cgi?id=488328)).

### 2c. Drawpile (bundles Krita's old code) [src]
[`src/desktop/bundled/kis_tablet/kis_tablet_support_win.cpp`](https://github.com/drawpile/Drawpile/blob/c1bba52c0832627e29b484118c9dfd2d2261214f/src/desktop/bundled/kis_tablet/kis_tablet_support_win.cpp):
- Target rect is `lcSys` from `WTI_DEFCONTEXT` (L662-675), with no dialog.
- It divides by the active window's `devicePixelRatioF()`.
- It keeps the Qt 5.9-style **per-packet** cursor snap `if ((QCursor::pos() - globalPos).manhattanLength() > m_absoluteRange)`, with a user toggle `enableRelativePenModeHack` that sets the range to 20 or 0 (L177-181, L806-812).

---

## 3. GIMP / GTK (GDK Win32)

**GTK 2.24 (GIMP 2.10)** [src], [`gdk/win32/gdkinput-win32.c`](https://github.com/GNOME/gtk/blob/gtk-2-24/gdk/win32/gdkinput-win32.c):
- Context from `WTI_DEFSYSCTX`, output = raw axis range (L467-483).
- In `GDK_MODE_SCREEN`, it scales the full axis range to the **root window size**, which is the whole virtual desktop (L710-717). There is no lcSys and no DPI handling.
- GIMP 2.10 is an old GTK2 app. I did not verify its DPI-awareness manifest.

**GTK 3.24 (GIMP 3.x, Inkscape 1.x, MyPaint 2, Xournal++)** [src], [`gdkdevicemanager-win32.c` @62b69a3](https://github.com/GNOME/gtk/blob/gtk-3-24/gdk/win32/gdkdevicemanager-win32.c):
- The default API is **WinPointer**, falling back to WinTab. It can be overridden with `GDK_WIN32_TABLET_INPUT_API=wintab|winpointer|none` (L1486-1512).
- The WinTab context is device-specific `WTI_DSCTXS+devix` if available, else `WTI_DEFSYSCTX`, with `lcOut` set to the raw axis range (L1114-1146).
- The **target window is chosen from the cursor**: `gdk_device_get_window_at_position(core_pointer…)` (L2318).
- The position is `_gdk_device_translate_screen_coord`, which scales the axis range to `gdk_screen_get_width/height`, i.e. the whole virtual screen, minus the window origin ([`gdkdevice.c` L1785-1835](https://github.com/GNOME/gtk/blob/gtk-3-24/gdk/gdkdevice.c#L1785)). So it assumes the tablet is mapped to all displays. There's no lcSys and no mixed-DPI handling.

**WinPointer path, a useful contrast [src]** (L198-219, L1759-1778). GTK computes **sub-pixel** positions from `ptHimetricLocation` using **`GetPointerDeviceRects`**: it gets the device rect (himetric) and the *mapped display rect* (screen pixels), then divides by `window_scale`:
```c
device->origin_x = display_rect.left;
device->scale_x = rect_width (&display_rect) / rect_width (&device_rect);
x_root = device->origin_x + info->ptHimetricLocation.x * device->scale_x;
```

**GTK 4** [src], [`gdkdevice-wintab.c` @5a0d51e](https://github.com/GNOME/gtk/blob/main/gdk/win32/gdkdevice-wintab.c#L159-L175). It maps the tablet range to the **work area of the monitor nearest the window** (`MonitorFromWindow` → `rcWork`), with an open `/* XXX: the dimensions from minfo may need to be scaled for HiDPI usage */`. This is wrong for all-displays mapping and ignores the taskbar [inferred].

**MyPaint, Inkscape, Xournal++**: no WinTab code of their own. They inherit GTK's behavior. A GitHub code search for `wintab`/`WTPacket` in mypaint and xournalpp returned nothing.

---

## 4. Other projects

### Blender (GHOST), the most relevant design [src]
Read [`intern/ghost/intern/GHOST_Wintab.cc`](https://github.com/blender/blender/blob/0ae04d3e782c4f393950d2df9f764fd7688ef3ca/intern/ghost/intern/GHOST_Wintab.cc) and [`GHOST_SystemWin32.cc`](https://github.com/blender/blender/blob/0ae04d3e782c4f393950d2df9f764fd7688ef3ca/intern/ghost/intern/GHOST_SystemWin32.cc). The process is `SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)` (v1, L171).
- **Context:** raw counts, "because some drivers don't handle HIDPI or multi-display correctly" (Wintab.cc L190-196). The target is **lcSys from `WTI_DEFSYSCTX`**, with Y inverted (L198-212). It is re-read on `WM_DISPLAYCHANGE` via `remapCoordinates()` (SystemWin32 L2271-2275).
- **Integer remap:** `inMagnitude * absOutExt / absInExt + out.org` (L482-509). No sub-pixel.
- **Trust gate** (SystemWin32 L960-1045). WinTab positions are *untrusted* until a WinTab button-down matches the Win32 button-down's `msg.pt` within ±1 px:
```cpp
if (PeekMessage(&msg, window->getHWND(), message, message, PM_NOYIELD) ...) {
  useWintabPos = wt->testCoordinates(msg.pt.x, msg.pt.y, info.x, info.y);   // |dx|,|dy| <= 1
  if (!useWintabPos) continue;
```
- While untrusted, **WinTab moves are dropped**, and one cursor-move per WT_PACKET is synthesized at `GetMessagePos()`. That's the fallback. Pressure and tilt still come from WinTab.
- Trust is re-checked on **every** button-down and revoked on focus loss ("Mouse mode of tablet or display layout may change when Wintab or Window is inactive", Wintab.cc L278-280).
- Button events are only issued if an equivalent Win32 button message can be stolen from the queue.
- History [doc]: N. Rishel, [6f158f834dc](https://www.mail-archive.com/bf-blender-cvs@blender.org/msg145495.html), "Refactor of Wintab to use Wintab supplied mouse movement once verified against system input" (D11508, T88852), and ["Manual scaling"](https://www.mail-archive.com/bf-blender-cvs@blender.org/msg144101.html) (2021). Related Blender tasks: T84832 (multi-monitor broken), T85332 (pen display offset).
- **Net effect on our case [inferred]:** on the mixed-DPI desktop the ±1 px test fails, and Blender quietly degrades to cursor positions with WinTab pressure. It is correct but integer, and at message rate.

### OpenToonz / Tahoma2D [src]
There is no WinTab code of their own. They ship a **custom Qt 5.15.2** with the Qt 6 WinTab switch cherry-picked (shun-iwasawa/qtbase `4c4693cf`), per [`notice_about_modified_qt.txt`](https://github.com/opentoonz/opentoonz/blob/master/stuff/doc/LICENSE/notice_about_modified_qt.txt). `toonz/sources/toonz/main.cpp` calls `QWindowsWindowFunctions::setWinTabEnabled(!useQtNativeWinInk)`. So their mapping is **stock Qt's**: virtual-desktop mapping plus the 20 px cursor fallback per proximity.

### Aseprite (laf) [src], [`os/win/wintab.cpp`](https://github.com/aseprite/laf/blob/500768cccc6447e3b7816bedbfcb378a5c5222e5/os/win/wintab.cpp) / [`window.cpp`](https://github.com/aseprite/laf/blob/500768cccc6447e3b7816bedbfcb378a5c5222e5/os/win/window.cpp#L2022-L2028)
- The default "Wintab" mode opens `WTI_DEFSYSCTX` with **`CXO_SYSTEM`**, takes **position from normal mouse messages**, and takes only pressure and pointer type from packets.
- The "WintabPackets" mode trusts the driver-scaled `lcOut` pixels directly: `POINT pos = {pkX, (outBounds.h-1) - pkY}; ScreenToClient(...)`. It does not add `lcOutOrg` [inferred: breaks with negative virtual-desktop origins].

### SDL
I found no WinTab support. SDL uses WM_POINTER/raw input for pens. I did not dig deeper.

### OpenTabletDriver
OpenTabletDriver replaces the vendor driver and does **not** provide WinTab. Output on Windows goes through VMulti/Windows Ink. WinTab support is an open request ([OTD #1230](https://github.com/OpenTabletDriver/OpenTabletDriver/issues/1230)). It's only relevant as a way to take the Wacom driver out of the picture.

### Wacom samples and docs [src/doc]
- [Wintab Basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/) [doc]. It describes the system context as delivering "system pixel data (dpi adjusted)". It gives default mappings: a display tablet maps to its monitor, and an opaque tablet maps to "the entire system desktop (over all monitors)". There is **nothing** on mixed per-monitor DPI, and I found no Wacom FAQ on it. The developer-support site returned 403.
- [ScribbleDemo.CPP](https://github.com/Wacom-Developer/wacom-device-kit-windows/blob/8c8fe264697a2720c2679c046c0c016a18990a45/Wintab%20ScribbleDemo/SampleCode/ScribbleDemo.CPP) [src] is marked "Per Monitor High DPI Aware". It shows two paths:
  - **System context:** it trusts pkX/pkY as screen pixels and just calls `ScreenToClient` (L1047-1052).
  - **Digitizer context:** it maps raw counts itself. The target is the virtual screen `SM_[X|Y|CX|CY]VIRTUALSCREEN` (L655, L667-669, L1091-1095), or for display tablets the **monitor containing the window** (`MonitorFromWindow` → `rcMonitor`, L640-647).
  - It reopens contexts on `WM_DISPLAYCHANGE` (L927-934).
  - Oddity: `/useActualDigitizerOutput` bumps `lcOutExtX++` "to communicate to the driver that we want to use the fixed behavior to get actual tablet output" (L505-510). This is an undocumented driver switch and may be worth trying.
- [PressureTest.cpp](https://github.com/Wacom-Developer/wacom-device-kit-windows/blob/master/Wintab%20Pressure%20Test/SampleCode/PressureTest.cpp) [src] sets `lcOut` to `SM_*VIRTUALSCREEN` (L472-478) with `CXO_SYSTEM`.

---

## 5. General write-ups
- **Clip Studio Paint FAQ** [doc], [20190037](https://support.clip-studio.com/en-us/faq/articles/20190037): "The coordinate of the pen will shift if the value of display text scaling is different". The workarounds are to match scaling, or turn on "Use mouse mode in setting of tablet driver", i.e. the cursor. It says CSP v1.11.0 (Sep 2021) "was deployed to correct this problem". The user reports it's still visible, so either that fix doesn't cover this case or it regressed. CSP also has its own "Operate specified monitor" mapping in Preferences > Tablet.
- **Adobe community** [doc]: several threads about Photoshop WinTab offsets with two screens ([1](https://community.adobe.com/t5/photoshop-ecosystem-discussions/pen-offset-from-cursor-draws-above-or-below-my-cursor-depending-on-height-of-second-monitor/td-p/11003629), [2](https://community.adobe.com/t5/photoshop/brush-offset-from-cursor-from-using-two-screens/td-p/9671637)). The usual fixes are switching WinTab/Ink (`PSUserConfig.txt UseSystemStylus`) or matching scaling. There was no technical root cause.
- **Krita Artists** [doc], ["Draw/Cursor offset with display tablet in dual monitor arrangement"](https://krita-artists.org/t/draw-cursor-offset-with-display-tablet-in-dual-monitor-arrangement/45873): fixed through Krita's tablet-resolution settings (the custom rect).
- **TVPaint forum**, ["Cursor offset while using dual screens"](https://forum.tvpaint.com/viewtopic.php?t=10837): exists, not read in detail.
- I found **no** public write-up that models the Wacom mixed-DPI distortion quantitatively (the primary÷lowest-DPI factor, or the non-uniform all-displays warp). The only hits were our own (WinPenKit #129 and #130, TheSevenPens/PenDynamicsLab#87).

---

## Comparison

| Project | Context / output | Target rect for raw counts | Uses lcSys? | Uses cursor? | DPI / multi-monitor handling | Sub-pixel? |
|---|---|---|---|---|---|---|
| Qt 5.15 / 6 (stock) | DEFSYSCTX, lcOut=lcIn | Union of native screen geometries (full virtual desktop) | No | Yes: at first packet of proximity, if >20 px off → cursor for whole session | Everything in native pixels (PM-aware) | Yes in PenMode; no in MouseMode |
| Krita 3–4.1 (own) | DEFSYSCTX, lcOut=lcIn | User choice: lcSys / Qt desktop / manual (dialog) | Yes (option) | No | ÷ active window DPR | Yes |
| Krita 4.2–5.x (patched Qt 5.15) | same as Qt | lcSys (default), or virtual desktop, or custom rect (env vars / settings dialog) | Yes (default) | No (mouse mode disabled) | Native pixels | Yes |
| Drawpile | DEFSYSCTX, lcOut=lcIn | lcSys (DEFCONTEXT) | Yes | Per-packet snap if >N px (toggle) | ÷ active window DPR | Partly |
| GTK 2 (GIMP 2.10) | DEFSYSCTX, lcOut=axis range | Root window (virtual desktop) | No | Picks target window only | None | Yes |
| GTK 3.24 WinTab (GIMP 3, Inkscape, MyPaint, Xournal++) | DSCTXS/DEFSYSCTX, lcOut=axis range | gdk screen size (virtual desktop) | No | Picks target window only | None (WinPointer is default and correct) | Yes |
| GTK 4 WinTab | same | Work area of the window's monitor | No | — | "XXX may need to be scaled for HiDPI" | Yes |
| Blender | DEFSYSCTX, lcOut=lcIn | lcSys, re-read on WM_DISPLAYCHANGE | Yes | Yes: WinTab trusted only after button-down matches Win32 msg.pt ±1 px; else cursor | PM v1; relies on lcSys ≈ physical | No (integer) |
| OpenToonz/Tahoma2D | stock Qt (backported) | as Qt | No | as Qt | as Qt | as Qt |
| Aseprite | DEFSYSCTX + CXO_SYSTEM | none (mouse msgs) or driver-scaled lcOut | Implicitly | Yes (default mode) | — | No |
| Wacom ScribbleDemo | System ctx or digitizer ctx | System: driver pixels; digitizer: SM_*VIRTUALSCREEN or window's monitor | Implicitly | No | PM-aware; reopen on WM_DISPLAYCHANGE | No |

Nobody models the Wacom mixed-DPI distortion. Projects either trust some rectangle (lcSys, the virtual desktop, or a user rect) or **fall back to the system cursor** when WinTab disagrees with it (Qt, Blender, Aseprite, CSP's "mouse mode").

---

## Ideas relevant to us
1. **Blender-style trust gate, but continuous and self-calibrating.** Pair WinTab positions with cursor positions at moments when both are known and in sync: the WinTab button-down against `msg.pt` of WM_LBUTTONDOWN, and hover samples of `GetCursorPos` or `WM_POINTER`/`WM_MOUSEMOVE` while the pen is still. From those pairs, fit a per-monitor (or piecewise) affine map from WinTab space to physical space. Use it when residuals are small, and fall back to the cursor when they aren't. This keeps WinTab's sub-pixel precision and timing, which Qt and Blender both lose in fallback.
2. **`GetPointerDeviceRects` (the GTK WinPointer approach)** gives the pen device's *mapped display rect in screen pixels* straight from Windows, even while we consume WinTab (if the driver also exposes a Windows Ink pointer device). That answers "one display or all displays, and which one" without trusting `lcSys`. Then `pkX/pkY` in raw counts (lcOut=lcIn) can be mapped onto that physical rect ourselves. This might sidestep the driver's scaled system space entirely, at least for the single-display case.
3. **Use raw counts (lcOut = lcIn) and never the driver's pixel output.** Qt, Krita and Blender all do this. Then the only question is the target rect: physical monitor rect(s) from `EnumDisplayMonitors` (PMv2), not lcSys.
4. **Test whether `lcSys` / packet space depends on the calling process's DPI awareness** (Unaware / System / PMv1 / PMv2). Krita (Qt 5 → PMv1) and Blender (PMv1) trust lcSys and reportedly look fine, while we (PMv2?) see a mismatch. `wintab32.dll` runs in-process, so this is plausible and cheap to test with a tiny probe.
5. **Give users an escape hatch like Krita's**: a "use tablet info / map to entire virtual screen / custom rect" setting, stored per display configuration.
6. **Re-derive the mapping on `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, and on every proximity-enter.** Blender, Wacom's demo and Krita's patch all do some of this. Also revoke trust on focus loss, as Blender does.
7. **Keep the cursor-snap threshold per proximity, not per packet.** Qt and Drawpile learned that per-packet snapping mixes integer cursor points into strokes (QTBUG-36937).
8. Try Wacom's undocumented `lcOutExtX++` "actual digitizer output" switch from ScribbleDemo on the mixed-DPI rig, to see whether it changes the distorted output.
