# Mapping wizard

`WinPenKit.MappingWizard` checks whether each pen API puts the pen where Windows puts the
cursor, across display and tablet configurations. It walks you through a plan one step at a time.

It exists because of issue #129. On a desktop whose monitors were scaled differently, the Wacom
driver sent Wintab positions that were off by up to ~400 px. Nothing the driver reported about
itself showed it. The cursor did.

## Running it

```
dotnet run --project WinPenKit.MappingWizard -c Release
```

Each step says what to set:

1. **Scaling:** as it is now, every monitor at 100%, every monitor at the same scaling above
   100%, or (with two or more monitors) different scalings. You change this yourself in
   Settings > System > Display. If you change the primary monitor's scaling, sign out and back
   in. The wizard offers to carry on with the same session when you open it again.
2. **Tablet mapping:** one monitor, or all displays. You change this yourself in the tablet
   driver's settings. The wizard can't read it, but if it's wrong the targets will be out of
   reach.
3. **Resolution:** native, or the next mode down with the same shape. The wizard sets this for
   you when you press the button. You then get 15 seconds to keep the change before it reverts
   on its own. The change is never saved to Windows' settings, and every resolution the wizard
   changed is put back when it closes.

The wizard checks the scaling and the resolution before measuring, and says what doesn't match.

## Measuring

**Measure** covers each monitor the tablet should reach with a full-screen window showing four
targets. For each pen API in turn (Wintab, Wintab high-res, then WM_Pointer as the Windows Ink
reference), press the pen down on the white circle and keep it pressed until the ring fills. That
takes half a second. The ring only fills while the tip is down, so a quick tap does not count.

- A red dot shows where the current API says the pen is, so a bad mapping is visible right away.
- **S** skips an API, and **Esc** stops the step.
- If an API shows no pen data, lift the pen away and bring it back. A new Wintab context doesn't
  always get packets until the pen re-enters proximity.

The cursor is the reference, not the target or the nib. The driver moves the cursor, and Windows
places it on the physical desktop correctly. Holding the pen down still means the cursor, which is read a
moment after each packet, has caught up with the pen.

## Results

Each session is saved in `Documents\WinPenKit\MappingWizard\<date-time>\`. The files are
rewritten after every step.

- `summary.md`: a pass/fail table for every step and API, followed by a section per step. Each
  section lists the monitors, the system scaling, what the driver claims about its screen, the
  desktop map each Wintab session chose, and the mean error per API and monitor. It also gives
  the line fitted from each API's raw values to the cursor, which is the mapping the driver
  actually applied. Comparing those lines across steps is how a rule is found.
- `samples.csv`: every sample taken during each hold.
- `session.json`: what the wizard reads to resume a session.

A step passes when every target's mean difference from the cursor is at most 3 px on each axis.
