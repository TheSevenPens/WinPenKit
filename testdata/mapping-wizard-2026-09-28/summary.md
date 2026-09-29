# Wintab mapping wizard

Session 20260928-183802. A position passes when its mean difference from the cursor is at most 3 px on each axis; the cursor is where Windows put the pen.

| Step | Configuration | Wintab | Wintab (high-res) | WM_Pointer |
|---|---|---|---|---|
| 1 | scaling as it is; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native) | pass (1.1) | pass (1.6) | pass (1.3) |
| 2 | scaling as it is; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower) | pass (1.0) | pass (1.5) | pass (0.7) |
| 3 | scaling as it is; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native) | pass (1.0) | pass (1.6) | pass (1.4) |
| 4 | scaling as it is; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower) | pass (1.1) | pass (1.6) | pass (0.8) |
| 5 | scaling as it is; tablet mapped to all displays; monitor 1 at 3840x2160 (native) | **FAIL** (978 px) | **FAIL** (978 px) | pass (1.2) |
| 6 | scaling as it is; tablet mapped to all displays; monitor 1 at 3200x1800 (lower) | **FAIL** (605 px) | **FAIL** (603 px) | pass (1.5) |
| 7 | every monitor at 100%; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native) | pass (1.0) | pass (1.4) | pass (1.2) |
| 8 | every monitor at 100%; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower) | pass (0.8) | pass (1.4) | pass (2.2) |
| 9 | every monitor at 100%; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native) | pass (1.0) | pass (1.3) | pass (2.1) |
| 10 | every monitor at 100%; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower) | pass (1.0) | pass (1.4) | pass (2.4) |
| 11 | every monitor at 100%; tablet mapped to all displays; monitor 1 at 3840x2160 (native) | pass (1.0) | pass (1.4) | pass (1.7) |
| 12 | every monitor at 100%; tablet mapped to all displays; monitor 1 at 3200x1800 (lower) | pass (1.0) | pass (1.5) | pass (1.6) |
| 13 | every monitor at the same scaling, above 100%; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native) | pass (1.0) | pass (1.3) | pass (1.4) |
| 14 | every monitor at the same scaling, above 100%; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower) | pass (1.0) | pass (1.4) | pass (0.8) |
| 15 | every monitor at the same scaling, above 100%; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native) | pass (1.0) | pass (1.4) | pass (0.7) |
| 16 | every monitor at the same scaling, above 100%; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower) | pass (1.0) | pass (1.4) | pass (0.9) |
| 17 | every monitor at the same scaling, above 100%; tablet mapped to all displays; monitor 1 at 3840x2160 (native) | pass (1.0) | pass (1.3) | **FAIL** (3 px) |
| 18 | every monitor at the same scaling, above 100%; tablet mapped to all displays; monitor 1 at 3200x1800 (lower) | pass (1.0) | pass (1.5) | pass (2.4) |
| 19 | monitor 1 scaled lower than the others; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native) | **FAIL** (442 px) | **FAIL** (442 px) | pass (1.2) |
| 20 | monitor 1 scaled lower than the others; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower) | **FAIL** (419 px) | **FAIL** (420 px) | pass (1.1) |
| 21 | monitor 1 scaled lower than the others; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native) | **FAIL** (587 px) | **FAIL** (587 px) | pass (0.9) |
| 22 | monitor 1 scaled lower than the others; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower) | **FAIL** (434 px) | **FAIL** (434 px) | pass (1.1) |
| 23 | monitor 1 scaled lower than the others; tablet mapped to all displays; monitor 1 at 3840x2160 (native) | **FAIL** (442 px) | **FAIL** (442 px) | pass (2.3) |
| 24 | monitor 1 scaled lower than the others; tablet mapped to all displays; monitor 1 at 3200x1800 (lower) | **FAIL** (420 px) | **FAIL** (420 px) | **FAIL** (4 px) |

## Step 1: scaling as it is; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 250% scaling, primary, native
- monitor 2 (DISPLAY2) 2560x1600 at (719,2160), 225% scaling, native
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,4178), out ext (3840,4178), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3840,4178) is not the physical desktop (0,0)-(3840,3760); positions scaled by 0.9000, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3840,4178) is not the physical desktop (0,0)-(3840,3760); positions scaled by 0.9000, the ratio of their heights

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | -0.1, 0.3 | 1.1 at target 2 | pass |
| Wintab (high-res) | 1 | 0.4, 0.7 | 1.6 at target 4 | pass |
| WM_Pointer | 1 | 0.1, 0.2 | 1.3 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.900350 x rawX + -0.7, cursorY = 0.900174 x rawY + -0.5
- Wintab (high-res), monitor 1: cursorX = 0.068061 x rawX + -1.3, cursorY = -0.118507 x rawY + 3761.1
- WM_Pointer, monitor 1: cursorX = 0.189020 x rawX + -0.6, cursorY = 0.296334 x rawY + -1.2

## Step 2: scaling as it is; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 225% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 2560x1600 at (719,1800), 225% scaling, native
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3279,3400), out ext (3279,3400), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 225%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.5, 0.2 | 1.0 at target 3 | pass |
| Wintab (high-res) | 1 | 0.8, 0.8 | 1.5 at target 3 | pass |
| WM_Pointer | 1 | 0.0, -0.1 | 0.7 at target 2 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999840 x rawX + -0.2, cursorY = 0.999505 x rawY + 0.3
- Wintab (high-res), monitor 1: cursorX = 0.064548 x rawX + -0.8, cursorY = -0.107042 x rawY + 3398.2
- WM_Pointer, monitor 1: cursorX = 0.161368 x rawX + -0.1, cursorY = 0.267710 x rawY + 0.0

## Step 3: scaling as it is; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 225% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 2560x1600 at (719,1800), 225% scaling, native
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3279,3400), out ext (3279,3400), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 225%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.3, 0.6 | 1.0 at target 2 | pass |
| Wintab (high-res) | 2 | 0.7, 1.1 | 1.6 at target 3 | pass |
| WM_Pointer | 2 | 0.1, 0.0 | 1.4 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 1.000305 x rawX + -0.9, cursorY = 1.000610 x rawY + -2.2
- Wintab (high-res), monitor 2: cursorX = 0.064559 x rawX + -1.1, cursorY = -0.107053 x rawY + 3398.6
- WM_Pointer, monitor 2: cursorX = 0.161338 x rawX + 0.2, cursorY = 0.267328 x rawY + 3.5

## Step 4: scaling as it is; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 225% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 175% scaling, native is 2560x1600
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3393,3857), out ext (3393,3857), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3393,3857) is not the physical desktop (0,0)-(3200,3000); positions scaled by 0.7778, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3393,3857) is not the physical desktop (0,0)-(3200,3000); positions scaled by 0.7778, the ratio of their heights
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 225%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.6, 0.4 | 1.1 at target 3 | pass |
| Wintab (high-res) | 2 | 0.4, 1.1 | 1.6 at target 4 | pass |
| WM_Pointer | 2 | -0.5, 0.3 | 0.8 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 0.777518 x rawX + 0.0, cursorY = 0.778080 x rawY + -1.3
- Wintab (high-res), monitor 2: cursorX = 0.051930 x rawX + 0.3, cursorY = -0.094391 x rawY + 2998.3
- WM_Pointer, monitor 2: cursorX = 0.157502 x rawX + 0.1, cursorY = 0.236197 x rawY + -0.2

## Step 5: scaling as it is; tablet mapped to all displays; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 250% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 175% scaling, native is 2560x1600
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,4800), out ext (3840,4800), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3840,4800) is not the physical desktop (0,0)-(3840,3360); positions scaled by 0.7000, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3840,4800) is not the physical desktop (0,0)-(3840,3360); positions scaled by 0.7000, the ratio of their heights

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | -576.9, -323.0 | 978.4 at target 3 | **FAIL** |
| Wintab | 2 | -252.3, -351.3 | 705.0 at target 2 | **FAIL** |
| Wintab (high-res) | 1 | -576.3, -322.9 | 978.4 at target 3 | **FAIL** |
| Wintab (high-res) | 2 | -250.7, -351.5 | 703.2 at target 2 | **FAIL** |
| WM_Pointer | 1 | 0.3, 0.2 | 1.2 at target 4 | pass |
| WM_Pointer | 2 | 0.2, 0.2 | 0.8 at target 3 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 1.000188 x rawX + -0.7, cursorY = 0.999959 x rawY + -0.1
- Wintab, monitor 2: cursorX = 0.672016 x rawX + 309.4, cursorY = 0.382052 x rawY + 1443.5
- Wintab (high-res), monitor 1: cursorX = 0.075572 x rawX + -0.1, cursorY = -0.151174 x rawY + 4798.8
- Wintab (high-res), monitor 2: cursorX = 0.050691 x rawX + 310.4, cursorY = -0.057868 x rawY + 3277.1
- WM_Pointer, monitor 1: cursorX = 0.188970 x rawX + -0.3, cursorY = 0.264442 x rawY + 0.2
- WM_Pointer, monitor 2: cursorX = 0.188956 x rawX + -0.1, cursorY = 0.264503 x rawY + 0.3

## Step 6: scaling as it is; tablet mapped to all displays; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 225% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 175% scaling, native is 2560x1600
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3393,3857), out ext (3393,3857), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3393,3857) is not the physical desktop (0,0)-(3200,3000); positions scaled by 0.7778, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3393,3857) is not the physical desktop (0,0)-(3200,3000); positions scaled by 0.7778, the ratio of their heights
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 225%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | -354.1, -199.1 | 604.8 at target 3 | **FAIL** |
| Wintab | 2 | -186.7, -220.0 | 521.9 at target 2 | **FAIL** |
| Wintab (high-res) | 1 | -355.1, -198.4 | 603.4 at target 3 | **FAIL** |
| Wintab (high-res) | 2 | -187.0, -219.8 | 520.9 at target 2 | **FAIL** |
| WM_Pointer | 1 | 0.1, -0.1 | 1.5 at target 1 | pass |
| WM_Pointer | 2 | -0.1, 0.0 | 0.4 at target 3 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999769 x rawX + 0.1, cursorY = 0.999465 x rawY + -0.2
- Wintab, monitor 2: cursorX = 0.786240 x rawX + 170.6, cursorY = 0.511431 x rawY + 964.9
- Wintab (high-res), monitor 1: cursorX = 0.066776 x rawX + -0.4, cursorY = -0.121419 x rawY + 3854.6
- Wintab (high-res), monitor 2: cursorX = 0.052542 x rawX + 170.0, cursorY = -0.062008 x rawY + 2936.5
- WM_Pointer, monitor 1: cursorX = 0.157548 x rawX + -0.9, cursorY = 0.236066 x rawY + 0.6
- WM_Pointer, monitor 2: cursorX = 0.157537 x rawX + -0.6, cursorY = 0.236135 x rawY + 0.7

## Step 7: every monitor at 100%; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 100% scaling, native is 2560x1600
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3360), out ext (3840,3360), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.6, 0.4 | 1.0 at target 4 | pass |
| Wintab (high-res) | 1 | 1.1, 0.9 | 1.4 at target 3 | pass |
| WM_Pointer | 1 | -0.0, 0.4 | 1.2 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999894 x rawX + -0.4, cursorY = 0.999823 x rawY + -0.2
- Wintab (high-res), monitor 1: cursorX = 0.075583 x rawX + -0.9, cursorY = -0.105820 x rawY + 3359.0
- WM_Pointer, monitor 1: cursorX = 0.189001 x rawX + -0.3, cursorY = 0.264433 x rawY + 0.1

## Step 8: every monitor at 100%; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 100% scaling, native is 2560x1600
- system scaling 250% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,3000), out ext (3200,3000), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- note: Windows is still using 250% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | -0.0, 0.3 | 0.8 at target 1 | pass |
| Wintab (high-res) | 1 | 0.9, 1.3 | 1.4 at target 1 | pass |
| WM_Pointer | 1 | 0.5, -0.0 | 2.2 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999938 x rawX + 0.1, cursorY = 1.000190 x rawY + -0.4
- Wintab (high-res), monitor 1: cursorX = 0.062974 x rawX + -0.4, cursorY = -0.094484 x rawY + 2998.6
- WM_Pointer, monitor 1: cursorX = 0.157569 x rawX + -1.5, cursorY = 0.236337 x rawY + -0.5

## Step 9: every monitor at 100%; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 2560x1600 at (719,2160), 100% scaling, native
- system scaling 200% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3760), out ext (3840,3760), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3760) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3760) is the physical desktop
- note: Windows is still using 200% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.5, 0.7 | 1.0 at target 1 | pass |
| Wintab (high-res) | 2 | 1.2, 0.9 | 1.3 at target 1 | pass |
| WM_Pointer | 2 | -0.3, 0.1 | 2.1 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 1.000528 x rawX + -1.5, cursorY = 0.999701 x rawY + 0.2
- Wintab (high-res), monitor 2: cursorX = 0.075586 x rawX + -1.0, cursorY = -0.118410 x rawY + 3759.0
- WM_Pointer, monitor 2: cursorX = 0.188747 x rawX + 2.6, cursorY = 0.296223 x rawY + -2.0

## Step 10: every monitor at 100%; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 100% scaling, native is 2560x1600
- system scaling 200% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3360), out ext (3840,3360), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- note: Windows is still using 200% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.4, 0.5 | 1.0 at target 2 | pass |
| Wintab (high-res) | 2 | 1.1, 0.8 | 1.4 at target 2 | pass |
| WM_Pointer | 2 | -0.4, 0.7 | 2.4 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 0.999529 x rawX + 0.4, cursorY = 0.999452 x rawY + 1.0
- Wintab (high-res), monitor 2: cursorX = 0.075595 x rawX + -1.2, cursorY = -0.105782 x rawY + 3358.9
- WM_Pointer, monitor 2: cursorX = 0.189001 x rawX + 0.1, cursorY = 0.263981 x rawY + 5.2

## Step 11: every monitor at 100%; tablet mapped to all displays; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 100% scaling, native is 2560x1600
- system scaling 200% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3360), out ext (3840,3360), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- note: Windows is still using 200% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.4, 0.5 | 1.0 at target 1 | pass |
| Wintab | 2 | 0.3, 0.1 | 1.0 at target 4 | pass |
| Wintab (high-res) | 1 | 0.9, 0.8 | 1.2 at target 1 | pass |
| Wintab (high-res) | 2 | 0.9, 1.1 | 1.4 at target 2 | pass |
| WM_Pointer | 1 | -0.1, 0.5 | 1.6 at target 1 | pass |
| WM_Pointer | 2 | -0.4, 0.1 | 1.7 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 1.000048 x rawX + -0.5, cursorY = 1.000044 x rawY + -0.5
- Wintab, monitor 2: cursorX = 1.000347 x rawX + -0.8, cursorY = 1.000066 x rawY + -0.3
- Wintab (high-res), monitor 1: cursorX = 0.075587 x rawX + -0.8, cursorY = -0.105850 x rawY + 3359.6
- Wintab (high-res), monitor 2: cursorX = 0.075601 x rawX + -1.1, cursorY = -0.105859 x rawY + 3359.1
- WM_Pointer, monitor 1: cursorX = 0.188922 x rawX + 0.6, cursorY = 0.264611 x rawY + -0.8
- WM_Pointer, monitor 2: cursorX = 0.188904 x rawX + 1.0, cursorY = 0.264481 x rawY + 0.5

## Step 12: every monitor at 100%; tablet mapped to all displays; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 100% scaling, native is 2560x1600
- system scaling 200% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,3000), out ext (3200,3000), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- note: Windows is still using 200% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.1, 0.4 | 0.8 at target 4 | pass |
| Wintab | 2 | 0.2, 0.5 | 1.0 at target 4 | pass |
| Wintab (high-res) | 1 | 0.6, 1.1 | 1.4 at target 4 | pass |
| Wintab (high-res) | 2 | 0.6, 1.1 | 1.5 at target 3 | pass |
| WM_Pointer | 1 | -0.6, -0.1 | 1.6 at target 1 | pass |
| WM_Pointer | 2 | 0.1, 0.1 | 1.0 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999885 x rawX + 0.1, cursorY = 1.000078 x rawY + -0.5
- Wintab, monitor 2: cursorX = 0.999897 x rawX + 0.0, cursorY = 0.999508 x rawY + 0.6
- Wintab (high-res), monitor 1: cursorX = 0.062977 x rawX + -0.2, cursorY = -0.094457 x rawY + 2998.2
- Wintab (high-res), monitor 2: cursorX = 0.062960 x rawX + 0.2, cursorY = -0.094480 x rawY + 2998.8
- WM_Pointer, monitor 1: cursorX = 0.157467 x rawX + 0.6, cursorY = 0.235693 x rawY + 2.0
- WM_Pointer, monitor 2: cursorX = 0.157488 x rawX + -0.2, cursorY = 0.236363 x rawY + -1.7

## Step 13: every monitor at the same scaling, above 100%; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 125% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 125% scaling, native is 2560x1600
- system scaling 200% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3360), out ext (3840,3360), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- note: Windows is still using 200% as its system scaling, from when you signed in, but the primary monitor is now at 125%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | -0.2, 0.4 | 1.0 at target 2 | pass |
| Wintab (high-res) | 1 | 0.8, 1.1 | 1.3 at target 1 | pass |
| WM_Pointer | 1 | 0.3, -0.4 | 1.4 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999780 x rawX + 0.6, cursorY = 1.000176 x rawY + -0.6
- Wintab (high-res), monitor 1: cursorX = 0.075589 x rawX + -0.8, cursorY = -0.105839 x rawY + 3359.1
- WM_Pointer, monitor 1: cursorX = 0.188982 x rawX + -0.5, cursorY = 0.264599 x rawY + 0.2

## Step 14: every monitor at the same scaling, above 100%; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 150% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 2560x1600 at (719,1800), 150% scaling, native
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3279,3400), out ext (3279,3400), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 150%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.5, 0.3 | 1.0 at target 3 | pass |
| Wintab (high-res) | 1 | 1.0, 0.9 | 1.4 at target 1 | pass |
| WM_Pointer | 1 | 0.1, 0.2 | 0.8 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999946 x rawX + -0.4, cursorY = 0.999683 x rawY + -0.1
- Wintab (high-res), monitor 1: cursorX = 0.064553 x rawX + -1.2, cursorY = -0.107116 x rawY + 3399.8
- WM_Pointer, monitor 1: cursorX = 0.161371 x rawX + -0.2, cursorY = 0.267785 x rawY + -0.5

## Step 15: every monitor at the same scaling, above 100%; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 150% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 2560x1600 at (719,1800), 150% scaling, native
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3279,3400), out ext (3279,3400), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3279,3400) is the physical desktop
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 150%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.6, 0.3 | 1.0 at target 3 | pass |
| Wintab (high-res) | 2 | 0.9, 1.0 | 1.4 at target 3 | pass |
| WM_Pointer | 2 | 0.2, 0.3 | 0.7 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 0.999849 x rawX + -0.3, cursorY = 1.000228 x rawY + -0.8
- Wintab (high-res), monitor 2: cursorX = 0.064543 x rawX + -0.8, cursorY = -0.107094 x rawY + 3399.1
- WM_Pointer, monitor 2: cursorX = 0.161313 x rawX + 0.4, cursorY = 0.267704 x rawY + -0.4

## Step 16: every monitor at the same scaling, above 100%; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 150% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,3000), out ext (3200,3000), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 150%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 0.4, 0.7 | 1.0 at target 3 | pass |
| Wintab (high-res) | 2 | 0.9, 1.0 | 1.4 at target 2 | pass |
| WM_Pointer | 2 | -0.3, 0.3 | 0.9 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 0.999542 x rawX + 0.3, cursorY = 0.999835 x rawY + -0.3
- Wintab (high-res), monitor 2: cursorX = 0.062989 x rawX + -0.8, cursorY = -0.094536 x rawY + 2999.3
- WM_Pointer, monitor 2: cursorX = 0.157460 x rawX + 0.4, cursorY = 0.236080 x rawY + 0.9

## Step 17: every monitor at the same scaling, above 100%; tablet mapped to all displays; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 150% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,3360), out ext (3840,3360), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3840,3360) is the physical desktop
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 150%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.5, 0.5 | 1.0 at target 1 | pass |
| Wintab | 2 | 0.1, 0.0 | 1.0 at target 4 | pass |
| Wintab (high-res) | 1 | 0.9, 1.0 | 1.3 at target 1 | pass |
| Wintab (high-res) | 2 | 0.6, 0.9 | 1.1 at target 3 | pass |
| WM_Pointer | 1 | -0.5, -1.1 | 3.5 at target 4 | **FAIL** |
| WM_Pointer | 2 | -0.8, 0.3 | 1.4 at target 3 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 1.000014 x rawX + -0.5, cursorY = 1.000002 x rawY + -0.5
- Wintab, monitor 2: cursorX = 0.999057 x rawX + 1.4, cursorY = 0.999951 x rawY + 0.1
- Wintab (high-res), monitor 1: cursorX = 0.075573 x rawX + -0.4, cursorY = -0.105833 x rawY + 3359.2
- Wintab (high-res), monitor 2: cursorX = 0.075533 x rawX + 0.7, cursorY = -0.105811 x rawY + 3359.0
- WM_Pointer, monitor 1: cursorX = 0.189001 x rawX + 0.1, cursorY = 0.264877 x rawY + -0.2
- WM_Pointer, monitor 2: cursorX = 0.189011 x rawX + 0.4, cursorY = 0.264390 x rawY + 1.3

## Step 18: every monitor at the same scaling, above 100%; tablet mapped to all displays; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 150% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,3000), out ext (3200,3000), in ext (50800,31750)
- Wintab desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- Wintab (high-res) desktop map: identity: the driver's screen (0,0)-(3200,3000) is the physical desktop
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 150%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 0.0, 0.0 | 1.0 at target 2 | pass |
| Wintab | 2 | -0.2, 0.5 | 1.0 at target 3 | pass |
| Wintab (high-res) | 1 | 0.6, 1.0 | 1.3 at target 1 | pass |
| Wintab (high-res) | 2 | 0.7, 1.2 | 1.5 at target 1 | pass |
| WM_Pointer | 1 | -0.3, 0.4 | 0.9 at target 1 | pass |
| WM_Pointer | 2 | -0.4, -0.3 | 2.4 at target 3 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999550 x rawX + 0.7, cursorY = 0.999404 x rawY + 0.5
- Wintab, monitor 2: cursorX = 0.999596 x rawX + 0.9, cursorY = 0.998810 x rawY + 2.4
- Wintab (high-res), monitor 1: cursorX = 0.062985 x rawX + -0.5, cursorY = -0.094468 x rawY + 2998.5
- Wintab (high-res), monitor 2: cursorX = 0.062998 x rawX + -0.9, cursorY = -0.094531 x rawY + 2999.1
- WM_Pointer, monitor 1: cursorX = 0.157420 x rawX + 0.8, cursorY = 0.236240 x rawY + -0.5
- WM_Pointer, monitor 2: cursorX = 0.157440 x rawX + 0.7, cursorY = 0.236494 x rawY + -2.6

## Step 19: monitor 1 scaled lower than the others; tablet mapped to monitor 1; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,2960), out ext (3840,2960), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3840,2960) is not the physical desktop (0,0)-(3840,3360); positions scaled by 1.1351, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3840,2960) is not the physical desktop (0,0)-(3840,3360); positions scaled by 1.1351, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 259.3, 146.2 | 442.2 at target 2 | **FAIL** |
| Wintab (high-res) | 1 | 259.5, 146.3 | 442.4 at target 2 | **FAIL** |
| WM_Pointer | 1 | -0.1, -0.3 | 1.2 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999710 x rawX + 0.1, cursorY = 0.999910 x rawY + -0.5
- Wintab (high-res), monitor 1: cursorX = 0.075592 x rawX + -1.1, cursorY = -0.093233 x rawY + 2958.8
- WM_Pointer, monitor 1: cursorX = 0.188863 x rawX + 1.1, cursorY = 0.264386 x rawY + 0.9

## Step 20: monitor 1 scaled lower than the others; tablet mapped to monitor 1; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,2600), out ext (3200,2600), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 244.4, 138.7 | 419.5 at target 3 | **FAIL** |
| Wintab (high-res) | 1 | 246.2, 140.1 | 419.6 at target 3 | **FAIL** |
| WM_Pointer | 1 | 0.1, 0.2 | 1.1 at target 2 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999648 x rawX + 0.5, cursorY = 1.000001 x rawY + -0.5
- Wintab (high-res), monitor 1: cursorX = 0.062970 x rawX + -0.2, cursorY = -0.081811 x rawY + 2598.0
- WM_Pointer, monitor 1: cursorX = 0.157495 x rawX + -0.3, cursorY = 0.236388 x rawY + -0.9

## Step 21: monitor 1 scaled lower than the others; tablet mapped to monitor 2; monitor 2 at 2560x1600 (native)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 2560x1600 at (719,1800), 150% scaling, native
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,2867), out ext (3200,2867), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3200,2867) is not the physical desktop (0,0)-(3279,3400); positions scaled by 1.1859, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3200,2867) is not the physical desktop (0,0)-(3279,3400); positions scaled by 1.1859, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 371.1, 482.7 | 587.1 at target 4 | **FAIL** |
| Wintab (high-res) | 2 | 372.0, 483.4 | 587.4 at target 4 | **FAIL** |
| WM_Pointer | 2 | -0.3, 0.2 | 0.9 at target 1 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 0.999722 x rawX + 0.3, cursorY = 1.000016 x rawY + -0.1
- Wintab (high-res), monitor 2: cursorX = 0.062994 x rawX + -0.6, cursorY = -0.090372 x rawY + 2866.6
- WM_Pointer, monitor 2: cursorX = 0.161325 x rawX + 0.8, cursorY = 0.267481 x rawY + 1.9

## Step 22: monitor 1 scaled lower than the others; tablet mapped to monitor 2; monitor 2 at 1920x1200 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,2600), out ext (3200,2600), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 2 | 258.3, 369.5 | 434.4 at target 3 | **FAIL** |
| Wintab (high-res) | 2 | 259.2, 369.5 | 434.3 at target 4 | **FAIL** |
| WM_Pointer | 2 | -0.5, -0.0 | 1.1 at target 2 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 2: cursorX = 1.000746 x rawX + -1.8, cursorY = 0.999689 x rawY + 0.1
- Wintab (high-res), monitor 2: cursorX = 0.062980 x rawX + -0.2, cursorY = -0.081928 x rawY + 2599.4
- WM_Pointer, monitor 2: cursorX = 0.157471 x rawX + 0.5, cursorY = 0.236244 x rawY + -0.4

## Step 23: monitor 1 scaled lower than the others; tablet mapped to all displays; monitor 1 at 3840x2160 (native)

- monitor 1 (DISPLAY1) 3840x2160 at (0,0), 100% scaling, primary, native
- monitor 2 (DISPLAY2) 1920x1200 at (719,2160), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3840,2960), out ext (3840,2960), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3840,2960) is not the physical desktop (0,0)-(3840,3360); positions scaled by 1.1351, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3840,2960) is not the physical desktop (0,0)-(3840,3360); positions scaled by 1.1351, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 258.1, 144.1 | 441.5 at target 3 | **FAIL** |
| Wintab | 2 | -135.8, 147.0 | 300.1 at target 3 | **FAIL** |
| Wintab (high-res) | 1 | 262.9, 147.0 | 442.1 at target 2 | **FAIL** |
| Wintab (high-res) | 2 | -136.7, 147.4 | 299.2 at target 2 | **FAIL** |
| WM_Pointer | 1 | -0.2, -0.9 | 2.3 at target 4 | pass |
| WM_Pointer | 2 | -0.3, 0.8 | 1.5 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 1.000052 x rawX + -0.8, cursorY = 0.999840 x rawY + -0.1
- Wintab, monitor 2: cursorX = 1.499876 x rawX + -358.9, cursorY = 1.499455 x rawY + -1078.5
- Wintab (high-res), monitor 1: cursorX = 0.075591 x rawX + -0.9, cursorY = -0.093233 x rawY + 2959.0
- Wintab (high-res), monitor 2: cursorX = 0.113340 x rawX + -358.8, cursorY = -0.139832 x rawY + 3359.2
- WM_Pointer, monitor 1: cursorX = 0.188883 x rawX + 1.1, cursorY = 0.264916 x rawY + -0.7
- WM_Pointer, monitor 2: cursorX = 0.189199 x rawX + -1.7, cursorY = 0.264359 x rawY + 1.1

## Step 24: monitor 1 scaled lower than the others; tablet mapped to all displays; monitor 1 at 3200x1800 (lower)

- monitor 1 (DISPLAY1) 3200x1800 at (0,0), 100% scaling, primary, native is 3840x2160
- monitor 2 (DISPLAY2) 1920x1200 at (719,1800), 150% scaling, native is 2560x1600
- system scaling 125% (fixed at sign-in)
- tablet driver: Tablet
- driver's screen: sys org (0,0) ext (3200,2600), out ext (3200,2600), in ext (50800,31750)
- Wintab desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- Wintab (high-res) desktop map: scaled: the driver's screen (0,0)-(3200,2600) is not the physical desktop (0,0)-(3200,3000); positions scaled by 1.1538, the ratio of their heights
- note: Windows is still using 125% as its system scaling, from when you signed in, but the primary monitor is now at 100%. Sign out and back in for a clean result, or carry on to measure this case too; it is recorded either way.

| API | Monitor | Mean error x, y (px) | Worst target | Verdict |
|---|---|---|---|---|
| Wintab | 1 | 246.0, 139.2 | 419.6 at target 2 | **FAIL** |
| Wintab | 2 | 49.0, 155.9 | 362.1 at target 2 | **FAIL** |
| Wintab (high-res) | 1 | 247.8, 138.4 | 420.0 at target 3 | **FAIL** |
| Wintab (high-res) | 2 | -109.8, 139.6 | 265.9 at target 3 | **FAIL** |
| WM_Pointer | 1 | -1.4, -0.8 | 4.0 at target 1 | **FAIL** |
| WM_Pointer | 2 | -0.4, 0.4 | 1.0 at target 4 | pass |

What each API actually did, fitted from its raw values to the cursor:

- Wintab, monitor 1: cursorX = 0.999456 x rawX + 0.7, cursorY = 0.999711 x rawY + 0.1
- Wintab, monitor 2: cursorX = 1.035512 x rawX + 127.6, cursorY = 1.576178 x rawY + -1090.3
- Wintab (high-res), monitor 1: cursorX = 0.062979 x rawX + -0.6, cursorY = -0.081880 x rawY + 2598.8
- Wintab (high-res), monitor 2: cursorX = 0.094416 x rawX + -358.1, cursorY = -0.122855 x rawY + 2999.3
- WM_Pointer, monitor 1: cursorX = 0.157304 x rawX + 3.1, cursorY = 0.235799 x rawY + 2.3
- WM_Pointer, monitor 2: cursorX = 0.157438 x rawX + 0.8, cursorY = 0.236015 x rawY + 1.5

